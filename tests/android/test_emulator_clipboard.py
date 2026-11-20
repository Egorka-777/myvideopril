import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import h2.config
import h2.connection
import h2.events

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "tools/android/emulator_clipboard.py"
spec = importlib.util.spec_from_file_location("emulator_clipboard", HELPER)
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

HEADERS = {":status": "200", "content-type": "application/grpc"}


class FakeSocket:
    """In-memory HTTP/2 peer, including fragmented receives and flow control."""
    def __init__(self, payload=b"", status="0", finish=True):
        self.server = h2.connection.H2Connection(h2.config.H2Configuration(client_side=False))
        self.server.initiate_connection()
        self.payload, self.status, self.finish = payload, status, finish
        self.received = bytearray()
        self.headers = {}
        self.output = bytearray(self.server.data_to_send())

    def __enter__(self):
        return self

    def __exit__(self, *args):
        pass

    def settimeout(self, timeout):
        pass

    def sendall(self, packet):
        for event in self.server.receive_data(packet):
            if isinstance(event, h2.events.RequestReceived):
                self.headers = dict(event.headers)
            elif isinstance(event, h2.events.DataReceived):
                self.received.extend(event.data)
                self.server.acknowledge_received_data(event.flow_controlled_length, 1)
            elif isinstance(event, h2.events.StreamEnded):
                self.server.send_headers(1, list(HEADERS.items()))
                self.server.send_data(1, m.frame(self.payload))
                if self.finish:
                    self.server.send_headers(1, [("grpc-status", self.status)], end_stream=True)
        self.output.extend(self.server.data_to_send())

    def recv(self, size):
        # Deliberately split both HTTP/2 headers and gRPC message frames.
        result = bytes(self.output[:7])
        del self.output[:7]
        return result


class ClipboardTests(unittest.TestCase):
    def error(self, code, fn):
        with self.assertRaises(m.ClipboardError) as caught:
            fn()
        self.assertEqual(caught.exception.code, code)
        return caught.exception

    def discovery_file(self, directory, name="pid_123.ini", **values):
        cfg = {"port.serial": "5554", "port.adb": "5555", "grpc.port": "8554", "grpc.token": "private-token"}
        cfg.update(values)
        Path(directory, name).write_text("\n".join(k + "=" + v for k, v in cfg.items()), encoding="utf-8")

    def test_utf8_protobuf_roundtrip_and_byte_length(self):
        for text in ("", "Привет, мир!", "100%\n🐈 ё", "я" * 200):
            encoded = m.encode_clipboard(text)
            length, start = m.read_varint(encoded, 1)
            self.assertEqual(length, len(text.encode("utf-8")))
            self.assertEqual(encoded[start:], text.encode("utf-8"))
            self.assertEqual(m.decode_clipboard(encoded), text)
        self.assertEqual(m.decode_clipboard(b""), "")

    def test_unknown_protobuf_fields(self):
        unknown = b"\x10\x07\x19" + b"0" * 8 + b"\x22\x03abc\x2d" + b"0" * 4
        self.assertEqual(m.decode_clipboard(unknown + m.encode_clipboard("ё")), "ё")
        self.assertEqual(m.decode_clipboard(m.encode_clipboard("a") + m.encode_clipboard("b")), "b")

    def test_invalid_protobuf(self):
        for data in (b"\x0a\x04x", b"\x0a\x01\xff", b"\x00", b"\x08\x01", b"\x80", b"\x13", b"\x19x", b"\xff" * 11):
            self.error("protobuf_invalid", lambda: m.decode_clipboard(data))

    def test_unary_frame_parser(self):
        body = m.frame(m.encode_clipboard("Привет"))
        self.assertEqual(m.parse_response(HEADERS, {"grpc-status": "0"}, body, True), body[5:])
        self.assertEqual(m.parse_response(HEADERS, {"grpc-status": "0"}, m.frame(b""), True), b"")
        cases = [
            ("response_incomplete", HEADERS, {}, body, True),
            ("response_incomplete", HEADERS, {"grpc-status": "0"}, body, False),
            ("response_incomplete", HEADERS, {"grpc-status": "0"}, body[:-1], True),
            ("response_incomplete", HEADERS, {"grpc-status": "0"}, b"", True),
            ("grpc_failed", HEADERS, {"grpc-status": "7", "grpc-message": "private"}, body, True),
            ("grpc_failed", dict(HEADERS, **{"grpc-status": "16"}), {}, b"", True),
            ("compression_unsupported", HEADERS, {"grpc-status": "0"}, b"\x01" + body[1:], True),
            ("response_invalid", HEADERS, {"grpc-status": "0"}, body + m.frame(b""), True),
            ("http_failed", dict(HEADERS, **{":status": "401"}), {}, b"", True),
            ("response_invalid", {":status": "200", "content-type": "text/html"}, {"grpc-status": "0"}, body, True),
        ]
        for code, headers, trailers, data, ended in cases:
            with self.subTest(code=code, data=data):
                self.error(code, lambda: m.parse_response(headers, trailers, data, ended))

    def test_discovery(self):
        with tempfile.TemporaryDirectory() as directory:
            self.error("discovery_missing", lambda: m.discover(directory))
            self.discovery_file(directory, **{"grpc.token": "token%with+characters="})
            self.assertEqual(m.discover(directory), (8554, "token%with+characters="))
            self.discovery_file(directory, "pid_2.ini", **{"port.serial": "5556"})
            self.assertEqual(m.discover(directory)[0], 8554)
            self.discovery_file(directory, "pid_2.ini")
            self.error("discovery_ambiguous", lambda: m.discover(directory))
        self.error("invalid_serial", lambda: m.discover(serial="emulator-5556"))

    def test_discovery_rejects_invalid_endpoint_and_credentials(self):
        for field, value in (("grpc.port", "0"), ("grpc.port", "65536"), ("grpc.port", "remote:8554"),
                             ("port.adb", "5557"), ("grpc.token", ""), ("grpc.token", "bad token")):
            with tempfile.TemporaryDirectory() as directory:
                self.discovery_file(directory, **{field: value})
                self.error("discovery_invalid", lambda: m.discover(directory))

    def test_http2_transport_fragmentation_auth_and_flow_control(self):
        for payload in (m.encode_clipboard("Привет 🐈"), b"x" * 100000):
            peer = FakeSocket(m.encode_clipboard("readback"))
            with patch.object(m.socket, "create_connection", return_value=peer) as connect:
                result = m.unary((8554, "secret-token"), "setClipboard", payload)
            self.assertEqual(result, m.encode_clipboard("readback"))
            self.assertEqual(peer.received, m.frame(payload))
            self.assertEqual(peer.headers[b"authorization"], b"Bearer secret-token")
            self.assertEqual(connect.call_args.args[0], ("127.0.0.1", 8554))

    def test_http2_transport_failure_not_success(self):
        for peer, code in ((FakeSocket(status="7"), "grpc_failed"),
                           (FakeSocket(finish=False), "response_incomplete")):
            with patch.object(m.socket, "create_connection", return_value=peer):
                self.error(code, lambda: m.unary((8554, "secret"), "getClipboard", b""))

    def test_set_requires_exact_readback_without_retry(self):
        with tempfile.TemporaryDirectory() as directory:
            self.discovery_file(directory)
            calls = []
            def call(endpoint, method, payload):
                calls.append((method, payload))
                return b"" if method == "setClipboard" else m.encode_clipboard("Привет")
            result = m.execute({"action": "set", "value": "Привет"}, directory, call)
            self.assertEqual(result, {"ok": True, "action": "set", "verified": True})
            self.assertEqual([x[0] for x in calls], ["setClipboard", "getClipboard"])
            self.assertEqual(m.decode_clipboard(calls[0][1]), "Привет")
            error = self.error("readback_mismatch", lambda: m.execute({"action": "set", "value": "other"}, directory, call))
            self.assertTrue(error.may_have_changes)
            result = m.execute({"action": "get"}, directory, call)
            self.assertNotIn("Привет", json.dumps(result, ensure_ascii=False))

    def test_set_failure_retains_uncertain_mutation(self):
        with tempfile.TemporaryDirectory() as directory:
            self.discovery_file(directory)
            def failed(*args):
                raise m.ClipboardError("grpc_failed")
            error = self.error("grpc_failed", lambda: m.execute({"action": "set", "value": "secret"}, directory, failed))
            self.assertTrue(error.may_have_changes)

    def test_invalid_request_never_discovers_or_writes(self):
        with patch.object(m, "discover") as discover:
            for request in ({}, [], {"action": "set"}, {"action": "set", "value": 1},
                            {"action": "set", "value": "\ud800"}):
                self.error("invalid_request", lambda: m.execute(request))
            discover.assert_not_called()

    def test_cli_errors_never_echo_secrets(self):
        for payload in (b"private secret", b'{"action":"invalid","value":"private secret"}'):
            process = subprocess.run([sys.executable, str(HELPER)], input=payload, capture_output=True)
            self.assertEqual(process.returncode, 1)
            self.assertEqual(json.loads(process.stdout)["code"], "invalid_request")
            self.assertEqual(process.stderr, b"")
            self.assertNotIn(b"private", process.stdout)


if __name__ == "__main__":
    unittest.main()
