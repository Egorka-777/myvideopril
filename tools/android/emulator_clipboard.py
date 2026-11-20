"""Local Android Emulator UTF-8 clipboard helper (Python 3.12 with h2).

One UTF-8 JSON request on stdin: {"action":"set", "value":"..."} or
{"action":"get"}. Stdout contains status only, never clipboard text or tokens.
Set succeeds only after an exact getClipboard readback. No input is pasted.
"""
import configparser
import json
from pathlib import Path
import socket
import struct
import sys
import tempfile
import time

SERIAL = "emulator-5554"
SERVICE = "/android.emulation.control.EmulatorController/"
TIMEOUT = 8


class ClipboardError(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code
        self.may_have_changes = False


def discover(directory=None, serial=SERIAL):
    """Use the emulator's per-user discovery file, never a remote endpoint."""
    if serial != SERIAL:
        raise ClipboardError("invalid_serial")
    directory = Path(directory) if directory is not None else Path(tempfile.gettempdir()) / "avd" / "running"
    matches = []
    for path in directory.glob("pid_*.ini"):
        try:
            cfg = configparser.ConfigParser(interpolation=None)
            cfg.read_string("[emulator]\n" + path.read_text(encoding="utf-8-sig"))
            item = cfg["emulator"]
            if item.get("port.serial") != "5554":
                continue
            port = int(item["grpc.port"])
            token = item["grpc.token"]
            if (not 1 <= port <= 65535 or item.get("port.adb") != "5555"
                    or not token or any(ord(c) < 33 or ord(c) > 126 for c in token)):
                raise ValueError()
            matches.append((port, token))
        except (OSError, UnicodeError, configparser.Error, KeyError, ValueError):
            raise ClipboardError("discovery_invalid") from None
    if len(matches) != 1:
        raise ClipboardError("discovery_missing" if not matches else "discovery_ambiguous")
    return matches[0]


def varint(value):
    result = bytearray()
    while value >= 128:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


def read_varint(data, offset):
    value = 0
    for shift in range(0, 70, 7):
        if offset >= len(data):
            raise ClipboardError("protobuf_invalid")
        byte = data[offset]
        offset += 1
        if shift == 63 and byte > 1:
            raise ClipboardError("protobuf_invalid")
        value |= (byte & 127) << shift
        if byte < 128:
            return value, offset
    raise ClipboardError("protobuf_invalid")


def encode_clipboard(value):
    data = value.encode("utf-8")
    return b"\x0a" + varint(len(data)) + data


def decode_clipboard(data):
    """Decode ClipData, including protobuf's default empty text/unknown fields."""
    text, offset = "", 0
    while offset < len(data):
        tag, offset = read_varint(data, offset)
        number, wire = tag >> 3, tag & 7
        if not 1 <= number <= 0x1fffffff or (number == 1 and wire != 2):
            raise ClipboardError("protobuf_invalid")
        if wire == 0:
            _, offset = read_varint(data, offset)
        elif wire in (1, 5):
            offset += 8 if wire == 1 else 4
        elif wire == 2:
            length, offset = read_varint(data, offset)
            end = offset + length
            if end > len(data):
                raise ClipboardError("protobuf_invalid")
            if number == 1:
                try:
                    text = data[offset:end].decode("utf-8")
                except UnicodeError:
                    raise ClipboardError("protobuf_invalid") from None
            offset = end
        else:
            raise ClipboardError("protobuf_invalid")
        if offset > len(data):
            raise ClipboardError("protobuf_invalid")
    return text


def frame(payload):
    return b"\x00" + struct.pack(">I", len(payload)) + payload


def parse_response(headers, trailers, body, ended):
    """Require completed HTTP/2 stream, gRPC success trailer and ONE message."""
    if not ended:
        raise ClipboardError("response_incomplete")
    if headers.get(":status") != "200":
        raise ClipboardError("http_failed")
    content_type = headers.get("content-type", "").split(";", 1)[0]
    if content_type != "application/grpc" and not content_type.startswith("application/grpc+"):
        raise ClipboardError("response_invalid")
    # gRPC permits trailers-only errors in the initial HEADERS frame.
    status = trailers.get("grpc-status", headers.get("grpc-status"))
    if status is None:
        raise ClipboardError("response_incomplete")
    if status != "0":
        raise ClipboardError("grpc_failed")
    if len(body) < 5:
        raise ClipboardError("response_incomplete")
    if body[0] != 0:
        raise ClipboardError("compression_unsupported")
    length = struct.unpack(">I", body[1:5])[0]
    if len(body) < length + 5:
        raise ClipboardError("response_incomplete")
    if len(body) != length + 5:
        raise ClipboardError("response_invalid")
    return body[5:]


def unary(endpoint, method, payload):
    """One local plaintext HTTP/2 gRPC request; no retries or error details."""
    try:
        import h2.connection
        import h2.events
    except ImportError:
        raise ClipboardError("h2_unavailable") from None
    port, token = endpoint
    if method not in ("getClipboard", "setClipboard"):
        raise ClipboardError("invalid_method")
    headers, trailers, body = {}, {}, bytearray()
    deadline = time.monotonic() + TIMEOUT
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=TIMEOUT) as sock:
            connection = h2.connection.H2Connection()
            connection.initiate_connection()
            connection.send_headers(1, [
                (":method", "POST"), (":scheme", "http"),
                (":authority", "127.0.0.1:" + str(port)),
                (":path", SERVICE + method), ("content-type", "application/grpc"),
                ("te", "trailers"), ("authorization", "Bearer " + token),
            ])
            request, sent, ended = frame(payload), 0, False
            while not ended:
                # Respect both stream flow control and peer frame-size limits.
                while sent < len(request):
                    count = min(connection.local_flow_control_window(1),
                                connection.max_outbound_frame_size, len(request) - sent)
                    if count <= 0:
                        break
                    connection.send_data(1, request[sent:sent + count], end_stream=sent + count == len(request))
                    sent += count
                pending = connection.data_to_send()
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise ClipboardError("transport_failed")
                sock.settimeout(remaining)
                if pending:
                    sock.sendall(pending)
                packet = sock.recv(65536)
                if not packet:
                    break
                for event in connection.receive_data(packet):
                    if isinstance(event, h2.events.ResponseReceived) and event.stream_id == 1:
                        headers = {k.decode("ascii"): v.decode("ascii") for k, v in event.headers}
                    elif isinstance(event, h2.events.TrailersReceived) and event.stream_id == 1:
                        trailers = {k.decode("ascii"): v.decode("ascii") for k, v in event.headers}
                    elif isinstance(event, h2.events.DataReceived) and event.stream_id == 1:
                        body.extend(event.data)
                        connection.acknowledge_received_data(event.flow_controlled_length, 1)
                    elif isinstance(event, h2.events.StreamEnded) and event.stream_id == 1:
                        ended = True
                    elif isinstance(event, h2.events.StreamReset):
                        raise ClipboardError("transport_failed")
                    elif isinstance(event, h2.events.ConnectionTerminated) and not ended:
                        raise ClipboardError("transport_failed")
            return parse_response(headers, trailers, bytes(body), ended)
    except ClipboardError:
        raise
    except Exception:
        # Never expose grpc-message, socket errors, headers, tokens or payload.
        raise ClipboardError("transport_failed") from None


def read_clipboard(endpoint, call=unary):
    """Internal callable only. CLI never emits this private text."""
    return decode_clipboard(call(endpoint, "getClipboard", b""))


def execute(request, directory=None, call=unary):
    if not isinstance(request, dict) or request.get("action") not in ("get", "set"):
        raise ClipboardError("invalid_request")
    action = request["action"]
    if action == "set" and not isinstance(request.get("value"), str):
        raise ClipboardError("invalid_request")
    try:
        payload = encode_clipboard(request["value"]) if action == "set" else None
    except UnicodeError:
        raise ClipboardError("invalid_request") from None
    endpoint = discover(directory)
    try:
        if action == "set":
            if call(endpoint, "setClipboard", payload) != b"":
                raise ClipboardError("response_invalid")
        current = read_clipboard(endpoint, call)
        if action == "set" and current != request["value"]:
            raise ClipboardError("readback_mismatch")
    except ClipboardError as error:
        error.may_have_changes = action == "set"
        raise
    return {"ok": True, "action": action, "verified": True}


def main():
    try:
        request = json.loads(sys.stdin.buffer.read().decode("utf-8"))
        result = execute(request)
        code = 0
    except ClipboardError as error:
        result = {"ok": False, "code": error.code, "may_have_changes": error.may_have_changes}
        code = 1
    except (ValueError, UnicodeError):
        result, code = {"ok": False, "code": "invalid_request", "may_have_changes": False}, 1
    except Exception:
        result, code = {"ok": False, "code": "internal_error", "may_have_changes": True}, 1
    print(json.dumps(result, ensure_ascii=True))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
