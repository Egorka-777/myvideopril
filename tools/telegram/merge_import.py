"""Merge worker import into telegram.vault (current Windows user). No secrets printed."""
import ctypes
import json
import shutil
import subprocess
import sys
from datetime import datetime
from pathlib import Path

CRYPTPROTECT_UI_FORBIDDEN = 0x1
HEADER = b"VBTG1\n"


def dpapi_unprotect(data: bytes) -> bytes:
    blob_in = ctypes.create_string_buffer(data)
    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", ctypes.c_uint), ("pbData", ctypes.POINTER(ctypes.c_char))]
    inp = DATA_BLOB(len(data), blob_in)
    out = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptUnprotectData(
        ctypes.byref(inp), None, None, None, None, CRYPTPROTECT_UI_FORBIDDEN, ctypes.byref(out)
    ):
        raise OSError("CryptUnprotectData failed")
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out.pbData)


def dpapi_protect(data: bytes) -> bytes:
    blob_in = ctypes.create_string_buffer(data)
    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", ctypes.c_uint), ("pbData", ctypes.POINTER(ctypes.c_char))]
    inp = DATA_BLOB(len(data), blob_in)
    out = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptProtectData(
        ctypes.byref(inp), None, None, None, None, CRYPTPROTECT_UI_FORBIDDEN, ctypes.byref(out)
    ):
        raise OSError("CryptProtectData failed")
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out.pbData)


def load_vault(path: Path) -> dict:
    raw = path.read_bytes()
    if not raw.startswith(HEADER):
        raise ValueError("bad vault header")
    return json.loads(dpapi_unprotect(raw[len(HEADER) :]).decode("utf-8"))


def save_vault(path: Path, data: dict) -> None:
    plain = json.dumps(data, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    enc = dpapi_protect(plain)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_bytes(HEADER + enc)
    if path.exists():
        shutil.copy2(path, path.with_name(path.name + ".bak"))
        tmp.replace(path)
    else:
        tmp.replace(path)


def merge_account(data: dict, incoming: dict) -> None:
    accounts = data.setdefault("accounts", [])
    current = next((a for a in accounts if a.get("id") == incoming.get("id")), None)
    if current is None:
        incoming.setdefault("proxy", "")
        incoming.setdefault("proxy_id", "")
        incoming.setdefault("status", "unchecked")
        accounts.append(incoming)
        return
    keep = current.get("api_source") in ("json", "custom") and incoming.get("api_source") in (
        "desktop_compat",
        "tdata_desktop",
    )
    current["session"] = incoming["session"]
    current["source_format"] = incoming.get("source_format", current.get("source_format", ""))
    opts = incoming.get("dc_options") or []
    if opts:
        current["dc_options"] = opts
    if not keep:
        for key in ("api_id", "api_hash", "api_source", "device", "system", "app_version", "lang", "system_lang"):
            if key in incoming:
                current[key] = incoming[key]
    if incoming.get("expected_user_id"):
        current["expected_user_id"] = incoming["expected_user_id"]
        current["user_id"] = incoming.get("user_id", incoming["expected_user_id"])
    if current.get("session") != incoming.get("session") or not keep:
        if current.get("status") not in ("blocked", "deactivated", "unauthorized"):
            current["status"] = "unchecked"
            current["last_error"] = "Данные сессии обновлены; проверьте аккаунт"
            current["last_code"] = ""


def run_import(python: Path, worker: Path, root: Path, zip_path: Path) -> dict:
    job = json.dumps({"action": "import", "paths": [str(zip_path)], "root": str(root)}, ensure_ascii=False)
    proc = subprocess.run(
        [str(python), str(worker)],
        input=job + "\n",
        capture_output=True,
        text=True,
        encoding="utf-8",
        cwd=str(worker.parent),
    )
    if proc.returncode not in (0, None):
        raise RuntimeError(proc.stderr or proc.stdout or "import failed")
    for line in proc.stdout.splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        ev = json.loads(line)
        if ev.get("event") == "imported":
            return ev["account"]
    raise RuntimeError("no imported account in worker output")


def main():
    if len(sys.argv) < 2:
        print("usage: merge_import.py ZIP_PATH", file=sys.stderr)
        return 2
    zip_path = Path(sys.argv[1]).resolve()
    root = Path(os.environ.get("LOCALAPPDATA", "")) / "VideoBatchDesktop" / "telegram"
    vault = root / "telegram.vault"
    python = root / "venv" / "Scripts" / "python.exe"
    worker = next(
        p / "worker.py"
        for p in (
            Path(r"D:\VideoBatch\VideoBatch\tools\telegram"),
            Path(__file__).resolve().parent,
        )
        if (p / "worker.py").is_file()
    )
    if not python.is_file():
        raise SystemExit("venv python missing")
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    shutil.copy2(vault, str(vault) + f".bak-reimport-{stamp}")
    incoming = run_import(python, worker, root, zip_path)
    data = load_vault(vault)
    merge_account(data, incoming)
    save_vault(vault, data)
    print("merged", incoming.get("id", "")[:8] + "…")
    return 0


if __name__ == "__main__":
    import os

    raise SystemExit(main())
