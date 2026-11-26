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
    return NS(words=[NS(word=t, start=a, end=b) for t, a, b in words], start=0, end=end,
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

    def test_complete_chunks_keeps_utterances_and_covers_vad_exclusions(self):
        chunks = [dict(start=76032, end=121088), dict(start=126208, end=362752),
                  dict(start=479488, end=498944), dict(start=587008, end=639232),
                  dict(start=679680, end=688448)]
        original = [dict(c) for c in chunks]
        result = w.complete_chunks(chunks, 688448)
        self.assertEqual(chunks, original)
        self.assertEqual(result[0], dict(start=0, end=76032))
        self.assertIn(dict(start=498944, end=587008), result)
        for chunk in original:
            self.assertIn(chunk, result)
        self.assertEqual(result[-1]["end"], 688448)
        self.assertTrue(all(a["end"] == b["start"] for a, b in zip(result, result[1:])))
        self.assertTrue(all(c["end"] > c["start"] for c in result))

    def test_complete_chunks_bounds_long_vad_exclusions(self):
        self.assertEqual(w.complete_chunks([], 0), [])
        self.assertEqual(w.complete_chunks([], 65 * 16000),
                         [dict(start=0, end=30 * 16000),
                          dict(start=30 * 16000, end=60 * 16000),
                          dict(start=60 * 16000, end=65 * 16000)])
        self.assertEqual(w.complete_chunks([dict(start=16000, end=32000)], 48000),
                         [dict(start=0, end=16000), dict(start=16000, end=32000),
                          dict(start=32000, end=48000)])

    def test_completed_intervals_keep_absolute_words_and_reject_silence(self):
        class Model:
            def transcribe(self, audio, **kwargs):
                self_test.assertFalse(kwargs["vad_filter"])
                first = audio[0]
                if first == 0:
                    words = [("Hell", 0, .4), ("no!", .4, .8)]
                elif first == 16000:
                    return iter([segment([("hallucination", 0, .4)], no_speech_prob=.9)]), None
                else:
                    words = [("Please", .1, .3), ("back!", .3, .8)]
                return iter([segment(words, end=.8)]), None
        self_test = self
        chunks = w.complete_chunks([dict(start=16000, end=32000)], 48000)
        segments = w.transcribe_utterances(Model(), list(range(48000)), chunks, "en")
        result = w.timing_from_segments(segments, 3)
        self.assertEqual(result["Text"], "Hell no! Please back!")
        self.assertEqual(result["Alignment"]["character_start_times_seconds"], [0, .4, 2.1, 2.3])
        self.assertEqual([s.start for s in segments], [0, 1, 2])
        self.assertAlmostEqual(segments[-1].end, 2.8)

    def test_worker_and_runtime_cache_versions_match(self):
        source = (Path(__file__).parents[1] / "src/LocalAudioTranscription.cs").read_text(encoding="utf-8-sig")
        self.assertIn('public const string Version = "' + w.VERSION + '";', source)
        self.assertNotEqual(w.VERSION, "2026-11-18-utterance-timing-v2")

    def test_batch_uses_one_local_model_and_keeps_source(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / "голос.mp3"; source.write_bytes(b"audio fixture")
            calls = []
            class Model:
                def __init__(self, *args, **kwargs):
                    calls.append((args, kwargs))
                def detect_language(self, audio, **kwargs):
                    return "en", 1, []
                def transcribe(self, audio, **kwargs):
                    self_test.assertTrue(kwargs["word_timestamps"])
                    self_test.assertEqual(kwargs["task"], "transcribe")
                    self_test.assertEqual(kwargs["language"], "en")
                    self_test.assertFalse(kwargs["vad_filter"])
                    self_test.assertEqual(len(audio), 16000)
                    return iter([segment([("Hello", .1, .4), ("world", .5, .9)])]), NS(language="en")
            self_test = self
            digest = w.fingerprint(source)
            job = dict(models=str(root), items=[dict(audio=str(source), hash=digest, output=str(root / f"{i}.json")) for i in range(2)])
            loader = lambda path: [0.0] * 48000
            utterance = lambda audio: [dict(start=16000, end=32000)]
            w.execute(job, Model, loader, utterance)
            self.assertEqual(len(calls), 1)
            self.assertTrue(calls[0][1]["local_files_only"])
            self.assertEqual(calls[0][1]["cpu_threads"], max(2, (w.os.cpu_count() or 4) // 2))
            data = json.loads((root / "0.json").read_text(encoding="utf-8"))
            self.assertEqual(data["AudioHash"], digest)
            self.assertEqual(data["Source"], "faster-whisper/small")
            self.assertEqual(data["Language"], "en")
            self.assertEqual(data["Alignment"]["character_start_times_seconds"], [1.1, 1.5])
            self.assertEqual(w.fingerprint(source), digest)
            self.assertFalse(list(root.glob("*.tmp")))
            job["items"][0]["hash"] = "wrong"
            with self.assertRaisesRegex(ValueError, "изменился"):
                w.execute(job, Model, loader, utterance)


if __name__ == "__main__":
    unittest.main()
