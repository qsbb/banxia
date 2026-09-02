#!/usr/bin/env python3
"""assert-overlay.py - INV-7 HUD framing-grid self-proof (simulator).

Usage:
    assert-overlay.py HUD_GRID_ON.png [opts]

Checks (docs/plans/QA-assertions.md sec.6):
    A  red safe-zone frame present (red pixel rectangular border bands)
    B  green 1/3 line present (green horizontal line band at ~1/3 height)
    C  top-left numeric readout present (dark small text block)
    All three together prove the overlay rendered = framing is observable.

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

MIN_RED = 200
MIN_GREEN = 80
MIN_DARK = 30

# red frame (#FF3B30-like) and green line (#34C759) predicates
def is_red(r, g, b):
    return r >= 200 and g < 120 and b < 120


def is_green(r, g, b):
    return g >= 150 and (g - r) >= 60 and (g - b) >= 50


def is_dark(r, g, b):
    return (r + g + b) // 3 < 100


def red_stats(data, w, h, max_samples=8):
    count = 0
    samples = []
    minx = miny = 1 << 30
    maxx = maxy = -1
    # track presence in top 20% and bottom 20% bands (the two safe-zone rects)
    top_band = set()
    bottom_band = set()
    for y in range(0, h):
        base = y * w * 3
        for x in range(0, w):
            i = base + x * 3
            if is_red(data[i], data[i + 1], data[i + 2]):
                count += 1
                if x < minx: minx = x
                if x > maxx: maxx = x
                if y < miny: miny = y
                if y > maxy: maxy = y
                if y < 0.20 * h:
                    top_band.add(x // 40)
                if y > 0.80 * h:
                    bottom_band.add(x // 40)
                if len(samples) < max_samples:
                    samples.append((x, y))
    top_span = len(top_band)
    bottom_span = len(bottom_band)
    width_span = (maxx - minx + 1) if count else 0
    return count, samples, top_span, bottom_span, width_span, (minx, miny, maxx, maxy)


def green_line_stats(data, w, h, max_samples=8):
    """Green pixels inside the 1/3-line band y in [0.28, 0.38] * h."""
    y0 = int(0.28 * h)
    y1 = int(0.38 * h) + 1
    count = 0
    samples = []
    minx = 1 << 30
    maxx = -1
    sy = 0
    for y in range(y0, y1):
        base = y * w * 3
        for x in range(0, w):
            i = base + x * 3
            if is_green(data[i], data[i + 1], data[i + 2]):
                count += 1
                sy += y
                if x < minx: minx = x
                if x > maxx: maxx = x
                if len(samples) < max_samples:
                    samples.append((x, y))
    span = (maxx - minx + 1) if count else 0
    centroid = (sy / float(count)) if count else 0.0
    return count, samples, span, centroid


def dark_corner_stats(data, w, h, max_samples=8):
    """Dark pixels in the top-left readout corner."""
    x1 = int(0.30 * w)
    y1 = int(0.10 * h)
    count = 0
    samples = []
    for y in range(0, y1):
        base = y * w * 3
        for x in range(0, x1):
            i = base + x * 3
            if is_dark(data[i], data[i + 1], data[i + 2]):
                count += 1
                if len(samples) < max_samples:
                    samples.append((x, y))
    return count, samples


def main(argv=None):
    p = argparse.ArgumentParser(
        prog="assert-overlay.py",
        description="INV-7 HUD framing-grid render self-proof.")
    p.add_argument("image", help="HUD framing-grid screenshot PNG")
    add_common_args(p, with_insets=False)
    args = p.parse_args(argv)

    im, w, h, data = load_rgb(args.image)
    screen_h = args.screen_h if args.screen_h is not None else h

    inputs = {
        "image": args.image,
        "screen_h": screen_h,
        "image_h": h,
    }
    checks = []

    # A: red safe-zone frame
    rc, rsamples, top_span, bottom_span, rwidth, rbbox = red_stats(data, w, h)
    if rc >= MIN_RED and rwidth >= 0.5 * w and top_span >= 3 and bottom_span >= 3:
        checks.append(Check(
            "A", "PASS",
            "red frame present: %d red px, width span %dpx (>=%d), "
            "top/bottom bands both present" % (rc, rwidth, int(0.5 * w)),
            extra={"red_px": rc, "bbox": list(rbbox)}))
    else:
        checks.append(Check(
            "A", "FAIL",
            "red frame weak/missing: %d red px, width span %dpx, top_band=%d "
            "bottom_band=%d" % (rc, rwidth, top_span, bottom_span),
            rsamples,
            extra={"red_px": rc, "bbox": list(rbbox)}))

    # B: green 1/3 line
    gc, gsamples, gspan, gcentroid = green_line_stats(data, w, h)
    if gc >= MIN_GREEN and gspan >= 0.5 * w:
        checks.append(Check(
            "B", "PASS",
            "green 1/3 line present: %d green px, x-span %dpx (>=%d), "
            "centroid y=%.1f" % (gc, gspan, int(0.5 * w), gcentroid),
            extra={"green_px": gc, "x_span": gspan,
                   "centroid_y": round(gcentroid, 1)}))
    else:
        checks.append(Check(
            "B", "FAIL",
            "green 1/3 line weak/missing: %d green px, x-span %dpx"
            % (gc, gspan),
            gsamples,
            extra={"green_px": gc, "x_span": gspan}))

    # C: top-left numeric readout
    dc, dsamples = dark_corner_stats(data, w, h)
    if dc >= MIN_DARK:
        checks.append(Check("C", "PASS",
                            "top-left readout present: %d dark px (>=%d)"
                            % (dc, MIN_DARK),
                            extra={"dark_px": dc}))
    else:
        checks.append(Check("C", "FAIL",
                            "top-left readout weak/missing: %d dark px (<%d)"
                            % (dc, MIN_DARK),
                            dsamples,
                            extra={"dark_px": dc}))

    emit("assert-overlay", inputs, checks, args.json)


if __name__ == "__main__":
    main()
