"""Build-time consistency manifest for the complete desktop update package."""
import hashlib
import json
from pathlib import Path
import re
import sys


def build(root):
    root = Path(root).resolve()
    worker = root / 'tools/telegram/worker.py'
    version = re.search(r"^VERSION = '([^']+)'$", worker.read_text(encoding='utf-8'), re.M)[1]
    files = []
    for path in sorted(root.rglob('*')):
        if path.is_file() and path.name != 'TELEGRAM_PACKAGE.json':
            if path.is_symlink():raise ValueError('Package contains a link')
            files.append(dict(path=path.relative_to(root).as_posix(), sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    report = dict(version=version, files=files)
    (root/'TELEGRAM_PACKAGE.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    return report


if __name__=='__main__':build(sys.argv[1])
