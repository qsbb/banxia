"""Shared helpers for the banxia QA assertion scripts (INV-7 loop).

Runtime: /data/dsh/home/dsh/bridge-venv/bin/python + Pillow.  Pure ASCII.

Conventions (see docs/plans/QA-assertions.md):
  * exit 0 = PASS, exit 1 = FAIL
  * WARN = an optional input is missing / a check could not run honestly
    (WARN is never a fake PASS, and it does not force exit 1 on its own)
  * --json emits one machine-readable JSON object on stdout
  * shared skin mask (validated in-session):
        r>235 and 195<=g<=240 and 175<=b<=225 and r-b>25
"""

import json
import sys

from PIL import Image


# ---- pixel primitives -----------------------------------------------------

def is_skin(r, g, b):
    """Shared skin-tone mask predicate."""
    return r > 235 and 195 <= g <= 240 and 175 <= b <= 225 and (r - b) > 25


def load_rgb(path):
    """Open an image as RGB and return (Image, width, height, raw RGB bytes).

    ``data`` is row-major, 3 bytes per pixel: data[(y*W + x)*3 + c].
    """
    im = Image.open(path)
    if im.mode != "RGB":
        im = im.convert("RGB")
    im.load()
    w, h = im.size
    return im, w, h, im.tobytes()


def add_common_args(parser, with_insets=True):
    parser.add_argument(
        "--screen-h", type=int, default=None,
        help="logical screen height in px (default: auto = image height)")
    if with_insets:
        parser.add_argument(
            "--top-px", type=int, default=None,
            help="top bar bottom edge, absolute px (default 330 scaled to screen)")
        parser.add_argument(
            "--bottom-px", type=int, default=None,
            help="bottom controls top edge, absolute px (default 2640 scaled to screen)")
    parser.add_argument(
        "--json", action="store_true",
        help="emit machine-readable JSON instead of a human report")


def resolve_insets(screen_h, top_px, bottom_px):
    """Resolve effective top/bottom inset pixels.

    Defaults 330 / 2640 are calibrated for a 3200-tall screen and are scaled
    to the actual screen height; an explicit CLI value is used verbatim
    (already absolute px).
    """
    top = top_px if top_px is not None else round(330 * screen_h / 3200)
    bottom = bottom_px if bottom_px is not None else round(2640 * screen_h / 3200)
    return top, bottom


# ---- keycap detection (INV-3/INV-4 shared) --------------------------------

GRAY_LO = 225          # keycap fill gray band (theme --glass over white card)
GRAY_HI = 245


def gray_of(data, w, x, y):
    i = (y * w + x) * 3
    return (data[i] + data[i + 1] + data[i + 2]) // 3


def build_gray_mask(data, w, h, x0, y0, x1, y1):
    """Bytearray mask (w*h) marking gray keycap pixels inside the region."""
    mask = bytearray(w * h)
    for y in range(y0, y1):
        base = y * w * 3
        for x in range(x0, x1):
            i = base + x * 3
            g = (data[i] + data[i + 1] + data[i + 2]) // 3
            if GRAY_LO <= g <= GRAY_HI:
                mask[y * w + x] = 1
    return mask


def connected_components(mask, w, h, x0, y0, x1, y1):
    """4-connected components of a mask, as (x0, y0, x1, y1, area)."""
    visited = bytearray(w * h)
    comps = []
    for y in range(y0, y1):
        for x in range(x0, x1):
            idx = y * w + x
            if not mask[idx] or visited[idx]:
                continue
            visited[idx] = 1
            stack = [(x, y)]
            minx = maxx = x
            miny = maxy = y
            area = 0
            while stack:
                cx, cy = stack.pop()
                area += 1
                if cx < minx:
                    minx = cx
                if cx > maxx:
                    maxx = cx
                if cy < miny:
                    miny = cy
                if cy > maxy:
                    maxy = cy
                for nx, ny in ((cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1)):
                    if x0 <= nx < x1 and y0 <= ny < y1:
                        ni = ny * w + nx
                        if mask[ni] and not visited[ni]:
                            visited[ni] = 1
                            stack.append((nx, ny))
            comps.append((minx, miny, maxx + 1, maxy + 1, area))
    return comps


# ---- background detection -------------------------------------------------

def corner_background(data, w, h, block=6, tol=30):
    """Return (r, g, b) dominant color sampled from the four screen corners."""
    from collections import Counter

    cnt = Counter()
    corners = ((0, 0), (w - block, 0), (0, h - block), (w - block, h - block))
    for cx, cy in corners:
        for y in range(cy, cy + block):
            base = y * w * 3
            for x in range(cx, cx + block):
                i = base + x * 3
                cnt[(data[i], data[i + 1], data[i + 2])] += 1
    return cnt.most_common(1)[0][0]


def is_background(r, g, b, bg, tol=30):
    """True when the pixel is within +-tol of the background color per channel."""
    return (abs(r - bg[0]) <= tol and abs(g - bg[1]) <= tol
            and abs(b - bg[2]) <= tol)


# ---- reporting ------------------------------------------------------------

class Check(object):
    __slots__ = ("key", "status", "detail", "samples", "extra")

    def __init__(self, key, status, detail="", samples=None, extra=None):
        self.key = key
        self.status = status            # PASS | FAIL | WARN | SKIP
        self.detail = detail
        self.samples = samples or []
        self.extra = extra or {}


def emit(script, inputs, checks, json_out):
    """Print a human/JSON report and exit 1 iff any check FAILed."""
    overall = "FAIL"
    if not any(c.status == "FAIL" for c in checks):
        if any(c.status == "PASS" for c in checks):
            overall = "PASS"
        else:
            overall = "WARN"

    if json_out:
        payload = {
            "script": script,
            "inputs": inputs,
            "overall": overall,
            "checks": [
                {
                    "key": c.key,
                    "status": c.status,
                    "detail": c.detail,
                    "samples": c.samples,
                    **c.extra,
                }
                for c in checks
            ],
        }
        print(json.dumps(payload, ensure_ascii=True))
    else:
        print("[%s] %s" % (script, overall))
        for c in checks:
            line = "  [%s] %s" % (c.status, c.key)
            if c.detail:
                line += ": " + c.detail
            print(line)
            if c.samples:
                shown = c.samples[:8]
                print("        samples(x,y): %s%s" % (
                    ", ".join("(%d,%d)" % (x, y) for x, y in shown),
                    " ..." if len(c.samples) > 8 else ""))

    sys.exit(1 if overall == "FAIL" else 0)
