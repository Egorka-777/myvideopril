import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("profile_edit", ROOT / "tools/android/profile_edit.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
P = m.PACKAGES["tiktok"]


def node(text="", desc="", rid="", cls="android.widget.TextView", click=False, children=(), **attrs):
    n = ET.Element("node", {"text": text, "content-desc": desc, "resource-id": rid,
                           "class": cls, "package": P, "enabled": "true",
                           "clickable": str(click).lower(), "bounds": "[0,0][100,50]", **attrs})
    n.extend(children)
    return n


def screen(*nodes):
    root = ET.Element("hierarchy")
    root.extend(nodes)
    return m.Screen(ET.tostring(root, encoding="unicode"), P)


def profile(who="sample.user"):
    # Sanitized structure from the observed emulator TikTok profile. Display
    # name intentionally differs; only the scoped mje username proves identity.
    return screen(node("Display title", rid=P + ":id/kdt"),
                  node("@" + who, rid=P + ":id/mje", click=True),
                  node(desc="Edit profile", click=True, children=[node("Edit profile")]))


def edit(name="Old", bio="Old bio", who="sample.user"):
    def row(label, value):
        return node(click=True, children=[node(label), node(value)])
    return screen(node("Edit profile"), row("Username", who), row("Name", name), row("Bio", bio))


def field(label, value):
    return screen(node(label), node(value, cls="android.widget.EditText", click=True), node("Save", click=True))


class FakeAdb:
    def __init__(self, screens):
        self.screens = iter(screens)
        self.actions = []

    def screen(self, package):
        value = next(self.screens)
        if isinstance(value, Exception):
            raise value
        return value

    def tap(self, n):
        self.actions.append(("tap", n.get("text") or n.get("content-desc")))

    def replace(self, n, value):
        self.actions.append(("replace", value))

    def clipboard(self, action, value=None):
        self.actions.append(("clipboard", action))

    def run(self, *args):
        self.actions.append(args)


def job(**kwargs):
    result = dict(network="tiktok", account_id="internal-id", expected_username="@sample.user",
                  display_name="New", bio="New bio", avatar_path="")
    result.update(kwargs)
    return result


class ProfileEditTests(unittest.TestCase):
    def code(self, code, fn):
        with self.assertRaises(m.EditError) as caught:
            fn()
        self.assertEqual(caught.exception.code, code)
        return caught.exception

    def test_expected_identity_is_mandatory_not_derived_from_account_id(self):
        for missing in (None, "", " ", "Dolphin 123", "https://tiktok.com/@user", "user@example.com", "@@user"):
            self.code("identity_required", lambda: m.validate(job(expected_username=missing)))

    def test_identity_normalizes_only_at_prefix_and_case(self):
        profile().identity(m.username(" @Sample.User "))
        self.code("identity_mismatch", lambda: profile().identity("another.user"))

    def test_unscoped_expected_text_is_not_identity(self):
        s = screen(node("@sample.user"), node("Edit profile", click=True))
        self.code("identity_unverified", lambda: s.identity("sample.user"))

    def test_public_profile_without_owner_edit_action_is_not_identity(self):
        s = screen(node("@sample.user", rid=P + ":id/mje"))
        self.code("identity_unverified", lambda: s.identity("sample.user"))

    def test_wrong_identity_stops_before_any_action(self):
        adb = FakeAdb([profile("wrong.user")])
        self.code("identity_mismatch", lambda: m.apply(job(), adb))
        self.assertEqual(adb.actions, [])

    def test_auth_screens_stop(self):
        for label in ("Log in", "Verification code", "Security check", "Enter password"):
            self.code("auth_required", lambda: screen(node(label)))
        self.code("auth_required", lambda: screen(node(password="true")))

    def test_wrong_package_stops(self):
        self.code("wrong_app", lambda: screen(node(package="com.android.launcher")))

    def test_avatar_is_refused_before_any_changes(self):
        adb = FakeAdb([profile()])
        self.code("avatar_unverifiable", lambda: m.apply(job(avatar_path="private-photo.png"), adb))
        self.assertEqual(adb.actions, [])

    def test_unicode_percent_newline_use_clipboard_preflight_and_exact_readback(self):
        for value in ("Привет", "100%", "line\nnext", "Hi 🐈"):
            adb = FakeAdb([edit(name="New"), field("Bio", "Old bio"), field("Bio", value),
                           edit(name="New", bio=value), profile(), edit(name="New", bio=value)])
            result = m.apply(job(bio=value), adb, lambda *a, **kw: None)
            self.assertTrue(result["bio"])
            self.assertEqual(adb.actions[0], ("clipboard", "get"))
            self.assertEqual(sum(a == ("tap", "Save") for a in adb.actions), 1)

    def test_missing_clipboard_stops_before_any_edit(self):
        adb = FakeAdb([profile()])
        def failed(*args):
            raise m.EditError("clipboard_unavailable")
        adb.clipboard = failed
        self.code("clipboard_unavailable", lambda: m.apply(job(bio="Привет"), adb))
        self.assertEqual(adb.actions, [])

    def test_unicode_truncation_is_never_saved(self):
        adb = FakeAdb([edit(name="New"), field("Bio", "Old bio"), field("Bio", "Прив")])
        self.code("verification_failed", lambda: m.apply(job(bio="Привет"), adb, lambda *a, **kw: None))
        self.assertNotIn(("tap", "Save"), adb.actions)

    def test_instagram_explicitly_unsupported_without_mutation(self):
        adb = FakeAdb([profile()])
        self.code("unsupported_instagram", lambda: m.apply(job(network="instagram"), adb))
        self.assertEqual(adb.actions, [])

    def test_deduplicated_accessibility_parent_and_child(self):
        self.assertEqual(profile().one_label("Edit profile").get("content-desc"), "Edit profile")
        self.code("unsupported_screen", lambda: screen(
            node("Save", click=True, bounds="[0,0][100,50]"),
            node("Save", click=True, bounds="[200,0][300,50]")
        ).one_label("Save"))

    def test_ambiguous_rows_and_fields_are_unsupported(self):
        s = screen(node(click=True, children=[node("Name"), node("A"), node("B")]))
        self.code("unsupported_screen", lambda: s.row("Name"))
        s = screen(node("Bio"), node(cls="android.widget.EditText"), node(cls="android.widget.EditText"))
        self.code("unsupported_screen", lambda: s.field("Bio"))

    def test_success_requires_reopening_and_exact_readback(self):
        adb = FakeAdb([profile(), edit(), field("Name", "Old"), field("Name", "New"), edit(name="New"),
                       field("Bio", "Old bio"), field("Bio", "New bio"), edit(name="New", bio="New bio"),
                       profile(), edit(name="New", bio="New bio")])
        events = []
        result = m.apply(job(), adb, lambda *a, **kw: events.append((a, kw)))
        self.assertEqual(result, {"display_name": True, "bio": True, "avatar": "not_requested"})
        self.assertEqual(sum(a == ("tap", "Save") for a in adb.actions), 2)
        self.assertIn(("shell", "input", "keyevent", "4"), adb.actions)
        self.assertNotIn("sample.user", json.dumps(events))
        self.assertNotIn("Old bio", json.dumps(events))

    def test_partial_update_is_reported_and_save_not_retried(self):
        adb = FakeAdb([edit(), field("Name", "Old"), field("Name", "New"), edit(name="New"),
                       field("Bio", "Old bio"), field("Bio", "New bio"), m.EditError("auth_required")])
        error = self.code("auth_required", lambda: m.apply(job(), adb, lambda *a, **kw: None))
        self.assertEqual(error.confirmed_fields, ["display_name"])
        self.assertEqual(error.attempted_fields, ["display_name", "bio"])
        self.assertEqual(sum(a == ("tap", "Save") for a in adb.actions), 2)

    def test_truncated_input_never_saved(self):
        adb = FakeAdb([edit(), field("Name", "Old"), field("Name", "Ne")])
        error = self.code("verification_failed", lambda: m.apply(job(), adb, lambda *a, **kw: None))
        self.assertEqual(error.attempted_fields, [])
        self.assertNotIn(("tap", "Save"), adb.actions)

    def test_final_readback_mismatch_is_not_success(self):
        adb = FakeAdb([edit(name="New", bio="New bio"), profile(), edit(name="New", bio="stale")])
        self.code("verification_failed", lambda: m.apply(job(), adb, lambda *a, **kw: None))

    def test_identity_checked_again_after_save(self):
        adb = FakeAdb([edit(), field("Name", "Old"), field("Name", "New"), edit(name="New", who="other")])
        self.code("identity_mismatch", lambda: m.apply(job(), adb, lambda *a, **kw: None))
        self.assertEqual(sum(a[0] == "replace" for a in adb.actions), 1)

    def test_noop_still_requires_reopened_verification(self):
        adb = FakeAdb([edit(name="New", bio="New bio"), profile(), edit(name="New", bio="New bio")])
        m.apply(job(), adb, lambda *a, **kw: None)
        self.assertFalse(any(a[0] == "replace" for a in adb.actions))

    def test_protocol_errors_do_not_echo_input_or_traceback(self):
        for payload in ("private secret not json", json.dumps(job(expected_username="private secret"))):
            p = subprocess.run([sys.executable, str(ROOT / "tools/android/profile_edit.py")],
                               input=payload, capture_output=True, text=True)
            self.assertEqual(p.returncode, 1)
            event = json.loads(p.stdout)
            self.assertEqual(event["event"], "error")
            self.assertIs(event["mark_used"], False)
            self.assertNotIn("private secret", p.stdout + p.stderr)
            self.assertEqual(p.stderr, "")

    def test_unexpected_error_after_save_retains_partial_outcome(self):
        adb = FakeAdb([edit(), field("Name", "Old"), field("Name", "New"), RuntimeError("private UI data")])
        error = self.code("internal_error", lambda: m.apply(job(), adb, lambda *a, **kw: None))
        self.assertEqual(error.attempted_fields, ["display_name"])
        self.assertEqual(error.confirmed_fields, [])
        self.assertNotIn("private", str(error))

    def test_foreground_focus_rejects_background_package(self):
        adb = object.__new__(m.Adb)
        adb.run = lambda *a: "mCurrentFocus=Window{abc u0 com.android.launcher/.Home}"
        self.code("wrong_app", lambda: adb.screen(P))

    def test_input_uses_shell_quoting_and_no_arbitrary_command(self):
        adb = object.__new__(m.Adb)
        calls = []
        adb.run = lambda *a: calls.append(a)
        value = "a'; touch /sdcard/unwanted; #"
        adb.replace(node(click=True), value)
        import shlex
        shell_command = calls[-1][1]
        self.assertEqual(shlex.split(shell_command), ["input", "text", value.replace(" ", "%s")])

    def test_clipboard_replace_verifies_before_clear_and_uses_paste(self):
        adb = object.__new__(m.Adb)
        calls = []
        adb.run = lambda *a: calls.append(a)
        adb.clipboard = lambda *a: calls.append(("clipboard", *a))
        for value in ("Привет 🐈", "100%", "line\nnext"):
            calls.clear()
            adb.replace(node(click=True), value)
            self.assertEqual(calls[0], ("clipboard", "set", value))
            self.assertEqual(calls[-1], ("shell", "input", "keyevent", "279"))
            self.assertFalse(any("input text" in str(a) for a in calls))

    def test_clipboard_set_failure_never_clears_field(self):
        adb = object.__new__(m.Adb)
        calls = []
        adb.run = lambda *a: calls.append(a)
        def failed(*args):
            raise m.EditError("clipboard_unavailable")
        adb.clipboard = failed
        self.code("clipboard_unavailable", lambda: adb.replace(node(click=True), "Привет"))
        self.assertEqual(calls, [])

    def test_clipboard_bridge_uses_python312_private_stdin_and_strict_reply(self):
        adb = object.__new__(m.Adb)
        adb.serial = "emulator-5554"
        for action in ("get", "set"):
            good = dict(ok=True, verified=True, action=action)
            with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout=json.dumps(good))) as run:
                adb.clipboard(action, "Привет")
            args, kwargs = run.call_args
            self.assertIn("Python312", args[0][0])
            self.assertTrue(args[0][1].endswith("emulator_clipboard.py"))
            self.assertNotIn("Привет", str(args))
            self.assertEqual(json.loads(kwargs["input"]), {"action": action, **({"value": "Привет"} if action == "set" else {})})
        for reply in ("secret invalid response", "[]", '{"ok":true}', '{"ok":true,"verified":true,"action":"set"}'):
            with patch.object(m.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout=reply)):
                self.code("clipboard_unavailable", lambda: adb.clipboard("get"))
        with patch.object(m.subprocess, "run", side_effect=FileNotFoundError("secret")):
            self.code("clipboard_unavailable", lambda: adb.clipboard("get"))

    def test_threads_has_no_adapter_and_never_mutates(self):
        adb = FakeAdb([])
        self.code("invalid_request", lambda: m.apply(job(network="threads"), adb))
        self.assertEqual(adb.actions, [])

    def test_name_length_preflight(self):
        self.code("invalid_request", lambda: m.validate(job(display_name="x" * 31)))


if __name__ == "__main__":
    unittest.main()
