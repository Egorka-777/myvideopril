#!/usr/bin/env python3
"""Extract YOUTUBE_TITLE_BANKS / YOUTUBE_PAIRS from Antikhaos HTML into youtube-title-banks.json."""
import json
import re
import sys
from pathlib import Path


def extract_braced_object(html: str, name: str) -> str | None:
    marker = name + " ="
    i = html.find(marker)
    if i < 0:
        return None
    i = html.find("{", i)
    if i < 0:
        return None
    depth = 0
    for j in range(i, len(html)):
        c = html[j]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return html[i : j + 1]
    return None


def js_object_to_json(js: str) -> dict:
    # Antikhaos files use JS object literals with double-quoted strings.
    js = re.sub(r",\s*}", "}", js)
    js = re.sub(r",\s*]", "]", js)
    return json.loads(js)


def title_case_en(s: str) -> str:
    small = {"a", "an", "the", "and", "or", "for", "to", "of", "in", "on", "with"}
    words = s.split()
    out = []
    for i, w in enumerate(words):
        lw = w.lower()
        if i > 0 and lw in small:
            out.append(lw)
        elif lw == "ai":
            out.append("AI")
        elif lw in ("po", "pocketoption"):
            out.append(w.capitalize())
        else:
            out.append(w[:1].upper() + w[1:] if w else w)
    return " ".join(out)


def title_case_ru_short(s: str) -> str:
    # Capitalize first letter of each segment separated by | or space for Latin tokens.
    parts = [p.strip() for p in s.split("|")]
    fixed = []
    for p in parts:
        words = p.split()
        nw = []
        for w in words:
            if w.lower() in ("binodex", "ai"):
                nw.append("BinoDex" if w.lower() == "binodex" else "AI")
            elif w.isascii() and w.isalpha():
                nw.append(w[:1].upper() + w[1:].lower())
            else:
                nw.append(w[:1].upper() + w[1:] if w else w)
        fixed.append(" ".join(nw))
    return " | ".join(fixed) if "|" in s else fixed[0]


def expand_shorts(base: list[str], target: int = 100) -> list[str]:
    seen = set()
    out: list[str] = []
    for x in base:
        x = x.strip()
        if x and x not in seen:
            seen.add(x)
            out.append(x)
    if len(out) >= target:
        return out[:target]
    # Extra permutations from token pools (EN/RU mixed detection by first item)
    sample = (out[0] if out else "").lower()
    if "pocket" in sample or "binary" in sample or "trading" in sample:
        pools = [
            ["pocket option", "binary options", "ai trading bot"],
            ["strategy", "signals", "trading"],
            ["pocket option", "ai trading bot", "signals"],
            ["binary options", "strategy", "trading"],
        ]
        extras = []
        for a, b, c in pools:
            for tpl in (
                f"{a} {b} {c}",
                f"{a} | {b} | {c}",
                f"{b} {a} {c}",
            ):
                extras.append(title_case_en(tpl))
        for x in extras:
            if len(out) >= target:
                break
            if x not in seen:
                seen.add(x)
                out.append(x)
    else:
        pools = [
            ["бинодекс", "binodex", "бинарные опционы"],
            ["сигналы", "стратегия", "трейдинг"],
            ["binodex", "бот", "сигналы"],
        ]
        for a, b, c in pools:
            for tpl in (f"{a} {b} {c}", f"{a} | {b} | {c}"):
                t = title_case_ru_short(tpl)
                if t not in seen and len(out) < target:
                    seen.add(t)
                    out.append(t)
    return out[: max(len(base), min(target, len(out)))]


def main() -> int:
    html_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(
        r"C:\Users\hackerman\Downloads\Antikhaos_v36_Short_Titles.html"
    )
    out_path = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).with_name(
        "youtube-title-banks.json"
    )
    html = html_path.read_text(encoding="utf-8", errors="replace")
    banks_js = extract_braced_object(html, "YOUTUBE_TITLE_BANKS")
    pairs_js = extract_braced_object(html, "YOUTUBE_PAIRS")
    if not banks_js or not pairs_js:
        print("Could not find YOUTUBE_TITLE_BANKS or YOUTUBE_PAIRS in", html_path)
        return 1
    banks = js_object_to_json(banks_js)
    pairs = js_object_to_json(pairs_js)

    long_ru = list(banks.get("ru_long") or [])
    long_en = list(banks.get("en_long") or [])
    short_ru_raw = list(pairs.get("ruShort") or pairs.get("ru_short") or [])
    short_en_raw = list(pairs.get("enShort") or pairs.get("en_short") or [])

    short_en = [title_case_en(x) for x in short_en_raw]
    short_ru = [title_case_ru_short(x) for x in short_ru_raw]
    short_en = expand_shorts(short_en, 100)
    short_ru = expand_shorts(short_ru, 100)

    payload = {
        "BundleRevision": 3,
        "TitleBankLongRu": long_ru,
        "TitleBankLongEn": long_en,
        "TitleBankShortsRu": short_ru,
        "TitleBankShortsEn": short_en,
    }
    out_path.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(
        "Wrote",
        out_path,
        "| long RU/EN",
        len(long_ru),
        len(long_en),
        "| shorts RU/EN",
        len(short_ru),
        len(short_en),
    )
    if long_ru:
        print("RU long[0]:", long_ru[0][:90])
    if short_ru:
        print("RU short[0]:", short_ru[0][:90])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
