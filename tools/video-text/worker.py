"""Local scene-text boxes only. Requires ffprobe on PATH; never runs recognition.

RapidOCR 1.4.4's TextDetector accepts BGR arrays and returns (quadrilaterals,
elapsed). Use that class directly: RapidOCR() also constructs unused rec/cls
sessions even with use_rec=False. The PP-OCRv4 DB model ships in the wheel.
"""
import argparse
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

VERSION = 1


def emit(stage, message, **fields):
    print(json.dumps(dict(stage=stage, message=message, **fields), ensure_ascii=False), flush=True)


def detector():
    import rapidocr_onnxruntime
    from rapidocr_onnxruntime.ch_ppocr_det import TextDetector
    from rapidocr_onnxruntime.utils import read_yaml, update_model_path

    root = Path(rapidocr_onnxruntime.__file__).parent
    config = update_model_path(read_yaml(root / "config.yaml"))["Det"]
    # Keep full input resolution (rounding to DB's multiple of 32 only), without
    # upscaling small clips to 736px or shrinking large source glyphs away.
    config.update(limit_type="min", limit_side_len=32,
                  intra_op_num_threads=min(4, os.cpu_count() or 1), inter_op_num_threads=1)
    return TextDetector(config)


def union(a, b):
    return min(a[0], b[0]), min(a[1], b[1]), max(a[2], b[2]), max(a[3], b[3])


def area(a):
    return max(0, a[2] - a[0]) * max(0, a[3] - a[1])


def overlap(a, b):
    intersection = max(0, min(a[2], b[2]) - max(a[0], b[0])) * max(0, min(a[3], b[3]) - max(a[1], b[1]))
    return intersection / min(area(a), area(b)) if intersection else 0.0


def group_boxes(boxes):
    """Join adjacent words on the same line, not separate stacked captions.

    Geometric grouping is intentionally independent of recognized content.
    Rectangles here are unpadded (x0,y0,x1,y1) in display coordinates.
    """
    boxes = sorted((tuple(map(float, b)) for b in boxes if area(b) > 0), key=lambda b: (b[1], b[0]))
    parents = list(range(len(boxes)))

    def root(i):
        while parents[i] != i:
            parents[i] = parents[parents[i]]
            i = parents[i]
        return i

    for i, a in enumerate(boxes):
        ah = a[3] - a[1]
        for j in range(i + 1, len(boxes)):
            b = boxes[j]
            if b[1] >= a[3]:
                break
            bh = b[3] - b[1]
            vertical = max(0, min(a[3], b[3]) - max(a[1], b[1]))
            gap = max(0, max(a[0], b[0]) - min(a[2], b[2]))
            if vertical >= .6 * max(ah, bh) and gap <= .9 * max(ah, bh):
                parents[root(j)] = root(i)
    grouped = {}
    for i, box in enumerate(boxes):
        key = root(i)
        grouped[key] = union(grouped[key], box) if key in grouped else box
    return sorted(grouped.values(), key=lambda b: (b[1], b[0]))


class Tracker:
    """Only consecutive-frame overlap continues a track; no grace/bridging.

    Match against the last box, never the accumulated union (which could
    otherwise join unrelated text inside an old swept-out area).
    """
    def __init__(self, width, height):
        self.width, self.height = width, height
        self.active = []

    def region(self, track, end):
        box = track["bounds"]
        padding = max(2, min(6, math.ceil((box[3] - box[1]) * .06)))
        x, y = max(0, math.floor(box[0]) - padding), max(0, math.floor(box[1]) - padding)
        right, bottom = min(self.width, math.ceil(box[2]) + padding), min(self.height, math.ceil(box[3]) + padding)
        return dict(Start=track["start"], End=end, X=x, Y=y, Width=right-x, Height=bottom-y)

    def update(self, timestamp, boxes):
        # Greedy one-to-one spatial matching lets text change without resetting
        # its interval, while separate simultaneous lines retain their identity.
        pairs = sorted(((overlap(t["last"], b), i, j)
                        for i, t in enumerate(self.active) for j, b in enumerate(boxes)), reverse=True)
        used_tracks, used_boxes, following = set(), set(), []
        for score, i, j in pairs:
            if score < .4:
                break
            if i in used_tracks or j in used_boxes:
                continue
            track = self.active[i]
            track["last"] = boxes[j]
            track["bounds"] = union(track["bounds"], boxes[j])
            following.append(track)
            used_tracks.add(i)
            used_boxes.add(j)
        ended = [self.region(t, timestamp) for i, t in enumerate(self.active)
                 if i not in used_tracks and timestamp > t["start"]]
        following.extend(dict(start=timestamp, last=b, bounds=b) for j, b in enumerate(boxes) if j not in used_boxes)
        self.active = following
        return ended

    def finish(self, duration):
        ended = [self.region(t, duration) for t in self.active if duration > t["start"]]
        self.active = []
        return ended


def probe_metadata(source, executable=None):
    executable = executable or shutil.which("ffprobe")
    if not executable:
        raise RuntimeError("ffprobe is required on PATH for frame presentation timestamps and decode validation.")
    result = subprocess.run([executable, "-v", "error", "-select_streams", "v:0", "-show_streams", "-show_format",
                             "-of", "json", str(source)], capture_output=True, encoding="utf-8", errors="replace")
    if result.returncode or result.stderr.strip():
        raise RuntimeError("Cannot probe video: " + result.stderr.strip())
    metadata = json.loads(result.stdout)
    if not metadata.get("streams"):
        raise ValueError("Source has no video stream.")
    return executable, metadata["streams"][0], metadata.get("format", {})


def number(value, default=None):
    try:
        result = float(value)
        return result if math.isfinite(result) else default
    except (ValueError, TypeError):
        return default


def rotation_of(stream):
    # ffprobe display-matrix rotation is counterclockwise, unlike OpenCV's
    # CAP_PROP_ORIENTATION_META. Apply ourselves with ORIENTATION_AUTO disabled.
    angle = next((s["rotation"] for s in stream.get("side_data_list", []) if "rotation" in s),
                 stream.get("tags", {}).get("rotate", 0))
    angle = float(angle)
    quarter = round(angle / 90)
    if abs(angle - quarter * 90) > .1:
        raise ValueError("Only right-angle display rotation is supported: " + str(angle))
    return quarter % 4


def display_frame(frame, rotation):
    import cv2
    if rotation == 1:
        return cv2.rotate(frame, cv2.ROTATE_90_COUNTERCLOCKWISE)
    if rotation == 2:
        return cv2.rotate(frame, cv2.ROTATE_180)
    if rotation == 3:
        return cv2.rotate(frame, cv2.ROTATE_90_CLOCKWISE)
    return frame


class FrameClock:
    """Stream ffprobe's decoder presentation-order frames, bounded RAM.

    Unlike OpenCV POS_MSEC this preserves VFR, B-frame and non-zero-start PTS.
    stderr goes to disk, avoiding pipe deadlock. Any decoder error is fatal.
    """
    def __init__(self, executable, source):
        self.errors = tempfile.TemporaryFile()
        self.process = subprocess.Popen([executable, "-v", "error", "-err_detect", "explode",
            "-select_streams", "v:0", "-show_frames", "-show_entries",
            "frame=best_effort_timestamp_time,duration_time,pkt_duration_time",
            "-of", "compact=p=0:nk=0", str(source)], stdout=subprocess.PIPE, stderr=self.errors,
            text=True, encoding="utf-8", errors="replace")

    def next(self):
        for line in self.process.stdout:
            fields = dict(part.split("=", 1) for part in line.strip().split("|") if "=" in part)
            if "best_effort_timestamp_time" not in fields:
                continue  # e.g. blank SEI side-data line
            timestamp = number(fields["best_effort_timestamp_time"])
            if timestamp is None:
                raise RuntimeError("Video frame has no valid presentation timestamp.")
            return timestamp, number(fields.get("duration_time"), number(fields.get("pkt_duration_time"), 0))
        code = self.process.wait()
        self.errors.seek(0)
        diagnostic = self.errors.read(6000).decode("utf-8", errors="replace").strip()
        if code or diagnostic:
            raise RuntimeError("Video decode failed: " + (diagnostic or str(code)))
        return None

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
        self.process.wait()
        self.process.stdout.close()
        self.errors.close()


def detect_video(job, detect):
    """Atomically stream finished intervals; do not retain frames or all boxes."""
    import cv2
    source, output = Path(job["source"]), Path(job["output"])
    duration = number(job["duration"])
    width, height = int(job["width"]), int(job["height"])
    if duration is None or duration <= 0 or width <= 0 or height <= 0:
        raise ValueError("Job duration and display dimensions must be positive and finite.")
    if not source.is_file():
        raise FileNotFoundError("Source video does not exist: " + str(source))
    if source.resolve() == output.resolve():
        raise ValueError("Output must not overwrite the source video.")
    executable, stream, container = probe_metadata(source, job.get("ffprobe"))
    origin = number(container.get("start_time"), number(stream.get("start_time"), 0))
    rotation = rotation_of(stream)
    capture = cv2.VideoCapture(str(source), cv2.CAP_FFMPEG)
    if not capture.isOpened():
        raise RuntimeError("OpenCV cannot open the source video.")
    capture.set(cv2.CAP_PROP_ORIENTATION_AUTO, 0)
    clock, temporary = None, None
    tracker = Tracker(width, height)
    count, region_count, previous, last_end, last_report = 0, 0, None, None, 0
    try:
        clock = FrameClock(executable, source)
        output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=output.parent,
                                         prefix=".video-text-", suffix=".tmp", delete=False) as target:
            temporary = Path(target.name)
            target.write('{"version":1,"regions":[')

            def write_regions(regions):
                nonlocal region_count
                for region in regions:
                    if region_count:
                        target.write(",")
                    json.dump(region, target, allow_nan=False)
                    region_count += 1

            while True:
                timing = clock.next()
                ok, frame = capture.read()
                if timing is None:
                    if ok:
                        raise RuntimeError("OpenCV decoded more frames than ffprobe; presentation timing cannot be trusted.")
                    break
                if not ok or frame is None:
                    raise RuntimeError("OpenCV decode stopped before ffprobe frame %d (truncated or unsupported video)." % count)
                timestamp, frame_duration = timing
                timestamp -= origin
                if previous is not None and timestamp <= previous:
                    raise RuntimeError("Non-increasing frame presentation timestamps; cannot form exact text intervals.")
                previous, last_end = timestamp, timestamp + frame_duration
                frame = display_frame(frame, rotation)
                fh, fw = frame.shape[:2]
                if (fw, fh) != ((int(stream["height"]), int(stream["width"])) if rotation % 2 else
                                (int(stream["width"]), int(stream["height"]))):
                    raise RuntimeError("Unexpected OpenCV display rotation or changing frame dimensions.")
                boxes, _ = detect(frame)
                rectangles = []
                for box in boxes if boxes is not None else []:
                    xs, ys = [float(p[0]) for p in box], [float(p[1]) for p in box]
                    rectangles.append((min(xs)*width/fw, min(ys)*height/fh, max(xs)*width/fw, max(ys)*height/fh))
                if timestamp < duration:
                    write_regions(tracker.update(max(0, timestamp), group_boxes(rectangles)))
                count += 1
                now = time.monotonic()
                if now - last_report >= 1:
                    emit("detect", "Detecting visible text, frame %d" % count,
                         percent=min(99, max(0, int(100 * timestamp / duration))), frames=count)
                    last_report = now
            if not count:
                raise RuntimeError("No video frames were decoded.")
            expected_count = number(stream.get("nb_frames"))
            if expected_count is not None and count != int(expected_count):
                raise RuntimeError("Truncated video: decoded %d of %d declared frames." % (count, expected_count))
            # A damaged tail can look like a clean EOF in both decoders. Check
            # the declared video timeline as well, never the longer audio tail.
            stream_duration = number(stream.get("duration"))
            expected_end = (number(stream.get("start_time"), origin) - origin + stream_duration
                            if stream_duration is not None else duration)
            tolerance = max(.05, 1.5 * (frame_duration or 0))
            if last_end + tolerance < expected_end:
                raise RuntimeError("Truncated video timeline: decoded to %.6fs, expected %.6fs." % (last_end, expected_end))
            write_regions(tracker.finish(duration))
            target.write("]}")
        os.replace(temporary, output)
        emit("complete", "Detected %d text intervals in %d frames." % (region_count, count), percent=100, frames=count, regions=region_count)
    finally:
        capture.release()
        if clock:
            clock.close()
        if temporary and temporary.exists():
            temporary.unlink()


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=path.parent, delete=False) as target:
            temporary = Path(target.name)
            json.dump(value, target, allow_nan=False)
        os.replace(temporary, path)
    finally:
        if temporary and temporary.exists():
            temporary.unlink()


def main():
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--job", type=Path)
    mode.add_argument("--prepare", type=Path)
    args = parser.parse_args()
    try:
        emit("model", "Loading local PP-OCRv4 DB text detector (no recognition).")
        detect = detector()
        if args.prepare:
            import numpy as np
            detect(np.zeros((64, 64, 3), dtype=np.uint8))
            atomic_json(args.prepare / "runtime.json", dict(python=str(Path(sys.executable).resolve()), version="1"))
            emit("prepare", "Local video text detector is ready.", percent=100)
        else:
            with args.job.open(encoding="utf-8-sig") as source:
                job = json.load(source)
            detect_video(job, detect)
        return 0
    except Exception as error:
        emit("error", str(error) or type(error).__name__)
        return 1


if __name__ == "__main__":
    sys.exit(main())
