"""Boundary refinement unit tests and opt-in local source smoke test.

Run with the installed highlights Python. --inspect only diagnoses short contexts;
--real runs refinement against the two reported source files (no export/deployment).
"""
import json
from pathlib import Path
import subprocess
import sys
import unittest

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools" / "highlights"))

ROOT = Path(r"C:\Users\hackerman\AppData\Local\VideoBatchDesktop\highlights")
FFMPEG = Path(r"D:\VideoBatch\VideoBatch\tools\ffmpeg.exe")


def production_sources():
    saved = json.loads((ROOT / 'last-youtube-result.json').read_text(encoding='utf-8'))
    for video in ('qeM1RX3GBG0', 'gFuEs4O35Nw'):
        source = next(s for s in saved['sources'] if s['videoId'] == video)
        words = json.loads(Path(source['transcript']).read_text(encoding='utf-8'))['words']
        yield video, dict(root=str(ROOT), source=source['source'], ffmpeg=str(FFMPEG)), words, source['clips']


def inspect_sources():
    from whisper_runtime import create_model
    from speech_boundaries import _audio, _recognize, _silences
    model = create_model("small", ROOT)
    for video, job, words, clips in production_sources():
        for clip in clips:
            for edge in ('start', 'end'):
                t = clip[edge]
                origin = max(0, t - 10)
                audio = _audio(job, origin, t + 10)
                decoded = _recognize(model, audio, origin)
                gaps = _silences(audio, origin)
                print(json.dumps(dict(video=video, clip=clip['id'], edge=edge, original=t,
                    cached=[w for w in words if w['end'] >= t - 10 and w['start'] <= t + 2],
                    decoded=decoded, gaps=gaps),
                    ensure_ascii=True), flush=True)


from speech_boundaries import BoundaryError, _boundary, _choose, _following, _silences, _validate, refine_clips


class BoundaryTests(unittest.TestCase):
    def test_last_pause_not_intra_word_stop(self):
        words = [dict(text="125", start=135, end=135.76), dict(text="то", start=136.32, end=136.48)]
        self.assertAlmostEqual(_choose(words, 0, [(135.88, 136.04), (136.13, 136.25)],
                                       135.96, "end", 605), 136.19)

    def test_125_cached_following_word_starts_before_release(self):
        from unittest.mock import patch
        cached = [dict(text='125', start=134.98, end=135.84),
                  dict(text='то', start=135.84, end=136.44)]
        decoded = [dict(text='125', start=134.98, end=135.76),
                   dict(text='то', start=136.32, end=136.48)]
        with patch('speech_boundaries._audio', return_value=np.zeros(320000)), \
             patch('speech_boundaries._recognize', return_value=decoded), \
             patch('speech_boundaries._silences', return_value=[(135.88, 136.04), (136.13, 136.26)]):
            self.assertAlmostEqual(_boundary(None, {}, 605, cached, 0, 135.96, 'end'), 136.195)

    def test_good_short_pause_unchanged(self):
        words = [dict(text="плаву", start=252.4, end=252.74), dict(text="если", start=252.84, end=252.96)]
        self.assertEqual(_choose(words, 0, [(252.729, 252.844)], 252.82, "end", 600), 252.82)

    def test_word_alignment_inside_pause_uses_measured_midpoint(self):
        words = [dict(text='before', start=83, end=84.44), dict(text='next', start=85.1, end=85.76)]
        self.assertEqual(_choose(words, 1, [(84.4336875, 85.2149375)], 84.98, 'start', 600), 84.98)
        words = [dict(text='end', start=704.6, end=705.2), dict(text='next', start=705.36, end=705.66)]
        self.assertEqual(_choose(words, 0, [(705.2115, 705.393625)], 705.36, 'end', 1000), 705.36)

    def test_adjacent_word_alignment_can_touch_inside_same_pause(self):
        words = [dict(text='simple', start=271.09, end=271.79), dict(text='you', start=271.79, end=272.33)]
        self.assertEqual(_choose(words, 0, [(271.7186875, 272.28575)], 271.87, 'end', 600), 271.87)

    def test_no_pause_uses_cached_interword_timing(self):
        words = [dict(text="a", start=1, end=2), dict(text="b", start=2, end=3)]
        self.assertEqual(_choose(words, 0, [], 2, "end", 10), 2)
        words[1]['start'] = 2.2
        self.assertAlmostEqual(_choose(words, 0, [], 1.9, "end", 10), 2.1)
        self.assertEqual(_choose(words, 0, [], 2.05, "end", 10), 2.05)

    def test_following_word_uses_time_not_spelling(self):
        decoded = [dict(text='это', start=745.74, end=746.06),
                   dict(text='таких', start=746.06, end=747.16)]
        self.assertEqual(_following(decoded, dict(text='Таких', start=746.72, end=747.2)), 1)
        with self.assertRaises(BoundaryError):
            _following(decoded, dict(text='таких', start=750, end=751))

    def test_music_opening_uses_vad_or_cached_bounds_not_distant_quiet(self):
        from unittest.mock import patch
        words = [dict(text='тренду', start=51.86, end=52.26),
                 dict(text='и', start=59.72, end=60.58)]
        with patch('speech_boundaries._audio', return_value=np.zeros(320000)), \
             patch('speech_boundaries._silences', return_value=[(54.224, 54.401)]), \
             patch('speech_boundaries._speech_gaps', return_value=[(52.256, 60.48)]):
            self.assertEqual(_boundary(None, {}, 400, words, 1, 59.6, 'start'), 59.6)
        with patch('speech_boundaries._audio', return_value=np.zeros(320000)), \
             patch('speech_boundaries._silences', return_value=[]), \
             patch('speech_boundaries._speech_gaps', return_value=[]):
            self.assertEqual(_boundary(None, {}, 400, words, 1, 59.6, 'start'), 59.6)

    def test_changed_entre_spelling_retains_verified_ending(self):
        from unittest.mock import patch
        words = [dict(text='энтре.', start=745.74, end=746.54),
                 dict(text='Таких', start=746.72, end=747.2)]
        decoded = [dict(text='это', start=745.74, end=746.06),
                   dict(text='таких', start=746.06, end=747.16)]
        with patch('speech_boundaries._audio', return_value=np.zeros(320000)), \
             patch('speech_boundaries._recognize', return_value=decoded), \
             patch('speech_boundaries._silences', return_value=[(745.9355625, 746.6761875)]):
            self.assertEqual(_boundary(None, {}, 1000, words, 0, 746.54, 'end'), 746.54)

    def test_final_word_followed_by_source_trailing_silence(self):
        words = [dict(text='end', start=500, end=501)]
        self.assertEqual(_choose(words, 0, [(501.1, 505)], 501.2, 'end', 505), 501.2)

    def test_silence_preserves_short_release(self):
        audio = np.zeros(16000, dtype=np.float32)
        audio[4000:4100] = .1
        gaps = _silences(audio, 10)
        self.assertEqual(len(gaps), 2)
        self.assertEqual(gaps[0][1], 10.25)

    def test_invalid_duration_rejected_but_overlap_allowed(self):
        with self.assertRaises(BoundaryError):
            _validate([dict(start=0, end=300.01)], {}, 600)
        _validate([dict(start=0, end=130), dict(start=129, end=260)], {}, 600)

    def test_reported_opening_456140_without_quiet_gap(self):
        from unittest.mock import patch
        words = [dict(text='previous', start=455.5, end=456.12),
                 dict(text='Если', start=456.16, end=456.4)]
        with patch('speech_boundaries._audio', return_value=np.zeros(320000)), \
             patch('speech_boundaries._silences', return_value=[]), \
             patch('speech_boundaries._speech_gaps', return_value=[]):
            self.assertEqual(_boundary(None, {}, 1018.221, words, 1, 456.140, 'start'), 456.140)

    def test_reported_ending_391600_uses_decoded_interword_timing(self):
        from unittest.mock import patch
        words = [dict(text='бенадекс', start=391.2, end=391.52),
                 dict(text='next', start=391.6, end=392)]
        decoded = [dict(text='бенадекс', start=391.2, end=391.7),
                   dict(text='next', start=391.8, end=392)]
        with patch('speech_boundaries._audio', return_value=np.zeros(320000)), \
             patch('speech_boundaries._recognize', return_value=decoded), \
             patch('speech_boundaries._silences', return_value=[]):
            self.assertAlmostEqual(_boundary(None, {}, 880.081, words, 0, 391.6, 'end'), 391.75)

    def test_new_cut_does_not_include_decoded_next_word(self):
        from unittest.mock import patch
        words = [dict(text='end', start=10, end=11), dict(text='next', start=11.8, end=12.5)]
        decoded = [dict(text='end', start=10, end=11), dict(text='next', start=11.5, end=12)]
        with patch('speech_boundaries._audio', return_value=np.zeros(320000)), \
             patch('speech_boundaries._recognize', return_value=decoded), \
             patch('speech_boundaries._silences', return_value=[(11.3, 11.9)]):
            self.assertAlmostEqual(_boundary(None, {}, 600, words, 0, 11, 'end'), 11.4)

    def test_decode_and_model_failures_propagate(self):
        from unittest.mock import patch
        words = [dict(text='a', start=10, end=11), dict(text='b', start=139, end=140)]
        for stage, error in [('create_model', RuntimeError('CUDA init failed')),
                             ('_audio', BoundaryError('Boundary audio decode failed')),
                             ('_recognize', RuntimeError('decode infrastructure failed'))]:
            with patch('speech_boundaries.create_model'), \
                 patch('speech_boundaries._audio', return_value=np.zeros(320000)), \
                 patch('speech_boundaries._silences', return_value=[]), \
                 patch('speech_boundaries._speech_gaps', return_value=[]), \
                 patch('speech_boundaries.' + stage, side_effect=error):
                # Include a following word so the ending performs local decode.
                with self.assertRaisesRegex(type(error), str(error)):
                    refine_clips(dict(root='unused'), dict(duration=500),
                                 words + [dict(text='next', start=141, end=142)],
                                 [dict(start=10, end=140)])

    def test_no_local_alignment_uses_cached_bounds(self):
        from unittest.mock import patch
        words = [dict(text='end', start=10, end=11), dict(text='next', start=11.2, end=12)]
        with patch('speech_boundaries._audio', return_value=np.zeros(320000)), \
             patch('speech_boundaries._recognize', return_value=[]), \
             patch('speech_boundaries._silences', return_value=[]):
            self.assertAlmostEqual(_boundary(None, {}, 600, words, 0, 10.9, 'end'), 11.1)

    def test_overlapping_alignment_retains_original_without_padding(self):
        from speech_boundaries import _timed_cut
        self.assertEqual(_timed_cut(391.8, 391.7, 391.6, 'end'), 391.6)

    def test_refined_overlapping_boundaries_preserved(self):
        from unittest.mock import patch, Mock
        clips = [dict(start=10, end=140), dict(start=141, end=271)]
        words = [dict(start=t, end=t + 1, text='x') for t in (10, 139, 141, 270)]
        model = Mock()
        model.model.device = 'cpu'
        with patch('speech_boundaries.create_model', return_value=model), \
             patch('speech_boundaries._boundary', side_effect=[10, 142, 141, 271]):
            result=refine_clips(dict(root='unused'), dict(duration=500), words, clips)
            self.assertEqual(result,[dict(start=10,end=142),dict(start=141,end=271)])

    def test_invalid_original_still_rejected(self):
        from unittest.mock import patch
        with patch('speech_boundaries.create_model') as load:
            with self.assertRaises(BoundaryError):
                refine_clips({}, dict(duration=500), [], [dict(start=0, end=119)])
            load.assert_not_called()

    def test_empty_does_not_load_model(self):
        self.assertEqual(refine_clips({}, {}, [], []), [])

    def test_api_copies_metadata_loads_once_and_validates_result(self):
        from unittest.mock import patch, Mock
        model = Mock()
        model.model.device = "cuda"
        clips = [dict(start=10, end=140, title="keep", text="keep")]
        words = [dict(start=10, end=11, text="first"), dict(start=139, end=140, text="last")]
        with patch("speech_boundaries.create_model", return_value=model) as load:
            with patch("speech_boundaries._boundary", side_effect=[9.9, 140.1]):
                result = refine_clips(dict(root="unused"), dict(duration=500), words, clips)
            load.assert_called_once()
            self.assertEqual(clips[0]["start"], 10)
            self.assertEqual(result[0]["title"], "keep")
            self.assertEqual(result[0]["text"], "keep")
            with patch("speech_boundaries._boundary", side_effect=[0, 301]):
                self.assertEqual(refine_clips(dict(root="unused"), dict(duration=500), words, clips), clips)


def real_sources():
    for video, job, words, clips in production_sources():
        probe = subprocess.run([str(FFMPEG.with_name('ffprobe.exe')), '-v', 'error',
            '-show_entries', 'format=duration', '-of', 'json', job['source']],
            check=True, capture_output=True, text=True)
        duration = float(json.loads(probe.stdout)['format']['duration'])
        result = refine_clips(job, dict(duration=duration), words, clips)
        summary = lambda items: [{k: c[k] for k in ('id', 'start', 'end')} for c in items]
        print(json.dumps(dict(video=video, before=summary(clips), after=summary(result)), ensure_ascii=True), flush=True)
        if video == 'qeM1RX3GBG0':
            assert any(abs(c['start'] - 456.140) < .001 for c in clips), clips
        elif video == 'gFuEs4O35Nw':
            assert any(abs(c['end'] - 391.600) < .001 for c in clips), clips
        _validate(result, job, duration)
        from speech_boundaries import _audio, _speech_gaps
        for clip in result:
            # Decode actual retained cuts. Non-speech evidence is diagnostic,
            # not a requirement: continuous speech can use word timing.
            cut = _audio(job, clip['start'], clip['end'])
            assert abs(len(cut) / 16000 - (clip['end'] - clip['start'])) < .001
            for edge in ('start', 'end'):
                t = clip[edge]
                origin = max(0, t - 10)
                audio = _audio(job, origin, min(duration, t + 10))
                gaps = _silences(audio, origin)
                evidence = [g for g in gaps if g[0] < t < g[1]]
                kind = 'amplitude'
                if not evidence and edge == 'start':
                    evidence = [g for g in _speech_gaps(audio, origin) if g[0] < t < g[1]]
                    kind = 'voice_activity'
                if not evidence:
                    kind = 'word_timing'
                print('CUT_EVIDENCE', video, clip['id'], edge, t, kind, evidence, flush=True)
    for video in ("UJwS9HlI9wY", "TDHfPgCLSck"):
        folder = ROOT / "youtube" / video
        search = json.loads((folder / "last-search.json").read_text(encoding="utf-8"))
        transcript = json.loads(Path(search["transcript"]).read_text(encoding="utf-8"))
        job = dict(root=str(ROOT), source=search["source"], ffmpeg=str(FFMPEG))
        # Historical regression bounds must not depend on mutable UI search results.
        clip = dict(start=0.18, end=135.96) if video == "UJwS9HlI9wY" else dict(start=44.58, end=252.82)
        result = refine_clips(job, dict(duration=search["duration"]), transcript["words"], [clip])
        print(json.dumps(dict(video=video, before=clip, after=result[0]), ensure_ascii=True), flush=True)
        if video == "UJwS9HlI9wY":
            assert 136.04 < result[0]["end"] < 136.32, result
            from speech_boundaries import _audio, _recognize
            from whisper_runtime import create_model
            verification = create_model("small", ROOT)
            for end in (135.96, result[0]["end"]):
                recognized = _recognize(verification, _audio(job, 115.96, end), 115.96)
                tail = [w["text"] for w in recognized if w["start"] >= 133]
                print("CUT_RECOGNITION", video, end, json.dumps(tail, ensure_ascii=True), flush=True)
                if end != 135.96:
                    assert "125" in tail, tail
            del verification
        else:
            assert result[0]["end"] == 252.82, result


if __name__ == "__main__":
    if "--inspect" in sys.argv:
        inspect_sources()
    elif "--real" in sys.argv:
        real_sources()
    else:
        unittest.main()
