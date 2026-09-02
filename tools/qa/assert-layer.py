#!/usr/bin/env python3
"""assert-layer.py - INV-5 sheet-layer assertions.

Usage:
    assert-layer.py SHEET_OPEN.png [SHEET_CLOSED.png] [opts]

Checks (docs/plans/QA-assertions.md sec.2):
    A  control yielding: red-ish pixels (r>200, g<110, b<110) in the control
       band y in [2660,2900] (scaled to screen) == 0
    B  mask dimming: with a closed-state screenshot, each R/G/B channel mean
       of the call-picture band (upper 60% of screen) is <= 60% of the closed
       state.  Per-channel comparison is color-space tolerant: the scrim
       rgba(0,0,0,0.4) scales every channel by 0.6, so no luminance weighting
       (Rec.709 vs linear vs sRGB) is assumed.
    C  closed-state reference: the closed screenshot has > 100 red-ish pixels
       in the control band (proves the reference is valid)

exit 0 = PASS, exit 1 = FAIL.  --json for machine output.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from qa_common import (  # noqa: E402
    Check,
    add_common_args,
    emit,
    load_rgb,
)

# control band, calibrated for a 3200-tall screen; scale to the real screen
CONTROL_BAND = (2660, 2900)
CONTROL_BAND_BASE_H = 3200
# the scrim dims the call-picture area above the sheet; the sheet starts at
# ~66% of screen height (M2 evidence: y2135..3200), so compare the upper 60%.
DIM_REGION_FRACTION = 0.60
CLOSED_REF_MIN_RED = 100


def is_red(r, g, b):
    return r > 200 and g < 110 and b < 110


def count_red_in_band(data, w, h, y0, y1, max_samples=8):
    y0 = max(0, min(y0, h))
    y1 = max(0, min(y1, h))
    count = 0
    samples = []
    for y in range(y0, y1):
        base = y * w * 3
        for x in range(0, w):
            i = base + x * 3
            if is_red(data[i], data[i + 1], data[i + 2]):
                count += 1
                if len(samples) < max_samples:
                    samples.append((x, y))
    return count, samples


def mean_rgb_band(data, w, h, y0, y1):
    """Mean (r, g, b) over a horizontal band; color-space tolerant."""
    y0 = max(0, min(y0, h))
    y1 = max(0, min(y1, h))
    sr = sg = sb = 0
    n = 0
    for y in range(y0, y1):
        base = y * w * 3
        for x in range(0, w):
            i = base + x * 3
            sr += data[i]
            sg += data[i + 1]
            sb += data[i + 2]
            n += 1
    if n == 0:
        return (0.0, 0.0, 0.0)
    return (sr / float(n), sg / float(n), sb / float(n))


def band_for(h):
    scale = h / float(CONTROL_BAND_BASE_H)
    return int(round(CONTROL_BAND[0] * scale)), int(round(CONTROL_BAND[1] * scale))


def main(argv=None):
    p = argparse.ArgumentParser(
        prog="assert-layer.py",
        description="INV-5 sheet-layer assertions.")
    p.add_argument("image", help="sheet-open screenshot PNG")
    p.add_argument("closed", nargs="?", default=None,
                   help="sheet-closed screenshot PNG (enables B, C)")
    add_common_args(p, with_insets=False)
    args = p.parse_args(argv)

    im, w, h, data = load_rgb(args.image)
    screen_h = args.screen_h if args.screen_h is not None else h
    y0, y1 = band_for(screen_h)

    inputs = {
        "image": args.image,
        "closed": args.closed,
        "screen_h": screen_h,
        "image_h": h,
        "control_band": [y0, y1],
        "dim_region_fraction": DIM_REGION_FRACTION,
    }
    checks = []

    # A: red pixels must vanish in the control band while the sheet is open
    cnt, samples = count_red_in_band(data, w, h, y0, y1)
    if cnt == 0:
        checks.append(Check("A", "PASS",
                            "0 red pixels in control band y in [%d,%d)" % (y0, y1)))
    else:
        checks.append(Check("A", "FAIL",
                            "%d red pixels in control band y in [%d,%d)"
                            % (cnt, y0, y1), samples))

    # B / C: need the closed-state reference
    if args.closed is None:
        checks.append(Check("B", "WARN",
                            "no closed-state screenshot given; mask dimming not "
                            "checked (not a fake PASS)"))
        checks.append(Check("C", "SKIP", "no closed-state screenshot given"))
    else:
        cim, cw, ch, cdata = load_rgb(args.closed)
        c_screen_h = ch
        cy0, cy1 = band_for(c_screen_h)

        # C: closed reference must actually show the red controls
        ccnt, csamples = count_red_in_band(cdata, cw, ch, cy0, cy1)
        if ccnt > CLOSED_REF_MIN_RED:
            checks.append(Check("C", "PASS",
                                "closed reference has %d red pixels (>%d)"
                                % (ccnt, CLOSED_REF_MIN_RED)))
        else:
            checks.append(Check("C", "WARN",
                                "closed reference has only %d red pixels (<=%d); "
                                "reference may be invalid, review B"
                                % (ccnt, CLOSED_REF_MIN_RED), csamples))

        # B: dimming - each channel of the upper call-picture band must be
        # scaled to <= 60% of the closed state (scrim rgba(0,0,0,0.4)).
        dy1 = int(DIM_REGION_FRACTION * h)
        cdy1 = int(DIM_REGION_FRACTION * ch)
        open_rgb = mean_rgb_band(data, w, h, 0, dy1)
        closed_rgb = mean_rgb_band(cdata, cw, ch, 0, cdy1)
        # per-channel ratio; +2.0 abs slack absorbs capture noise
        ok_channels = []
        ratios = []
        for name, oc, cc in zip(("r", "g", "b"), open_rgb, closed_rgb):
            if cc <= 0:
                ok_channels.append(False)
                ratios.append(None)
            else:
                ratios.append(oc / cc)
                ok_channels.append(oc <= 0.60 * cc + 2.0)
        if all(cc <= 0 for cc in closed_rgb):
            checks.append(Check("B", "WARN",
                                "closed reference band is black; cannot judge "
                                "dimming"))
        elif all(ok_channels):
            checks.append(Check(
                "B", "PASS",
                "open band RGB (%.1f, %.1f, %.1f) <= 60%% of closed "
                "(%.1f, %.1f, %.1f)"
                % (open_rgb + closed_rgb),
                extra={"open_rgb": [round(v, 2) for v in open_rgb],
                       "closed_rgb": [round(v, 2) for v in closed_rgb],
                       "ratios": [round(r, 3) if r is not None else None
                                  for r in ratios]}))
        else:
            checks.append(Check(
                "B", "FAIL",
                "open band RGB (%.1f, %.1f, %.1f) not all <= 60%% of closed "
                "(%.1f, %.1f, %.1f)"
                % (open_rgb + closed_rgb),
                extra={"open_rgb": [round(v, 2) for v in open_rgb],
                       "closed_rgb": [round(v, 2) for v in closed_rgb],
                       "ratios": [round(r, 3) if r is not None else None
                                  for r in ratios]}))

    emit("assert-layer", inputs, checks, args.json)


if __name__ == "__main__":
    main()
