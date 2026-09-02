#!/usr/bin/env python3
"""assert-align.py - INV-4 keycap digit-centering assertions.

Usage:
    assert-align.py KEYPAD.png [--key-region x0 y0 x1 y1] [opts]

Check (docs/plans/QA-assertions.md sec.4):
    For every keycap, the CENTER OF THE DARK-PIXEL BOUNDING BOX (luma < 150)
    is compared against the keycap bounding-box center; the deviation must be
    < 8px.  Using the dark-pixel bbox center (not the ink-weighted centroid)
    is robust against asymmetric glyphs (1, 7, backspace, check), whose ink
    mass is off-center even when the glyph is correctly centered.  Per-key
    deviations and the maximum are reported.

Thresholds (documented):
    DARK_LIMIT = 150   gray/luma below which a pixel is the digit glyph
    MAX_DEV    = 8     px; bbox-center vs keycap-center deviation limit

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
DARK_LIMIT = 150         # digit block: luma below this counts as the digit
MAX_DEV = 8              # bbox-center vs keycap-center deviation limit (px)
# Noisy glyph pixels (anti-aliased edges, stray shadows) are ignored by using
# the geometric center of the dark-pixel bounding box instead of the ink
# centroid; the bbox is the tight extent of the whole glyph.


def digit_bbox_center(data, w, box):
    """Center (cx, cy), pixel count and bbox of dark pixels inside a keycap.

    Returns ((cx, cy), n, (minx, miny, maxx, maxy)); ((None), 0, None) when no
    dark pixel is present.
    """
    x0, y0, x1, y1 = box
    minx = miny = 1 << 30
    maxx = maxy = -1
    n = 0
    for y in range(y0, y1):
        base = y * w * 3
        for x in range(x0, x1):
            i = base + x * 3
            if (data[i] + data[i + 1] + data[i + 2]) // 3 < DARK_LIMIT:
                n += 1
                if x < minx: minx = x
                if x > maxx: maxx = x
                if y < miny: miny = y
                if y > maxy: maxy = y
    if n == 0:
        return None, 0, None
    cx = (minx + maxx) / 2.0
    cy = (miny + maxy) / 2.0
    return (cx, cy), n, (minx, miny, maxx, maxy)


def main(argv=None):
    p = argparse.ArgumentParser(
        prog="assert-align.py",
        description="INV-4 keycap digit-centering assertions.")
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
        "max_dev_px": MAX_DEV,
    }
    checks = []

    mask = build_gray_mask(data, w, h, x0, y0, x1, y1)
    comps = connected_components(mask, w, h, x0, y0, x1, y1)

    region_area = (x1 - x0) * (y1 - y0)
    min_area = max(200, region_area // 4000)
    comps = [c for c in comps if c[4] >= min_area and c[4] <= region_area * 0.30]
    comps.sort(key=lambda c: c[4], reverse=True)

    if not comps:
        checks.append(Check("A", "FAIL", "no keycap components found in region"))
        emit("assert-align", inputs, checks, args.json)
        return

    keycaps = comps[:EXPECT_KEYS]
    found = len(keycaps)

    deviations = []
    no_digit = []
    for i, box in enumerate(keycaps):
        c, n, _bbox = digit_bbox_center(data, w, (box[0], box[1], box[2], box[3]))
        cx = (box[0] + box[2]) / 2.0
        cy = (box[1] + box[3]) / 2.0
        if c is None:
            no_digit.append(i)
            deviations.append(None)
            continue
        dx = c[0] - cx
        dy = c[1] - cy
        dev = (dx * dx + dy * dy) ** 0.5
        deviations.append(dev)

    numeric = [d for d in deviations if d is not None]
    if not numeric:
        checks.append(Check("A", "FAIL",
                            "no dark digit block found in any keycap"))
        emit("assert-align", inputs, checks, args.json)
        return

    max_dev = max(numeric)
    ok = max_dev < MAX_DEV

    per_key = [
        {"key": i, "dev_px": round(d, 2)} if d is not None
        else {"key": i, "dev_px": None, "missing_digit": True}
        for i, d in enumerate(deviations)
    ]

    detail = "max deviation %.2fpx (limit <%dpx); %d keys measured" % (
        max_dev, MAX_DEV, len(numeric))
    if no_digit:
        detail += "; keys without digit: %s" % no_digit

    checks.append(Check(
        "A", "PASS" if ok else "FAIL", detail,
        extra={"per_key": per_key, "max_dev_px": round(max_dev, 2)}))

    emit("assert-align", inputs, checks, args.json)


if __name__ == "__main__":
    main()
