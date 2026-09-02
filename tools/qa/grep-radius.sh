#!/usr/bin/env bash
# grep-radius.sh - INV-3 anti-regression: ban absolute large radii (>=900px).
#
# Run from the banxia repo root (or anywhere; the script locates the repo).
#   tools/qa/grep-radius.sh [UI_DIR]
#
# Exit 0 = no violating radius, exit 1 = at least one 'border-radius: 9xx px'.

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
UI_DIR="${1:-$REPO_ROOT/Assets/UI}"

PAT_RG='border-radius:[[:space:]]*9[0-9]{2,}px'
PAT_GREP='border-radius:[[:space:]]*9[0-9]{2,}px'

echo "== grep-radius.sh: scan $UI_DIR =="

if [ ! -d "$UI_DIR" ]; then
    echo "ERROR: UI directory not found: $UI_DIR" >&2
    exit 1
fi

# ---- 1) violation scan: absolute large radius (900px .. 999px ..) ----
violations=""
if command -v rg >/dev/null 2>&1; then
    violations="$(rg -n --no-heading -e "$PAT_RG" "$UI_DIR" 2>/dev/null)"
else
    violations="$(grep -rEn "$PAT_GREP" "$UI_DIR" 2>/dev/null)"
fi

echo
echo "-- violation scan (border-radius >= 900px; expect 0) --"
if [ -n "$violations" ]; then
    echo "$violations"
    echo "VIOLATION: found $(echo "$violations" | grep -c .) large-radius line(s)"
else
    echo "none"
fi

# ---- 2) full list of every border-radius line ----
echo
echo "-- all border-radius lines --"
if command -v rg >/dev/null 2>&1; then
    rg -n --no-heading 'border-radius:' "$UI_DIR" 2>/dev/null || echo "(none)"
else
    grep -rEn 'border-radius:' "$UI_DIR" 2>/dev/null || echo "(none)"
fi

# ---- 3) hint table: radius vs same-rule element height ----
echo
echo "-- radius-vs-height hint table (numeric radii; manual review) --"

# Collect .uss files with shell globbing (no external find dependency).
shopt -s nullglob globstar
uss_files=()
for f in "$UI_DIR"/**/*.uss; do
    [[ -f "$f" ]] && uss_files+=("$f")
done
shopt -u nullglob globstar

if [ "${#uss_files[@]}" -eq 0 ]; then
    echo "(no .uss files under $UI_DIR)"
else
awk '
    function trim(s){ gsub(/^[ \t]+|[ \t]+$/,"",s); return s }
    /^[ \t]*\/\*/ { next }
    /^[ \t]*[^/{}][^{}]*\{[ \t]*$/ {
        sel = $0; sub(/\{.*/,"",sel); sel = trim(sel)
        height = ""; radius = ""; radline = ""
        next
    }
    /^[ \t]*\}[ \t]*$/ {
        if (radius != "") {
            hint = "manual"
            if (height != "" && height > 0) {
                h2 = height / 2
                if (radius ~ /^[0-9]+(\.[0-9]+)?$/) {
                    rnum = radius + 0
                    if (rnum == h2) hint = "= h/2 capsule"
                    else if (rnum > h2) hint = "> h/2  WARN"
                    else if (rnum > 48) hint = ">48px  CHECK card"
                    else hint = "<=48px OK"
                } else {
                    hint = "token (manual)"
                }
            }
            printf "  %s:%d  %s  radius=%s  height=%s  [%s]\n", \
                FILENAME, radline, sel, radius, (height==""?"?":height), hint
        }
        next
    }
    /^[ \t]*border-radius:/ {
        r = $0; sub(/^[ \t]*border-radius:[ \t]*/,"",r)
        sub(/px.*/,"",r); sub(/[ \t]*;.*/,"",r)
        radius = trim(r); radline = FNR
    }
    /^[ \t]*height:[ \t]*[0-9]/ {
        hh = $0; sub(/^[ \t]*height:[ \t]*/,"",hh); sub(/px.*/,"",hh)
        height = trim(hh)
    }
' "${uss_files[@]}"
fi

echo

# ---- exit code ----
if [ -n "$violations" ]; then
    echo "RESULT: FAIL (absolute large radius present)"
    exit 1
fi
echo "RESULT: PASS (no absolute large radius)"
exit 0
