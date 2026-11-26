"""Run with the prepared runtime: python -m unittest discover -s tests -p video_text_test.py -v.
Set VIDEO_TEXT_REAL_MODEL=1 to include real bundled DB-model inference.
"""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("video_text", Path(__file__).parents[1] / "tools/video-text/worker.py")
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)


def quad(rect):
    x, y, r, b = rect
    return [[x, y], [r, y], [r, b], [x, b]]


class TrackingTests(unittest.TestCase):
    def test_one_frame_text_starts_at_actual_frame_and_ends_first_absent(self):
        tracker = w.Tracker(640, 360)
        self.assertEqual(tracker.update(0, []), [])
        self.assertEqual(tracker.update(.125, [(20, 30, 120, 60)]), [])
        self.assertEqual(tracker.update(.25, []), [dict(Start=.125, End=.25, X=18, Y=28, Width=104, Height=34)])
        self.assertEqual(tracker.finish(1), [])

    def test_text_changes_union_and_clamps_padding(self):
        tracker = w.Tracker(200, 100)
        tracker.update(.1, [(1, 1, 100, 30)])
        tracker.update(.2, [(0, 0, 150, 31)])
        tracker.update(.3, [(0, 0, 199, 33)])
        self.assertEqual(tracker.finish(.4), [dict(Start=.1, End=.4, X=0, Y=0, Width=200, Height=35)])

    def test_missing_frame_is_not_bridged(self):
        tracker = w.Tracker(200, 100)
        box = [(20, 30, 100, 50)]
        tracker.update(0, box)
        first = tracker.update(.1, [])
        tracker.update(.2, box)
        second = tracker.finish(.3)
        self.assertEqual([(r["Start"], r["End"]) for r in first + second], [(0, .1), (.2, .3)])

    def test_nonoverlapping_simultaneous_lines_stay_separate(self):
        tracker = w.Tracker(500, 300)
        tracker.update(0, [(20, 20, 200, 50), (20, 150, 200, 180)])
        self.assertEqual(len(tracker.finish(1)), 2)

    def test_moved_text_does_not_use_historical_union_for_matching(self):
        tracker = w.Tracker(500, 300)
        tracker.update(0, [(0, 20, 100, 50)])
        tracker.update(.1, [(50, 20, 150, 50)])
        tracker.update(.2, [(100, 20, 200, 50)])
        ended = tracker.update(.3, [(0, 20, 40, 50)])
        self.assertEqual(len(ended), 1)
        self.assertEqual(ended[0]["End"], .3)
        self.assertEqual(tracker.finish(.4)[0]["Start"], .3)

    def test_words_group_but_not_different_lines_or_remote_labels(self):
        boxes = [(10, 20, 60, 40), (67, 21, 120, 41), (10, 60, 100, 80), (300, 20, 380, 40)]
        self.assertEqual(w.group_boxes(boxes), [(10., 20., 120., 41.), (300., 20., 380., 40.), (10., 60., 100., 80.)])

    def test_rotation_matrix_convention(self):
        self.assertEqual(w.rotation_of({"side_data_list": [{"rotation": -90}]}), 3)
        with self.assertRaisesRegex(ValueError, "right-angle"):
            w.rotation_of({"side_data_list": [{"rotation": 45}]})


class VideoTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which("ffmpeg") or not shutil.which("ffprobe"):
            raise unittest.SkipTest("ffmpeg/ffprobe required")
        try:
            import cv2
            import numpy
        except ImportError:
            raise unittest.SkipTest("run with prepared video-text runtime")
        cls.temp = tempfile.TemporaryDirectory(prefix="video-text-tests-")
        cls.root = Path(cls.temp.name)
        cls.base = cls.root / "base.mp4"
        cls.ffmpeg("-f", "lavfi", "-i", "color=c=black:s=640x360:r=12:d=1", "-c:v", "libx264",
                   "-bf", "3", "-pix_fmt", "yuv420p", str(cls.base))

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    @staticmethod
    def ffmpeg(*args):
        result = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args],
                                capture_output=True, encoding="utf-8", errors="replace")
        if result.returncode:
            raise AssertionError(result.stderr)

    def run_video(self, source, detect, duration=1, width=640, height=360):
        output = self.root / (self.id().split(".")[-1] + ".json")
        with contextlib.redirect_stdout(io.StringIO()) as progress:
            w.detect_video(dict(source=str(source), output=str(output), duration=duration, width=width, height=height), detect)
        self.assertEqual(json.loads(progress.getvalue().splitlines()[-1])["stage"], "complete")
        return json.loads(output.read_text(encoding="utf-8"))

    def test_decodes_every_b_frame_and_retains_single_frame_detection(self):
        frames = []
        def detect(frame):
            frames.append(frame.shape)
            return ([quad((100, 60, 220, 100))] if len(frames) in (4, 7, 8, 9) else []), 0
        result = self.run_video(self.base, detect)
        self.assertEqual(len(frames), 12)
        self.assertEqual(result["version"], 1)
        self.assertEqual([(r["Start"], r["End"]) for r in result["regions"]], [(.25, .333333), (.5, .75)])

    def test_no_text_is_valid_empty_result(self):
        self.assertEqual(self.run_video(self.base, lambda frame: ([], 0)), dict(version=1, regions=[]))

    def test_cli_success_publishes_exact_contract(self):
        job = self.root / "good-job.json"
        output = self.root / "cli-result.json"
        job.write_text(json.dumps(dict(source=str(self.base), output=str(output), duration=1, width=640, height=360)), encoding="utf-8")
        result = subprocess.run([sys.executable, str(Path(w.__file__)), "--job", str(job)],
                                capture_output=True, encoding="utf-8", errors="replace")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        progress = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(progress[-1]["stage"], "complete")
        self.assertEqual(progress[-1]["frames"], 12)
        self.assertEqual(json.loads(output.read_text(encoding="utf-8")), dict(version=1, regions=[]))

    def test_vfr_uses_presentation_timestamps_not_frame_index(self):
        video = self.root / "vfr.mkv"
        self.ffmpeg("-i", str(self.base), "-vf", "select='eq(n,0)+eq(n,1)+eq(n,4)+eq(n,10)'",
                    "-fps_mode", "vfr", "-c:v", "libx264", str(video))
        seen = []
        def detect(frame):
            seen.append(1)
            return ([quad((100, 60, 220, 100))] if len(seen) == 2 else []), 0
        result = self.run_video(video, detect)
        self.assertEqual(len(seen), 4)
        self.assertAlmostEqual(result["regions"][0]["Start"], .083, places=3)
        self.assertAlmostEqual(result["regions"][0]["End"], .333, places=3)

    def test_nonzero_container_start_is_normalized_like_ffmpeg(self):
        video = self.root / "offset.mp4"
        self.ffmpeg("-i", str(self.base), "-c", "copy", "-output_ts_offset", "5", str(video))
        seen = []
        def detect(frame):
            seen.append(1)
            return ([quad((100, 60, 220, 100))] if len(seen) == 4 else []), 0
        region = self.run_video(video, detect)["regions"][0]
        self.assertAlmostEqual(region["Start"], .25)
        self.assertAlmostEqual(region["End"], .333333)

    def test_rotation_and_source_display_dimension_scaling(self):
        video = self.root / "rotated.mp4"
        # Positive ffprobe rotation is CCW; OpenCV auto-rotation is disabled.
        self.ffmpeg("-display_rotation", "90", "-i", str(self.base), "-c", "copy", str(video))
        _, metadata, _ = w.probe_metadata(video)
        self.assertEqual(w.rotation_of(metadata), 1)
        seen = []
        def detect(frame):
            seen.append(frame.shape)
            return [quad((10, 20, 110, 60))], 0
        region = self.run_video(video, detect, width=720, height=1280)["regions"][0]
        self.assertEqual(set(seen), {(640, 360, 3)})
        self.assertEqual((region["X"], region["Y"], region["Width"], region["Height"]), (15, 35, 210, 90))
        self.assertEqual((region["Start"], region["End"]), (0, 1))

    def test_rotation_pixel_direction_matches_display_matrix(self):
        import numpy as np
        frame = np.arange(18, dtype=np.uint8).reshape((2, 3, 3))
        self.assertTrue(np.array_equal(w.display_frame(frame, 1), np.rot90(frame, 1)))
        self.assertTrue(np.array_equal(w.display_frame(frame, 3), np.rot90(frame, -1)))

    def test_decode_failure_never_publishes_partial_output(self):
        video = self.root / "truncated.mp4"
        self.ffmpeg("-i", str(self.base), "-c", "copy", "-movflags", "+faststart", str(video))
        content = video.read_bytes()
        video.write_bytes(content[:-max(300, len(content)//5)])
        output = self.root / "truncated-result.json"
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises((RuntimeError, ValueError)):
            w.detect_video(dict(source=str(video), output=str(output), duration=1, width=640, height=360), lambda f: ([], 0))
        self.assertFalse(output.exists())
        self.assertFalse(list(self.root.glob(".video-text-*.tmp")))

    def test_cli_failure_is_nonzero_with_json_error(self):
        job = self.root / "bad-job.json"
        job.write_text(json.dumps(dict(source=str(self.root / "missing.mp4"), output=str(self.root / "never.json"),
                                       duration=1, width=640, height=360)), encoding="utf-8")
        result = subprocess.run([sys.executable, str(Path(w.__file__)), "--job", str(job)],
                                capture_output=True, encoding="utf-8", errors="replace")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout.splitlines()[-1])["stage"], "error")

    @unittest.skipUnless(os.environ.get("VIDEO_TEXT_REAL_MODEL") == "1", "set VIDEO_TEXT_REAL_MODEL=1 for real DB inference")
    def test_real_model_generated_one_frame_latin_and_cyrillic_text(self):
        video = self.root / "real-text.mp4"
        caption = self.root / "caption.txt"
        caption.write_text("HELLO WORLD\nПРИВЕТ МИР", encoding="utf-8")
        # Forward slashes and escaped drive colon for ffmpeg's filter parser.
        font = Path(os.environ.get("WINDIR", "/usr/share/fonts/truetype/dejavu"))
        font = font / "Fonts/arial.ttf" if os.name == "nt" else font / "DejaVuSans.ttf"
        escaped_font = str(font).replace("\\", "/").replace(":", "\\:")
        escaped_caption = str(caption).replace("\\", "/").replace(":", "\\:")
        draw = ("drawtext=fontfile='%s':textfile='%s':fontsize=42:fontcolor=white:x=100:y=100:"
                "line_spacing=25:enable='eq(n,3)+between(n,6,8)'" % (escaped_font, escaped_caption))
        self.ffmpeg("-i", str(self.base), "-vf", draw, "-c:v", "libx264", "-crf", "16", str(video))
        detect = w.detector()
        result = self.run_video(video, detect)
        regions = result["regions"]
        self.assertEqual(len(regions), 4, regions)  # two lines, two intervals, including one-frame flash
        self.assertEqual(sorted((r["Start"], r["End"]) for r in regions), [(.25, .333333)]*2 + [(.5, .75)]*2)
        for region in regions:
            self.assertLess(region["X"], 110)
            self.assertGreater(region["Width"], 200)
            self.assertGreater(region["Height"], 25)


if __name__ == "__main__":
    unittest.main()
