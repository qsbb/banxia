#!/usr/bin/env python3
"""assert-shape.py - INV-3 keycap shape assertions.

Usage:
    assert-shape.py KEYPAD.png [--key-region x0 y0 x1 y1] [opts]

Checks (docs/plans/QA-assertions.md sec.3):
    A  keycap localization: connected components of gray in [225,245] within
       the region == keycap bounding boxes (expect 12)
    B  horizontal capsule geometry: scan each keycap's TOP and BOTTOM edge per
       4px column and take the first/last non-white y.  A horizontal capsule
       (radius = height/2) has a flat top and bottom plateau of length W-H; a
       true ellipse curves through every column.  A keycap with no plateau is
       judged an ellipse -> FAIL.
    C  size consistency: width/height coefficient of variation < 5% across
       the keycaps (self-proof of the derived layout)

exit 0 = PASS, exit 1 = FAIL.  --json for machine output.
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from qa_common import (  # noqa: E402
    Check,
    add_common_args,
    build_gray_mask,
    connected_components,
    emit,
    load_rgb,
)

EXPECT_KEYS = 12
WHITE_MIN = 250        # pixel is "white background" when min channel >= this
EDGE_STEP = 4          # px between top/bottom edge samples
PLATEAU_MIN_SPAN = 8   # columns: absolute minimum flat plateau length
PLATEAU_FRAC = 0.4     # plateau must span >= 40% of the capsule flat top W-H
CV_LIMIT = 0.05        # 5% size-consistency limit


def top_edge(data, w, box):
    """[(x, y)] first non-white y per column (ascending x), step EDGE_STEP."""
    x0, y0, x1, y1 = box
    edges = []
    for x in range(x0, x1, EDGE_STEP):
        for y in range(y0, y1):
            i = (y * w + x) * 3
            if min(data[i], data[i + 1], data[i + 2]) < WHITE_MIN:
                edges.append((x, y))
                break
    return edges


def bottom_edge(data, w, box):
    """[(x, y)] last non-white y per column (ascending x), step EDGE_STEP."""
    x0, y0, x1, y1 = box
    edges = []
    for x in range(x0, x1, EDGE_STEP):
        for y in range(y1 - 1, y0 - 1, -1):
            i = (y * w + x) * 3
            if min(data[i], data[i + 1], data[i + 2]) < WHITE_MIN:
                edges.append((x, y))
                break
    return edges


def longest_flat_run(edges):
    """Longest run of consecutive columns with exactly equal edge y.

    A capsule's flat top/bottom edge is a crisp horizontal line, so its edge
    y is identical across the plateau; an ellipse's edge y changes every
    column (no exactly-flat span), which is what rejects true ellipses.
    """
    if not edges:
        return 0
    best = 1
    run = 1
    prev_y = edges[0][1]
    for _, y in edges[1:]:
        run = run + 1 if y == prev_y else 1
        prev_y = y
        if run > best:
            best = run
    return best


def required_plateau_cols(w, h):
    """Minimum plateau length (columns) to call a keycap a horizontal capsule.

    The capsule flat top/bottom is exactly W - H (radius = H/2).  Requiring at
    least PLATEAU_FRAC of that span (plus an absolute floor) rejects ellipses,
    whose "flat-looking" top is far shorter than W - H for wide keycaps.
    """
    flat_px = w - h
    if flat_px <= 0:
        return PLATEAU_MIN_SPAN
    frac_cols = int(round(PLATEAU_FRAC * flat_px / EDGE_STEP))
    return max(PLATEAU_MIN_SPAN, frac_cols)


def coeff_of_variation(values):
    if not values:
        return 0.0
    mean = sum(values) / float(len(values))
    if mean == 0:
        return 0.0
    var = sum((v - mean) ** 2 for v in values) / float(len(values))
    return (var ** 0.5) / mean


def main(argv=None):
    p = argparse.ArgumentParser(
        prog="assert-shape.py",
        description="INV-3 keycap shape assertions.")
    p.add_argument("image", help="keypad screenshot PNG")
    p.add_argument("--key-region", nargs=4, type=int, metavar=("X0", "Y0", "X1", "Y1"),
                   default=None,
                   help="crop to the keypad: x0 y0 x1 y1 (default: whole image)")
    add_common_args(p, with_insets=False)
    args = p.parse_args(argv)

    im, w, h, data = load_rgb(args.image)
    screen_h = args.screen_h if args.screen_h is not None else h

    if args.key_region:
        x0, y0, x1, y1 = args.key_region
    else:
        x0, y0, x1, y1 = 0, 0, w, h
    x0 = max(0, min(x0, w)); y0 = max(0, min(y0, h))
    x1 = max(0, min(x1, w)); y1 = max(0, min(y1, h))
    if x1 <= x0 or y1 <= y0:
        print("error: empty key region", file=sys.stderr)
        sys.exit(1)

    inputs = {
        "image": args.image,
        "screen_h": screen_h,
        "image_h": h,
        "key_region": [x0, y0, x1, y1],
        "expect_keys": EXPECT_KEYS,
        "plateau_min_span": PLATEAU_MIN_SPAN,
        "plateau_frac": PLATEAU_FRAC,
    }
    checks = []

    mask = build_gray_mask(data, w, h, x0, y0, x1, y1)
    comps = connected_components(mask, w, h, x0, y0, x1, y1)

    region_area = (x1 - x0) * (y1 - y0)
    min_area = max(200, region_area // 4000)
    comps = [c for c in comps if c[4] >= min_area and c[4] <= region_area * 0.30]
    comps.sort(key=lambda c: c[4], reverse=True)

    if not comps:
        checks.append(Check("A", "FAIL",
                            "no gray keycap components found in region"))
        checks.append(Check("B", "SKIP", "no keycaps to measure"))
        checks.append(Check("C", "SKIP", "no keycaps to measure"))
        emit("assert-shape", inputs, checks, args.json)
        return

    keycaps = comps[:EXPECT_KEYS]
    found = len(keycaps)

    # A: keycap count
    if found == EXPECT_KEYS:
        checks.append(Check("A", "PASS",
                            "%d keycap components (expect %d)"
                            % (found, EXPECT_KEYS),
                            extra={"component_areas": [c[4] for c in keycaps]}))
    else:
        checks.append(Check("A", "FAIL",
                            "%d keycap components (expect %d)"
                            % (found, EXPECT_KEYS),
                            extra={"component_areas": [c[4] for c in keycaps]}))

    # B: horizontal capsule geometry (flat top/bottom plateau)
    ellipse_keys = []
    per_key = []
    for i, box in enumerate(keycaps):
        bx0, by0, bx1, by1 = box[0], box[1], box[2], box[3]
        kw = bx1 - bx0
        kh = by1 - by0
        req = required_plateau_cols(kw, kh)
        flat_top = longest_flat_run(top_edge(data, w, (bx0, by0, bx1, by1)))
        flat_bottom = longest_flat_run(bottom_edge(data, w, (bx0, by0, bx1, by1)))
        ok = flat_top >= req or flat_bottom >= req
        per_key.append({
            "key": i,
            "flat_top_cols": flat_top,
            "flat_bottom_cols": flat_bottom,
            "required_cols": req,
        })
        if not ok:
            ellipse_keys.append(i)
    if ellipse_keys:
        checks.append(Check(
            "B", "FAIL",
            "%d/%d keycaps judged true ellipse (no top/bottom plateau): %s"
            % (len(ellipse_keys), found, ellipse_keys),
            extra={"ellipse_keycaps": ellipse_keys, "per_key": per_key}))
    else:
        checks.append(Check(
            "B", "PASS",
            "all %d keycaps have a flat top/bottom plateau (horizontal capsule)"
            % found,
            extra={"per_key": per_key}))

    # C: size consistency
    widths = [c[2] - c[0] for c in keycaps]
    heights = [c[3] - c[1] for c in keycaps]
    cv_w = coeff_of_variation(widths)
    cv_h = coeff_of_variation(heights)
    ok = cv_w < CV_LIMIT and cv_h < CV_LIMIT
    checks.append(Check(
        "C", "PASS" if ok else "FAIL",
        "width CV=%.2f%% height CV=%.2f%% (limit <5%%)"
        % (cv_w * 100, cv_h * 100),
        extra={"cv_width_pct": round(cv_w * 100, 2),
               "cv_height_pct": round(cv_h * 100, 2),
               "widths": widths,
               "heights": heights}))

    emit("assert-shape", inputs, checks, args.json)


if __name__ == "__main__":
    main()
