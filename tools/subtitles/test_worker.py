"""Offline contracts: no_speech is opt-in; real recognition failures remain fatal."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace as NS
import unittest

import worker


class WorkerTests(unittest.TestCase):
    def segment(self, probability=.1):
        return NS(no_speech_prob=probability, avg_logprob=-.2, start=0, end=1,
                  words=[NS(word=" Speech ", start=.2, end=.7)])

    def test_strict_and_optional_empty(self):
        for segments in ([], [self.segment(.9)]):
            with self.assertRaisesRegex(ValueError, "Речь не найдена"):
                worker.timing_from_segments(segments, 1)
            result = worker.timing_from_segments(segments, 1, allow_empty=True)
            self.assertEqual(result["Status"], "no_speech")
            self.assertEqual(result["Text"], "")
            self.assertTrue(all(not v for v in result["Alignment"].values()))

    def test_speech_unchanged(self):
        strict = worker.timing_from_segments([self.segment()], 1)
        optional = worker.timing_from_segments([self.segment()], 1, allow_empty=True)
        self.assertEqual(strict, optional)
        self.assertEqual(strict["Status"], "speech")
        self.assertEqual(strict["Text"], "Speech")
        self.assertEqual(strict["Alignment"]["character_start_times_seconds"], [.2])

    def test_generator_failure_not_no_speech(self):
        def broken():
            raise RuntimeError("decoder failed")
            yield
        with self.assertRaisesRegex(RuntimeError, "decoder failed"):
            worker.timing_from_segments(broken(), 1, allow_empty=True)

    def test_execute_opt_in_and_failures(self):
        class Model:
            def detect_language(self, *args, **kwargs):
                return ("en",)

            def transcribe(self, *args, **kwargs):
                return iter([]), None

        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            audio = Path(directory) / "source.wav"
            audio.write_bytes(b"fixture")
            output = Path(directory) / "result.json"
            job = dict(models=directory, items=[dict(audio=str(audio), hash=worker.fingerprint(audio), output=str(output))])
            arguments = dict(model_factory=lambda *a, **kw: Model(),
                             audio_loader=lambda path: [0.] * 16000,
                             chunk_detector=lambda samples: [dict(start=0, end=len(samples))])
            with self.assertRaisesRegex(ValueError, "Речь не найдена"):
                worker.execute(job, **arguments)
            self.assertFalse(output.exists())
            job["allow_empty"] = True
            worker.execute(job, **arguments)
            result = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(result["Status"], "no_speech")
            self.assertEqual(result["AudioHash"], worker.fingerprint(audio))
            self.assertEqual(result["Version"], worker.VERSION)
            output.unlink()
            # An option does not suppress model, decoder, language detection or ASR failures.
            for stage in ("model", "decode", "language", "transcribe"):
                def fail(*args, **kwargs):
                    raise RuntimeError(stage + " failed")
                class BrokenModel(Model):
                    detect_language = fail if stage == "language" else Model.detect_language
                    transcribe = fail if stage == "transcribe" else Model.transcribe
                bad = dict(arguments, model_factory=fail if stage == "model" else lambda *a, **kw: BrokenModel(),
                           audio_loader=fail if stage == "decode" else arguments["audio_loader"])
                with self.assertRaisesRegex(RuntimeError, stage + " failed"):
                    worker.execute(job, **bad)
                self.assertFalse(output.exists())
            with self.assertRaisesRegex(ValueError, "длительность"):
                worker.execute(job, **dict(arguments, audio_loader=lambda path: []))
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
