#!/usr/bin/env python3
"""Patch VideoBatchDesktop settings.xml title banks from youtube-title-banks.json."""
import json
import shutil
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"x": "http://www.w3.org/2001/XMLSchema-instance"}
ET.register_namespace("", "http://www.w3.org/2001/XMLSchema-instance")
ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")


def set_string_list(parent: ET.Element, tag: str, values: list[str]) -> None:
    el = parent.find(tag)
    if el is None:
        el = ET.SubElement(parent, tag)
    for child in list(el):
        el.remove(child)
    for v in values:
        s = ET.SubElement(el, "string")
        s.text = v


def main() -> int:
    settings = Path(
        sys.argv[1]
        if len(sys.argv) > 1
        else Path.home() / "AppData/Local/VideoBatchDesktop/settings.xml"
    )
    banks_path = Path(__file__).with_name("youtube-title-banks.json")
    data = json.loads(banks_path.read_text(encoding="utf-8"))
    backup = settings.with_suffix(
        settings.suffix + ".bak-titlebanks-20261014"
    )
    shutil.copy2(settings, backup)
    tree = ET.parse(settings)
    root = tree.getroot()
    mapping = {
        "TitleBankLongRu": data["TitleBankLongRu"],
        "TitleBankLongEn": data["TitleBankLongEn"],
        "TitleBankShortsRu": data["TitleBankShortsRu"],
        "TitleBankShortsEn": data["TitleBankShortsEn"],
    }
    for tag, vals in mapping.items():
        set_string_list(root, tag, vals)
    rev = root.find("TitleBanksBundleRevision")
    if rev is None:
        rev = ET.SubElement(root, "TitleBanksBundleRevision")
    rev.text = str(data.get("BundleRevision", 3))
    seeded = root.find("TitleBanksSeeded")
    if seeded is None:
        seeded = ET.SubElement(root, "TitleBanksSeeded")
    seeded.text = "true"
    for cur in (
        "TitleCursorLongRu",
        "TitleCursorLongEn",
        "TitleCursorShortsRu",
        "TitleCursorShortsEn",
    ):
        c = root.find(cur)
        if c is not None:
            c.text = "0"
    tree.write(settings, encoding="utf-8", xml_declaration=True)
    print("Backup:", backup)
    print("Updated:", settings)
    for tag, vals in mapping.items():
        print(tag, len(vals), "->", vals[0][:70])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
