"""Fail-closed editor for an already open official Android app.

Protocol: one JSON object on stdin; JSON events on stdout. Never launches an
app/emulator or logs UI text, account IDs, supplied values, paths or adb output.
This adapter supports English TikTok profile/edit/name/bio screens only.
Avatar upload is deliberately refused BEFORE edits: UI XML cannot verify the
uploaded image. Non-ASCII input uses the existing local emulator UTF-8 clipboard
helper under Python 3.12; stock ADB input remains used for plain ASCII.
Empty avatar_path means preserve the avatar, not remove it. No preset storage
or 'used' state is touched here. Instagram fails explicitly until its actual
logged-in UI and image verification have been implemented and tested.
"""
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

SERIAL = "emulator-5554"
PACKAGES = {"tiktok": "com.zhiliaoapp.musically", "instagram": "com.instagram.android"}
MESSAGES = {
    "invalid_request": "Invalid Android profile edit request; nothing was applied.",
    "identity_required": "An explicit expected platform username is required; a display name or Dolphin ID is not an identity.",
    "identity_mismatch": "The open account does not match the expected username. Editing stopped.",
    "identity_unverified": "The current account identity cannot be verified. Editing stopped.",
    "auth_required": "Login, password, security verification or a code is required. Complete it manually; editing stopped.",
    "wrong_app": "The required official app is not in the foreground. Open the correct logged-in account manually.",
    "unsupported_screen": "Unsupported or ambiguous Android screen. Editing stopped; no arbitrary clicks were attempted.",
    "unsupported_instagram": "Instagram editing is not supported by this adapter; no fields were changed.",
    "avatar_unverifiable": "Avatar upload and post-save image verification are not supported by this Android adapter; no fields were changed.",
    "clipboard_unavailable": "The local emulator UTF-8 clipboard helper failed or did not confirm exact readback. Editing stopped; no save was attempted for this field.",
    "adb_failed": "Android inspection or input failed. The outcome is not confirmed; inspect the app manually before another attempt.",
    "verification_failed": "The saved profile fields could not all be verified. Inspect the app manually; the preset must not be marked used.",
    "busy": "Another Android profile edit is in progress. Wait for it to finish.",
    "internal_error": "Android profile editing stopped unexpectedly. The outcome is not confirmed; inspect the app manually.",
}


class EditError(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def emit(event, **fields):
    print(json.dumps(dict(event=event, **fields), ensure_ascii=True), flush=True)


def username(value):
    if not isinstance(value, str):
        raise EditError("identity_required")
    value = value.strip()
    if value.startswith("@"):
        value = value[1:]
    if not re.fullmatch(r"[A-Za-z0-9_.]{1,30}", value):
        raise EditError("identity_required")
    return value.lower()


def validate(job):
    if not isinstance(job, dict) or job.get("network") not in PACKAGES:
        raise EditError("invalid_request")
    expected = username(job.get("expected_username"))
    if not isinstance(job.get("account_id"), str) or not job["account_id"].strip():
        raise EditError("invalid_request")
    for key in ("display_name", "bio", "avatar_path"):
        if not isinstance(job.get(key), str):
            raise EditError("invalid_request")
    if not job["display_name"].strip():
        raise EditError("invalid_request")
    # No guessed bio limits: the application's editable field/counter is
    # authoritative; truncation is detected before Save by exact readback.
    # TikTok nickname documentation: support.tiktok.com/en/getting-started/
    # setting-up-your-profile/changing-your-nickname (30 characters).
    if job["network"] == "tiktok" and len(job["display_name"]) > 30:
        raise EditError("invalid_request")
    return expected


class Screen:
    def __init__(self, xml, package):
        try:
            self.root = ET.fromstring(xml)
        except ET.ParseError:
            raise EditError("unsupported_screen") from None
        self.package = package
        self.nodes = list(self.root.iter("node"))
        self.parents = {child: parent for parent in self.root.iter() for child in parent}
        self.app = [n for n in self.nodes if n.get("package") == package]
        labels = {n.get(k, "").strip().lower() for n in self.nodes for k in ("text", "content-desc", "hint")}
        auth = {"log in", "sign in", "log into instagram", "log in to tiktok", "password", "enter password",
                "enter code", "verification code", "security check", "verify your identity", "two-factor authentication"}
        if labels & auth or any(n.get("password") == "true" for n in self.nodes):
            raise EditError("auth_required")
        if not self.app:
            raise EditError("wrong_app")

    def labels(self, label):
        return [n for n in self.app if label in (n.get("text"), n.get("content-desc"))]

    def one_label(self, label):
        nodes = self.labels(label)
        # Accessibility often exposes both a clickable parent description and
        # its nonclickable text child. They must resolve to ONE action target.
        targets = []
        for n in nodes:
            target = self.clickable(n)
            if target not in targets:
                targets.append(target)
        if len(targets) != 1:
            raise EditError("unsupported_screen")
        return targets[0]

    def clickable(self, node):
        while node is not None:
            if node.get("package") != self.package or node.get("enabled") == "false":
                break
            if node.get("clickable") == "true":
                return node
            node = self.parents.get(node)
        raise EditError("unsupported_screen")

    def is_edit(self):
        return all(self.labels(label) for label in ("Edit profile", "Name", "Username", "Bio"))

    def row(self, label):
        nodes = [n for n in self.app if n.get("text") == label]
        if len(nodes) != 1:
            raise EditError("unsupported_screen")
        # A supported row is a clickable container with one label and one value.
        # Never infer values from geometry or from unrelated text on the page.
        container = self.clickable(nodes[0])
        values = [n.get("text", "") for n in container.iter("node")
                  if n is not nodes[0] and n.get("text", "")]
        if len(values) != 1:
            raise EditError("unsupported_screen")
        return container, values[0]

    def identity(self, expected):
        if self.is_edit():
            actual = self.row("Username")[1]
        else:
            # Resource ID observed on the installed official TikTok profile UI.
            ids = [n for n in self.app if n.get("resource-id") == self.package + ":id/mje"]
            if len(ids) != 1 or not self.labels("Edit profile"):
                raise EditError("identity_unverified")
            actual = ids[0].get("text", "")
        try:
            matched = username(actual) == expected
        except EditError:
            raise EditError("identity_unverified") from None
        if not matched:
            raise EditError("identity_mismatch")

    def field(self, title):
        fields = [n for n in self.app if n.get("class") == "android.widget.EditText"]
        if not self.labels(title) or len(fields) != 1 or fields[0].get("password") == "true":
            raise EditError("unsupported_screen")
        return fields[0]


def needs_clipboard(value):
    return any(ord(c) < 32 or ord(c) > 126 or c == "%" for c in value)


class Adb:
    def __init__(self, sdk):
        self.exe = str(Path(sdk) / "platform-tools" / "adb.exe")
        if not Path(self.exe).is_file():
            raise EditError("adb_failed")

    def run(self, *args):
        try:
            result = subprocess.run([self.exe, "-s", SERIAL, *args], capture_output=True,
                                    encoding="utf-8", errors="replace", timeout=30,
                                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except (OSError, subprocess.SubprocessError):
            raise EditError("adb_failed") from None
        if result.returncode:
            raise EditError("adb_failed")
        return result.stdout

    def screen(self, package):
        # Validate foreground focus, not just a background activity in dumpsys.
        focus = self.run("shell", "dumpsys", "window")
        current = re.search(r"mCurrentFocus=Window\{[^\r\n]+", focus)
        if not current or not re.search(r"\s" + re.escape(package) + r"/", current.group()):
            raise EditError("wrong_app")
        path = "/sdcard/vb-profile-edit.xml"
        self.run("shell", "rm", "-f", path)
        dumped = self.run("shell", "uiautomator", "dump", path)
        if "dumped to:" not in dumped:
            raise EditError("unsupported_screen")
        return Screen(self.run("shell", "cat", path), package)

    def tap(self, node):
        bounds = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
        if not bounds or node.get("enabled") == "false":
            raise EditError("unsupported_screen")
        x1, y1, x2, y2 = map(int, bounds.groups())
        if x2 <= x1 or y2 <= y1:
            raise EditError("unsupported_screen")
        self.run("shell", "input", "tap", str((x1 + x2) // 2), str((y1 + y2) // 2))

    def clipboard(self, action, value=None):
        # This helper's h2 dependency is already installed under Python 3.12,
        # not the Python 3.11 runtime used by this editor. Never fall back to
        # lossy adb text input after a clipboard failure.
        python = Path(os.environ.get("LOCALAPPDATA", "")) / "Programs/Python/Python312/python.exe"
        helper = Path(__file__).with_name("emulator_clipboard.py")
        request = {"action": action}
        if action == "set":
            request["value"] = value
        try:
            result = subprocess.run([str(python), str(helper)], input=json.dumps(request, ensure_ascii=True),
                                    capture_output=True, encoding="utf-8", timeout=30,
                                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            reply = json.loads(result.stdout)
            if (result.returncode != 0 or not isinstance(reply, dict) or reply.get("ok") is not True
                    or reply.get("verified") is not True or reply.get("action") != action):
                raise EditError("clipboard_unavailable")
        except (OSError, subprocess.SubprocessError, ValueError):
            raise EditError("clipboard_unavailable") from None

    def replace(self, node, value):
        use_clipboard = needs_clipboard(value)
        # Set and verify BEFORE clearing the editable field.
        if use_clipboard:
            self.clipboard("set", value)
        self.tap(node)
        self.run("shell", "input", "keycombination", "113", "29")  # Ctrl+A
        self.run("shell", "input", "keyevent", "67")  # clear even for empty bio
        if use_clipboard:
            self.run("shell", "input", "keyevent", "279")  # KEYCODE_PASTE
        elif value:
            self.run("shell", "input text " + shlex.quote(value.replace(" ", "%s")))


def wait_screen(adb, package, predicate):
    # Readiness polling only. No input action is retried, including Save.
    deadline = time.monotonic() + 8
    while True:
        screen = adb.screen(package)
        if predicate(screen):
            return screen
        if time.monotonic() >= deadline:
            raise EditError("unsupported_screen")
        time.sleep(0.3)


def apply(job, adb, notify=emit):
    expected = validate(job)
    package = PACKAGES[job["network"]]
    screen = adb.screen(package)
    if job["network"] == "instagram":
        raise EditError("unsupported_instagram")
    screen.identity(expected)
    # Preflight ALL requested capabilities before touching any persisted field.
    if job["avatar_path"]:
        raise EditError("avatar_unverifiable")
    if any(needs_clipboard(job[key]) for key in ("display_name", "bio")):
        adb.clipboard("get")  # Read-only capability check before the first edit.
    notify("status", code="identity_verified", text="Open account identity verified; inspecting profile fields.")
    if not screen.is_edit():
        adb.tap(screen.one_label("Edit profile"))
        screen = wait_screen(adb, package, lambda s: s.is_edit())
    screen.identity(expected)
    # Require both supported row structures before the first save.
    screen.row("Name")
    screen.row("Bio")
    changed = []
    attempted = []
    try:
        for label, key in (("Name", "display_name"), ("Bio", "bio")):
            screen.identity(expected)
            row, current = screen.row(label)
            if current == job[key]:
                continue
            adb.tap(row)
            screen = wait_screen(adb, package, lambda s: bool(s.labels(label)) and
                                 len([n for n in s.app if n.get("class") == "android.widget.EditText"]) == 1)
            field = screen.field(label)
            # Treat opening an editor that doesn't match the verified row as an
            # unsupported state, rather than overwriting an unexpected field.
            if field.get("text", "") != current:
                raise EditError("verification_failed")
            adb.replace(field, job[key])
            screen = adb.screen(package)
            if screen.field(label).get("text", "") != job[key]:
                raise EditError("verification_failed")
            # Save may open a rate-limit confirmation, challenge, or fail. Never
            # click through it and never repeat the save.
            attempted.append(key)
            adb.tap(screen.one_label("Save"))
            screen = wait_screen(adb, package, lambda s: s.is_edit())
            screen.identity(expected)
            if screen.row(label)[1] != job[key]:
                raise EditError("verification_failed")
            changed.append(key)
        # Reopen after leaving the editor: do not treat an input echo as a save.
        adb.run("shell", "input", "keyevent", "4")
        screen = wait_screen(adb, package, lambda s: not s.is_edit() and bool(s.labels("Edit profile")))
        screen.identity(expected)
        adb.tap(screen.one_label("Edit profile"))
        screen = wait_screen(adb, package, lambda s: s.is_edit())
        screen.identity(expected)
        if screen.row("Name")[1] != job["display_name"] or screen.row("Bio")[1] != job["bio"]:
            raise EditError("verification_failed")
    except Exception as failure:
        error = failure if isinstance(failure, EditError) else EditError("internal_error")
        # Only fixed field identifiers, never their values. A failed save can
        # have committed remotely, even when the local UI never confirms it.
        error.confirmed_fields = changed
        error.attempted_fields = attempted
        raise error from None
    return {"display_name": True, "bio": True, "avatar": "not_requested"}


def main():
    changed = []
    attempted = []
    lock = None
    try:
        job = json.loads(sys.stdin.readline())
        validate(job)
        # Exclusive per-device lock shared by editor processes, not by account.
        # OS byte-range locks release after crashes; no stale-lock retry loop.
        import msvcrt
        lock_path = Path(os.environ.get("TEMP", ".")) / "videobatch-android-profile-emulator-5554.lock"
        lock = open(lock_path, "a+b")
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            raise EditError("busy") from None
        sdk = job.get("sdk")
        if not isinstance(sdk, str) or not sdk:
            raise EditError("invalid_request")
        verified = apply(job, Adb(sdk))
        emit("applied", code="verified", text="All requested supported fields were reread after saving; avatar was not requested.", verified=verified)
        return 0
    except EditError as error:
        changed = getattr(error, "confirmed_fields", [])
        attempted = getattr(error, "attempted_fields", [])
        emit("error", code=error.code, text=MESSAGES[error.code], confirmed_fields=changed,
             attempted_fields=attempted, may_have_changes=bool(attempted), mark_used=False)
    except (ValueError, TypeError):
        emit("error", code="invalid_request", text=MESSAGES["invalid_request"], mark_used=False)
    except Exception:
        emit("error", code="internal_error", text=MESSAGES["internal_error"], mark_used=False)
    finally:
        if lock is not None:
            lock.close()
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
