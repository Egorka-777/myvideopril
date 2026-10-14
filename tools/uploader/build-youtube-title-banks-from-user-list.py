#!/usr/bin/env python3
"""Build youtube-title-banks.json from pasted user list (_user_titles_raw.txt)."""
import json
import re
from itertools import permutations
from pathlib import Path

ROOT = Path(__file__).resolve().parent
RAW = ROOT / "_user_titles_raw.txt"
OUT = ROOT / "youtube-title-banks.json"
BUNDLE_REVISION = 3


def title_case_en(s: str) -> str:
    small = {"a", "an", "the", "and", "or", "for", "to", "of", "in", "on", "with", "&"}
    words = s.split()
    out = []
    for i, w in enumerate(words):
        core = w.strip("&")
        lw = core.lower()
        if i > 0 and lw in small:
            out.append(w.lower() if w.islower() else w)
        elif lw == "ai":
            out.append("AI")
        else:
            out.append(core[:1].upper() + core[1:] if core else w)
    return " ".join(out)


def title_case_ru_short(s: str) -> str:
    words = s.split()
    out = []
    for w in words:
        lw = w.lower()
        if lw == "binodex":
            out.append("BinoDex")
        elif lw == "бинодекс":
            out.append("Бинодекс")
        elif lw == "ai":
            out.append("AI")
        else:
            out.append(w)
    if out:
        out[0] = out[0][:1].upper() + out[0][1:] if out[0] else out[0]
    return " ".join(out)


def parse_numbered_block(text: str, start_marker: str, stop_markers: list[str]) -> list[str]:
    i = text.find(start_marker)
    if i < 0:
        return []
    i = text.find("\n", i) + 1
    end = len(text)
    for m in stop_markers:
        j = text.find(m, i)
        if j >= 0:
            end = min(end, j)
    block = text[i:end]
    lines = []
    for line in block.splitlines():
        line = line.strip()
        m = re.match(r"^\d+\.\s*(.+)$", line)
        if m:
            lines.append(m.group(1).strip())
    return lines


def parse_long_ru(text: str) -> list[str]:
    i = text.find("BinoDex торговля с нуля")
    if i < 0:
        return []
    j = text.find("Мне нужно, чтобы ты", i)
    if j < 0:
        j = len(text)
    block = text[i:j]
    titles = []
    for line in block.splitlines():
        line = line.strip()
        if "|" in line:
            titles.append(line)
    return titles


def expand_en_shorts(base: list[str], target: int = 100) -> list[str]:
    seen = {x.lower(): x for x in base}
    tokens = [
        "pocket option",
        "binary options",
        "ai trading bot",
        "strategy",
        "signals",
        "trading",
    ]
    # permutations of 3 distinct tokens from pool
    pool = ["pocket option", "binary options", "ai trading bot", "strategy", "signals", "trading"]
    for a, b, c in permutations(pool, 3):
        t = title_case_en(f"{a} {b} {c}")
        if t.lower() not in seen:
            seen[t.lower()] = t
        if len(seen) >= target:
            break
    out = list(seen.values())
    # preserve base order first
    ordered = []
    seen2 = set()
    for x in base:
        t = title_case_en(x)
        if t.lower() not in seen2:
            seen2.add(t.lower())
            ordered.append(t)
    for x in out:
        if x.lower() not in seen2:
            seen2.add(x.lower())
            ordered.append(x)
    return ordered[:target]


def expand_ru_shorts(base: list[str], target: int = 100) -> list[str]:
    seen = {x.lower(): x for x in base}
    pool = ["бинодекс", "binodex", "бинарные опционы", "сигналы", "стратегия", "бот"]
    for a, b, c in permutations(pool, 3):
        tpl = f"{a} {b} {c}"
        t = title_case_ru_short(tpl)
        if t.lower() not in seen:
            seen[t.lower()] = t
        if len(seen) >= target:
            break
    ordered = []
    seen2 = set()
    for x in base:
        t = title_case_ru_short(x)
        if t.lower() not in seen2:
            seen2.add(t.lower())
            ordered.append(t)
    for x in seen.values():
        if x.lower() not in seen2:
            seen2.add(x.lower())
            ordered.append(x)
    return ordered[:target]


def main() -> None:
    text = RAW.read_text(encoding="utf-8")
    en_short = parse_numbered_block(
        text,
        "🇺🇸 English — 40 combinations",
        ["🇷🇺 Русские", "40 новых английских"],
    )
    ru_short = parse_numbered_block(
        text,
        "🇷🇺 Русские — 40 связок",
        ["40 новых английских", "BinoDex торговля"],
    )
    en_long = parse_numbered_block(
        text,
        "40 новых английских заголовков",
        ["BinoDex торговля с нуля"],
    )
    ru_long = parse_long_ru(text)

    payload = {
        "BundleRevision": BUNDLE_REVISION,
        "TitleBankShortsEn": expand_en_shorts(en_short, 100),
        "TitleBankShortsRu": expand_ru_shorts(ru_short, 100),
        "TitleBankLongEn": en_long,
        "TitleBankLongRu": ru_long,
    }
    OUT.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(
        "counts:",
        {k: len(v) for k, v in payload.items() if isinstance(v, list)},
    )
    print("sample EN short:", payload["TitleBankShortsEn"][0])
    print("sample RU long:", payload["TitleBankLongRu"][0][:80])


if __name__ == "__main__":
    main()
