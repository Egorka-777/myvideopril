import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace as NS
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("subtitles", Path(__file__).parents[1] / "tools/subtitles/worker.py")
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)


def segment(words, end=3, **extra):
    return NS(words=[NS(word=t, start=a, end=b) for t, a, b in words], end=end,
              no_speech_prob=extra.get("no_speech_prob", .01), avg_logprob=extra.get("avg_logprob", -.1))


class SubtitlesTests(unittest.TestCase):
    def test_words_keep_gaps_and_original_language(self):
        result = w.timing_from_segments([segment([(" Привет", .4, .9), (" мир", 2, 2.5)])], 3)
        self.assertEqual(result["Text"], "Привет мир")
        self.assertEqual(result["Alignment"]["character_start_times_seconds"], [.4, 2])
        self.assertEqual(result["Alignment"]["character_end_times_seconds"], [.9, 2.5])

    def test_silence_is_not_a_caption(self):
        with self.assertRaisesRegex(ValueError, "Речь не найдена"):
            w.timing_from_segments([segment([("hallucination", 0, 2)], no_speech_prob=.9)], 3)

    def test_overlaps_and_nonfinite_times(self):
        result = w.timing_from_segments([segment([("one", .2, .8), ("two", .7, 1.2), ("bad", float("nan"), 2), ("tail", 2, 5)])], 3)
        self.assertEqual(result["Alignment"]["character_start_times_seconds"], [.2, .8, 2])
        self.assertEqual(result["Alignment"]["character_end_times_seconds"], [.8, 1.2, 3])

    def test_batch_uses_one_local_model_and_keeps_source(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / "голос.mp3"; source.write_bytes(b"audio fixture")
            calls = []
            class Model:
                def __init__(self, *args, **kwargs):
                    calls.append((args, kwargs))
                def transcribe(self, path, **kwargs):
                    self_test.assertTrue(kwargs["word_timestamps"])
                    self_test.assertEqual(kwargs["task"], "transcribe")
                    return iter([segment([("Hello", .3, .8), ("world", 1.1, 1.8)])]), NS(duration=2, language="en")
            self_test = self
            digest = w.fingerprint(source)
            job = dict(models=str(root), items=[dict(audio=str(source), hash=digest, output=str(root / f"{i}.json")) for i in range(2)])
            w.execute(job, Model)
            self.assertEqual(len(calls), 1)
            self.assertTrue(calls[0][1]["local_files_only"])
            self.assertEqual(calls[0][1]["cpu_threads"], 2)
            data = json.loads((root / "0.json").read_text(encoding="utf-8"))
            self.assertEqual(data["AudioHash"], digest)
            self.assertEqual(data["Source"], "faster-whisper/small")
            self.assertEqual(w.fingerprint(source), digest)
            self.assertFalse(list(root.glob("*.tmp")))
            job["items"][0]["hash"] = "wrong"
            with self.assertRaisesRegex(ValueError, "изменился"):
                w.execute(job, Model)


if __name__ == "__main__":
    unittest.main()
