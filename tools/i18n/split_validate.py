"""Split a flat translation file into Audiora's per-area locale files, validating it against English first.

Usage:  python -I tools/i18n/split_validate.py <code> <flat.json> [--check-only]

<flat.json> is one JSON object with every key of the language (all areas together). The script checks it against
src/SonosFlow.App/Localization/Strings/en/*.json and, only if there are no errors, writes
Strings/<code>/<area>.json with the same areas and key order as English (UTF-8 without BOM, no comments).

Checks: missing / extra keys, the plural categories the language uses (same table as PluralRules.CategoriesOf),
placeholders ({0}, {1:0.#}...) identical to English, balanced braces (string.Format-safe), empty values, and
short labels more than 2.2x longer than English (warning, as in the unit test).
"""
import io, json, os, re, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
STRINGS = os.path.join(ROOT, "src", "SonosFlow.App", "Localization", "Strings")
CATS = ("zero", "one", "two", "few", "many", "other")
PH = re.compile(r"\{(\d+)(:[^{}]*)?\}")


def categories(code):
    lang = code.split("-")[0]
    if lang in ("ja", "ko", "zh"): return ["other"]
    if lang == "ru": return ["one", "few", "many"]
    if lang == "ar": return ["zero", "one", "two", "few", "many", "other"]
    return ["one", "other"]


def load(path):
    text = io.open(path, encoding="utf-8-sig").read()
    text = re.sub(r"^\s*//.*$", "", text, flags=re.M)  # app files may carry // comments
    return json.loads(text)


def placeholders(s):
    return sorted(set(m.group(0) for m in PH.finditer(s)))


def braces_ok(s):
    rest = PH.sub("", s).replace("{{", "").replace("}}", "")
    return "{" not in rest and "}" not in rest


def main():
    if len(sys.argv) < 3:
        print(__doc__); return 2
    code, flat_path = sys.argv[1], sys.argv[2]
    check_only = "--check-only" in sys.argv
    en_dir = os.path.join(STRINGS, "en")
    areas = {}  # area -> ordered en keys
    en = {}
    for name in sorted(os.listdir(en_dir)):
        if name.endswith(".json"):
            table = load(os.path.join(en_dir, name))
            areas[name] = list(table.keys())
            en.update(table)

    plural_bases = {k.rsplit(".", 1)[0] for k in en if k.rsplit(".", 1)[-1] in CATS}
    cats = categories(code)
    expected = []  # (area, key, english reference)
    for area, keys in areas.items():
        seen = set()
        for k in keys:
            base, _, last = k.rpartition(".")
            if base in plural_bases and last in CATS:
                if base in seen: continue
                seen.add(base)
                for c in cats: expected.append((area, f"{base}.{c}", en.get(f"{base}.other") or en[k]))
            else:
                expected.append((area, k, en[k]))

    tr = load(flat_path)
    errors, warnings = [], []
    want = {k for _, k, _ in expected}
    for k in sorted(want - tr.keys()): errors.append(f"missing: {k}")
    for k in sorted(tr.keys() - want): errors.append(f"extra (not in English / wrong plural category): {k}")
    for _, k, ref in expected:
        v = tr.get(k)
        if v is None: continue
        if not isinstance(v, str) or not v.strip(): errors.append(f"empty or not a string: {k}"); continue
        if not braces_ok(v): errors.append(f"stray brace (string.Format would throw): {k}")
        got, need = placeholders(v), placeholders(ref)
        if got != need:
            # zero/one/two forms may spell the number out instead of using {0}
            relaxed = [p for p in need if not p.startswith("{0")]
            if not (k.rsplit(".", 1)[-1] in ("zero", "one", "two") and got == sorted(relaxed)):
                errors.append(f"placeholders {got} != English {need}: {k}")
        if 6 <= len(ref) <= 40 and len(v) > len(ref) * 2.2:
            warnings.append(f"long ({len(v)} vs {len(ref)} chars): {k}")

    for w in warnings: print("WARN ", w)
    for e in errors: print("ERROR", e)
    print(f"{code}: {len(want)} keys expected, {len(tr)} given, {len(errors)} errors, {len(warnings)} warnings")
    if errors or check_only: return 1 if errors else 0

    out_dir = os.path.join(STRINGS, code)
    os.makedirs(out_dir, exist_ok=True)
    for area in areas:
        table = {k: tr[k] for a, k, _ in expected if a == area}
        with io.open(os.path.join(out_dir, area), "w", encoding="utf-8", newline="\r\n") as f:
            f.write(json.dumps(table, ensure_ascii=False, indent=2) + "\n")
    print(f"written: {out_dir} ({len(areas)} files)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
