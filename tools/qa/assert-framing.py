#!/usr/bin/env python3
"""assert-framing.py - INV-1 / INV-2 framing assertions for real-device shots.

Usage:
    assert-framing.py CALL_SCREENSHOT.png [--fullbody FULLBODY.png] [opts]

Checks (docs/plans/QA-assertions.md sec.1):
    A  top bar has zero skin pixels in y in [0, top_px]
    B  eye-line green cross marker centroid y in [0.28, 0.38] * screen_h
       (optional: needs the HUD framing grid; WARN when no marker is found)
    C  fill rate: non-background ratio in y > 0.6*screen_h is > 30%
       (background = dominant four-corner color, tolerance +-30)
    D  full-body strip (only with --fullbody): head in [12,16]% and
       feet in [92,96]% of the full-body image height

exit 0 = PASS, exit 1 = FAIL.  --json for machine output.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from qa_common import (  # noqa: E402
    Check,
    add_common_args,
    corner_background,
    emit,
    is_background,
    is_skin,
    load_rgb,
    resolve_insets,
)

# green marker color used by the HUD framing grid is #34C759 = (52, 199, 89)
GREEN_MIN_PIXELS = 60
GREEN_SEARCH_TOP = 0.15     # exclude the headTop cross from the eye search
GREEN_SEARCH_BOTTOM = 0.55  # exclude the 7/10 green line from the eye search
EYE_BAND = (0.38, 0.46)     # phone call eye line sits below the upper third


def green_marker(r, g, b):
    # "near #34C759": dominant green channel, clearly above r/b
    return g >= 150 and (g - r) >= 50 and (g - b) >= 40


def scan_skin_top(data, w, h, top_px, max_samples=12):
    """Count skin pixels in y in [0, top_px)."""
    top = max(1, min(top_px, h))
    count = 0
    samples = []
    for y in range(0, top):
        base = y * w * 3
        for x in range(0, w):
            i = base + x * 3
            if is_skin(data[i], data[i + 1], data[i + 2]):
                count += 1
                if len(samples) < max_samples:
                    samples.append((x, y))
    return count, samples


def green_centroid(data, w, h, screen_h, max_samples=8):
    """Centroid y of green marker pixels in the eye-line search window."""
    y0 = max(0, int(GREEN_SEARCH_TOP * screen_h))
    y1 = min(h, int(GREEN_SEARCH_BOTTOM * screen_h) + 1)
    count = 0
    sy = 0
    samples = []
    for y in range(y0, y1):
        base = y * w * 3
        for x in range(0, w):
            i = base + x * 3
            if green_marker(data[i], data[i + 1], data[i + 2]):
                count += 1
                sy += y
                if len(samples) < max_samples:
                    samples.append((x, y))
    if count == 0:
        return 0, 0, samples
    return count, sy / count, samples


def fill_rate(data, w, h, screen_h, bg, max_samples=8):
    """Non-background ratio of the region y in (0.6*screen_h, h)."""
    y0 = max(0, int(0.6 * screen_h))
    total = 0
    nonbg = 0
    samples = []
    for y in range(y0, h):
        base = y * w * 3
        for x in range(0, w):
            i = base + x * 3
            total += 1
            if not is_background(data[i], data[i + 1], data[i + 2], bg):
                nonbg += 1
                if len(samples) < max_samples:
                    samples.append((x, y))
    if total == 0:
        return 0.0, samples
    return nonbg / float(total), samples


def content_strip(data, w, h, bg):
    """Top/bottom non-background rows over the central 40% width band."""
    x0 = int(0.30 * w)
    x1 = int(0.70 * w)
    top = None
    bottom = None
    for y in range(0, h):
        base = y * w * 3
        for x in range(x0, x1):
            i = base + x * 3
            if not is_background(data[i], data[i + 1], data[i + 2], bg):
                top = y
                break
        if top is not None:
            break
    for y in range(h - 1, -1, -1):
        base = y * w * 3
        for x in range(x0, x1):
            i = base + x * 3
            if not is_background(data[i], data[i + 1], data[i + 2], bg):
                bottom = y
                break
        if bottom is not None:
            break
    return top, bottom


def main(argv=None):
    p = argparse.ArgumentParser(
        prog="assert-framing.py",
        description="INV-1/INV-2 framing assertions for a call screenshot.")
    p.add_argument("image", help="video-call screenshot PNG")
    p.add_argument("--fullbody", default=None,
                   help="full-body virtual-scene screenshot PNG (enables D)")
    add_common_args(p)
    args = p.parse_args(argv)

    im, w, h, data = load_rgb(args.image)
    screen_h = args.screen_h if args.screen_h is not None else h
    top_px, bottom_px = resolve_insets(screen_h, args.top_px, args.bottom_px)
    top_px = min(top_px, h)
    bottom_px = min(bottom_px, h)

    inputs = {
        "image": args.image,
        "screen_h": screen_h,
        "image_h": h,
        "top_px": top_px,
        "bottom_px": bottom_px,
        "fullbody": args.fullbody,
    }
    notes = []
    if screen_h != h:
        notes.append("screen_h (%d) differs from image height (%d); "
                     "fraction targets use screen_h" % (screen_h, h))

    checks = []

    # A: top bar must be skin-free
    cnt, samples = scan_skin_top(data, w, h, top_px)
    if cnt == 0:
        checks.append(Check("A", "PASS",
                            "0 skin pixels in y in [0,%d)" % top_px))
    else:
        checks.append(Check("A", "FAIL",
                            "%d skin pixels in y in [0,%d)" % (cnt, top_px),
                            samples))

    # B: eye-line green marker centroid (optional HUD)
    gc, gy, gsamples = green_centroid(data, w, h, screen_h)
    lo = EYE_BAND[0] * screen_h
    hi = EYE_BAND[1] * screen_h
    if gc < GREEN_MIN_PIXELS:
        checks.append(Check(
            "B", "WARN",
            "no green cross marker found (%d green px) - HUD framing grid "
            "probably off; skipping (not a fake PASS)" % gc, gsamples))
    elif lo <= gy <= hi:
        checks.append(Check("B", "PASS",
                            "green marker centroid y=%.1f in [%.1f, %.1f] "
                            "(%d px)" % (gy, lo, hi, gc), gsamples))
    else:
        checks.append(Check("B", "FAIL",
                            "green marker centroid y=%.1f outside [%.1f, %.1f] "
                            "(%d px)" % (gy, lo, hi, gc), gsamples))

    # C: fill rate below 60% line
    bg = corner_background(data, w, h)
    rate, fsamples = fill_rate(data, w, h, screen_h, bg)
    if rate > 0.30:
        checks.append(Check("C", "PASS",
                            "fill rate below 0.6*h = %.1f%% (>30%%)" % (rate * 100)))
    else:
        checks.append(Check("C", "FAIL",
                            "fill rate below 0.6*h = %.1f%% (<=30%%)" % (rate * 100),
                            fsamples))

    # D: full-body strip (optional)
    if args.fullbody:
        _, fw, fh, fdata = load_rgb(args.fullbody)
        fbg = corner_background(fdata, fw, fh)
        ftop, fbottom = content_strip(fdata, fw, fh, fbg)
        if ftop is None or fbottom is None:
            checks.append(Check("D", "WARN",
                                "no content strip found in full-body image"))
        else:
            head_pct = 100.0 * ftop / fh
            feet_pct = 100.0 * fbottom / fh
            ok = (12.0 <= head_pct <= 16.0) and (92.0 <= feet_pct <= 96.0)
            checks.append(Check(
                "D", "PASS" if ok else "FAIL",
                "head=%.1f%% feet=%.1f%% (want 12-16%% / 92-96%%)"
                % (head_pct, feet_pct),
                extra={"head_pct": round(head_pct, 2),
                       "feet_pct": round(feet_pct, 2)}))
    else:
        checks.append(Check("D", "SKIP", "no --fullbody given"))

    if notes:
        for n in notes:
            checks.append(Check("note", "WARN", n))

    emit("assert-framing", inputs, checks, args.json)


if __name__ == "__main__":
    main()
