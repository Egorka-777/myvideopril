#!/usr/bin/env python3
"""Rename assembled-*.mp4 batch to studio title-bank filenames (default Pocket Option keys)."""
import argparse
import random
import re
import sys
from pathlib import Path

DEFAULT_PHRASES = [
    "pocket option ai trading", "pocket option ai trading bot", "pocket option signals", "ai trading bot",
    "pocket option trading bot", "pocket option bot", "ai trading pocket option", "pocket option",
    "best ai trading bot", "pocket option ai trading strategy", "ai trading bot pocket option", "ai trading",
    "automated trading strategy", "binary options", "pocket option trading", "pocket option trading strategy",
    "binary options trading", "binary options strategy", "pocket option ai", "pocket option strategy",
    "binary options bot", "ai bot trading pocket option", "binary options for beginners", "otc trading pocket option",
    "binary options trading robot", "pocket option ai bot", "ai trading bot for pocket option", "binary options pocket option",
    "pocket option for beginners", "tutorial trading pocket option", "pocket option ai trading bots",
    "free ai trading bot pocket option", "binary options signals",
]

SIDECAR_SUFFIXES = (".assembly.xml", ".performance.xml", ".metadata.txt", ".title.txt")


def to_title(phrase: str) -> str:
    return " ".join(p[:1].upper() + p[1:].lower() if p else "" for p in phrase.split())


def normalize_bank(phrases):
    seen = set()
    out = []
    for p in phrases:
        t = to_title(p.strip())
        if not t:
            continue
        key = t.casefold()
        if key in seen:
            continue
        seen.add(key)
        out.append(t)
    return out


def clean_title(title: str) -> str:
    t = (title or "").strip()
    t = re.sub(r"\s*\(\d+\)\s*$", "", t).strip()
    t = re.sub(r"\s+", " ", t)
    return t


def pack_title_with_index(base: str, index1: int, total: int) -> str:
    t = clean_title(base)
    if total <= 1:
        return t
    prefix = f"{index1}. "
    max_base = max(1, 100 - len(prefix))
    if len(t) > max_base:
        t = t[:max_base].strip()
    return prefix + t


def make_windows_safe_title(title: str) -> str:
    t = clean_title(title)
    for ch in '<>:"/\\|?*':
        t = t.replace(ch, " ")
    t = re.sub(r"\s+", " ", t).strip()
    return t[:100] if t else "video"


def file_name_from_title(title: str) -> str:
    t = make_windows_safe_title(title)
    return t if t else "video"


def pick_title(bank, used, rng: random.Random) -> str:
    if len(used) < len(bank):
        for _ in range(64):
            title = bank[rng.randrange(len(bank))]
            if title not in used:
                used.add(title)
                return title
    return bank[rng.randrange(len(bank))]


def find_unique_path(folder: Path, stem: str, ext: str, avoid: Path) -> Path:
    candidate = folder / f"{stem}{ext}"
    n = 2
    while candidate.exists() and candidate.resolve() != avoid.resolve():
        alt = f"{stem} ({n})"
        candidate = folder / f"{alt}{ext}"
        n += 1
        if n > 200:
            raise RuntimeError(f"cannot allocate unique name for {stem}")
    return candidate


def sidecars_for(video: Path):
    for suf in SIDECAR_SUFFIXES:
        p = Path(str(video) + suf)
        if p.exists():
            yield p


def rename_batch(folder: Path, pattern: str, seed: int, dry_run: bool):
    bank = normalize_bank(DEFAULT_PHRASES)
    videos = sorted(folder.glob(pattern))
    if not videos:
        print(f"No files matching {pattern} in {folder}", file=sys.stderr)
        return 1
    rng = random.Random(seed)
    used = set()
    total = len(videos)
    plan = []
    used_stems = set()
    for i, src in enumerate(videos, start=1):
        base = pick_title(bank, used, rng)
        final_title = pack_title_with_index(base, i, total)
        stem = file_name_from_title(final_title)
        if stem.casefold() in used_stems:
            for n in range(2, 50):
                alt_title = clean_title(base) + f" ({n})"
                final_title = pack_title_with_index(alt_title, i, total)
                stem = file_name_from_title(final_title)
                if stem.casefold() not in used_stems:
                    break
        used_stems.add(stem.casefold())
        dest = find_unique_path(folder, stem, src.suffix, src)
        plan.append((src, dest, final_title, base))

    log = folder / f"rename-plan-{pattern.replace('*', '')}.txt"
    lines = []
    for src, dest, final_title, base in plan:
        lines.append(f"{src.name}\t=>\t{dest.name}\t({final_title})")
    log.write_text("\n".join(lines), encoding="utf-8")
    print(f"Plan: {len(plan)} files -> {log}")

    if dry_run:
        for line in lines[:5]:
            print(line)
        if len(lines) > 5:
            print(f"... and {len(lines) - 5} more")
        return 0

    for src, dest, final_title, _base in plan:
        if src.resolve() == dest.resolve():
            continue
        for side in list(sidecars_for(src)):
            new_side = Path(str(side).replace(str(src), str(dest), 1))
            side.rename(new_side)
        src.rename(dest)
        title_path = Path(str(dest) + ".title.txt")
        title_path.write_text(final_title, encoding="utf-8")
        meta_path = Path(str(dest) + ".metadata.txt")
        if meta_path.exists():
            text = meta_path.read_text(encoding="utf-8")
            if re.search(r"(?m)^title=", text):
                text = re.sub(r"(?m)^title=.*$", f"title={final_title}", text)
            else:
                text = f"title={final_title}\n" + text
            meta_path.write_text(text, encoding="utf-8")

    print(f"Renamed {len(plan)} videos in {folder}")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--folder", default=r"C:\Users\hackerman\Videos\VideoBatch\Assembled")
    ap.add_argument("--pattern", default="assembled-20261009-*.mp4")
    ap.add_argument("--seed", type=int, default=20261009)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    return rename_batch(Path(args.folder), args.pattern, args.seed, args.dry_run)


if __name__ == "__main__":
    raise SystemExit(main())
