"""YouTube batch contracts: fake downloader/LLM, real FFmpeg. No live API assertions."""
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('youtube_highlights', Path(__file__).parents[1] / 'tools/highlights/worker.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)


class YoutubeTests(unittest.TestCase):
    def setUp(self):
        w.CANCEL = None
    def test_child_process_does_not_inherit_openrouter_proxy_variable(self):
        import os
        with patch.dict(os.environ, {'VIDEOBATCH_HTTP_PROXY': 'http://example.invalid:1',
                                     'HTTPS_PROXY': 'http://system-proxy.invalid:2'}):
            output = w.run([sys.executable, '-c',
                            'import os,json;print(json.dumps({k:v for k,v in os.environ.items() if k in ("VIDEOBATCH_HTTP_PROXY","HTTPS_PROXY")}))'])
        self.assertEqual({'HTTPS_PROXY': 'http://system-proxy.invalid:2'}, json.loads(output))

    def test_download_public_only_and_persistent_cache(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            media = folder / 'source.mkv'
            calls = []
            def downloader(args):
                calls.append(args)
                if '--skip-download' in args:
                    return json.dumps(dict(live_status='not_live', availability='public'))
                media.write_bytes(b'download fixture')
                return str(media)
            item = dict(videoId='abcdefghijk', url='https://youtu.be/abcdefghijk', title='Fixture')
            with patch.object(w, 'run', side_effect=downloader):
                first = w.download_youtube(dict(ffmpeg='ffmpeg',node='bundled-node.exe'), item, folder)
                self.assertEqual(first, w.download_youtube(dict(ffmpeg='ffmpeg',node='bundled-node.exe'), item, folder))
            self.assertEqual(2, len(calls))
            for args in calls:
                self.assertIn('--ignore-config', args)
                self.assertEqual('0', args[args.index('--extractor-retries') + 1])
                self.assertEqual('15', args[args.index('--socket-timeout') + 1])
                self.assertIn('--no-cookies', args)
                self.assertIn('--js-runtimes', args)
                self.assertIn('node:bundled-node.exe', args)
                self.assertNotIn('--cookies-from-browser', args)
                self.assertIn('https://www.youtube.com/watch?v=abcdefghijk', args)
            for metadata in (dict(is_live=True), dict(live_status='was_live'), dict(availability='private')):
                with self.assertRaises(ValueError):
                    w.validate_youtube_metadata(metadata)

    def test_desktop_environment_key_not_job_file(self):
        import os
        with tempfile.TemporaryDirectory() as temp:
            job = dict(action='youtube_batch', sources=[dict(videoId='abcdefghijk')], root=temp, output=temp,
                       result=str(Path(temp)/'result.json'), openrouter_model='fixture-model')
            with patch.dict(os.environ, {'VIDEOBATCH_OPENROUTER_KEY':'process-secret'}), patch.object(w, 'download_youtube', side_effect=RuntimeError('download blocked')):
                result=w.execute(job)
            self.assertIn('download blocked',result['sources'][0]['error'])
            self.assertNotIn('process-secret',Path(job['result']).read_text(encoding='utf-8'))
        with patch.dict(os.environ, {'VIDEOBATCH_HTTP_PROXY':''}), self.assertRaisesRegex(ValueError,'HTTP'):
            w.openrouter_ask(dict(openrouter_key='fake',openrouter_model='fake',http_proxy='socks5://localhost:1080'),'data')

    def test_download_error_not_hidden(self):
        with tempfile.TemporaryDirectory() as temp:
            item = dict(videoId='abcdefghijk', title='Unavailable')
            with patch.object(w, 'run', side_effect=RuntimeError('HTTP Error 403: Forbidden')):
                with self.assertRaisesRegex(RuntimeError, '403'):
                    w.download_youtube(dict(ffmpeg='ffmpeg'), item, Path(temp))
            self.assertFalse((Path(temp) / 'download.json').exists())

    def test_openrouter_request_contract_no_network(self):
        class Response:
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def read(self):
                return json.dumps({'choices': [dict(finish_reason='stop', message=dict(content='{"clips":[]}'))]}).encode()
        with patch.object(w.urllib.request, 'urlopen', return_value=Response()) as http:
            self.assertEqual({'clips': []}, w.openrouter_ask(dict(openrouter_key='fixture-key', openrouter_model='fixture-model'), 'Timestamped text'))
        req = http.call_args.args[0]
        self.assertEqual('https://openrouter.ai/api/v1/chat/completions', req.full_url)
        self.assertEqual('Bearer fixture-key', req.get_header('Authorization'))
        self.assertEqual('fixture-model', json.loads(req.data)['model'])

    @staticmethod
    def selection(units, first, last, **changes):
        item = dict(first_unit=units[first]['id'], last_unit=units[last]['id'],
                    opening_quote=units[first]['text'], closing_quote=units[last]['text'],
                    score=42, complete=True, title='Explanation', reason='The subject is introduced and resolved.')
        item.update(changes)
        return {'clips': [item]}

    @staticmethod
    def selection_units(count=8):
        return w.sentences([dict(start=i*65, end=(i+1)*65-.2, text=f' Topic {i}.') for i in range(count)])

    def test_semantic_proposal_then_independent_review(self):
        units = self.selection_units()
        answer = self.selection(units, 4, 6)
        with patch.object(w, 'openrouter_ask', return_value=answer) as ask:
            clips = w.select_youtube_speech(dict(minimum=120, maximum=300, count=4), units, 520)
        self.assertEqual(2, ask.call_count)
        proposal, review = [call.args[1] for call in ask.call_args_list]
        self.assertIn('SEMANTIC_PROPOSAL', proposal)
        self.assertIn('including later discussions', proposal)
        self.assertIn('NOT sensational highlights', proposal)
        self.assertIn('Exclude introductions', proposal)
        self.assertIn('Do not force a count', proposal)
        self.assertNotIn('CANDIDATES=', proposal)
        self.assertIn('ALLOWED_END_BOUNDS=', proposal)
        self.assertIn('INDEPENDENT_EDITORIAL_REVIEW', review)
        evidence = json.loads(review.split('\nREVIEW_EVIDENCE=')[1].split('\nALLOWED_END_BOUNDS=')[0])
        self.assertEqual(units[2:4], evidence['preceding'])
        self.assertEqual(units[4:7], evidence['passage'])
        self.assertEqual(units[7:], evidence['following'])
        self.assertNotIn('score', evidence)
        self.assertEqual(1, len(clips))
        self.assertEqual(4, clips[0]['first_unit'])
        self.assertTrue(120 <= clips[0]['end'] - clips[0]['start'] <= 300)

    def test_invalid_durations_ids_and_quotes_are_not_repaired(self):
        units = self.selection_units()
        job = dict(minimum=120, maximum=300, count=4)
        cases = [({'first_unit': '0'}, 'ID'), ({'first_unit': True}, 'ID'),
                 ({'last_unit': 99}, 'ID'), ({'last_unit': -1}, 'ID'),
                 ({'opening_quote': 'Invented.'}, 'opening_quote'),
                 ({'closing_quote': units[1]['text'] + ' '}, 'closing_quote')]
        for changes, expected in cases:
            with self.subTest(changes=changes), patch.object(w, 'openrouter_ask',
                    return_value=self.selection(units, 0, 1, **changes)) as ask, patch.object(w, 'emit') as emit:
                with self.assertRaisesRegex(ValueError, expected):
                    w.select_youtube_speech(job, units, 520)
                self.assertEqual(1, ask.call_count)
                self.assertIn(expected, emit.call_args.args[1])

    def test_review_rejects_mid_argument_and_logs_specific_reason(self):
        units = self.selection_units()
        reason = 'Following sentence supplies the explanation; a next-topic transition is not a conclusion.'
        answers = [self.selection(units, 0, 1), self.selection(units, 0, 1, complete=False, reason=reason)]
        with patch.object(w, 'openrouter_ask', side_effect=answers) as ask, patch.object(w, 'emit') as emit:
            clips = w.select_youtube_speech(dict(minimum=120, maximum=300, count=4), units, 520)
        self.assertEqual([], clips)
        self.assertEqual(2, ask.call_count)
        self.assertTrue(any(reason in str(call) for call in emit.call_args_list))

    def test_review_adjusts_valid_boundaries_and_checks_adjusted_quotes(self):
        units = self.selection_units()
        job = dict(minimum=120, maximum=300, count=4)
        proposal = self.selection(units, 1, 2)
        reviewed = self.selection(units, 0, 3)
        with patch.object(w, 'openrouter_ask', side_effect=[proposal, reviewed]):
            clips = w.select_youtube_speech(job, units, 520)
        self.assertEqual((0, 3), (clips[0]['first_unit'], clips[0]['last_unit']))
        bad = self.selection(units, 0, 3, opening_quote=units[1]['text'])
        with patch.object(w, 'openrouter_ask', side_effect=[proposal, bad]):
            with self.assertRaisesRegex(ValueError, 'review.*opening_quote'):
                w.select_youtube_speech(job, units, 520)

    def test_review_invalid_boundaries_and_quotes_fail_atomically(self):
        units = self.selection_units()
        job = dict(minimum=120, maximum=300, count=4)
        good = self.selection(units, 0, 1)
        proposals = {'clips': good['clips'] + self.selection(units, 4, 5)['clips']}
        for changes, expected in (({'last_unit': '5'}, 'ID'), ({'last_unit': 4}, 'длительность'),
                                  ({'closing_quote': 'Not the source.'}, 'closing_quote')):
            bad = self.selection(units, 4, 5, **changes)
            with self.subTest(changes=changes), patch.object(w, 'openrouter_ask',
                    side_effect=[proposals, good, bad]) as ask:
                with self.assertRaisesRegex(ValueError, 'review.*' + expected):
                    w.select_youtube_speech(job, units, 520)
                self.assertEqual(3, ask.call_count)

    def test_semantic_opening_not_pronoun_blacklist(self):
        units = self.selection_units(2)
        units[0]['text'] = 'Это объяснение того, как работает двигатель.'
        answer = self.selection(units, 0, 1)
        with patch.object(w, 'openrouter_ask', return_value=answer), patch.object(w, 'dependent_opening',
                side_effect=AssertionError('YouTube must not use the local pronoun guard')):
            self.assertEqual(1, len(w.select_youtube_speech(
                dict(minimum=120, maximum=300, count=4), units, 130)))
        with patch.object(w, 'openrouter_ask', return_value=self.selection(units, 0, 1,
                complete=False, reason='The explanation is not finished.')) as ask:
            self.assertEqual([], w.select_youtube_speech(dict(minimum=120, maximum=300, count=4), units, 130))
            self.assertEqual(1, ask.call_count)

    def test_distinct_nonoverlap_after_review(self):
        units = self.selection_units()
        items = [self.selection(units, a, b)['clips'][0] for a, b in ((0, 1), (1, 2), (4, 5))]
        answers = [{'clips': items}] + [{'clips': [item]} for item in items]
        with patch.object(w, 'openrouter_ask', side_effect=answers) as ask:
            clips = w.select_youtube_speech(dict(minimum=120, maximum=300, count=3), units, 520)
        self.assertEqual(4, ask.call_count)
        self.assertEqual(['0:1', '4:5'], [clip['id'] for clip in clips])
        self.assertLessEqual(clips[0]['end'], clips[1]['start'])

    def test_long_sentence_boundaries_no_forced_count(self):
        units = self.selection_units(3)
        job = dict(minimum=120, maximum=300, count=4)
        with patch.object(w, 'openrouter_ask', return_value=self.selection(units, 0, 2)):
            clips = w.select_youtube_speech(job, units, 200)
        self.assertEqual(1, len(clips))
        self.assertEqual(0, clips[0]['start'])
        self.assertAlmostEqual(194.98, clips[0]['end'])
        with patch.object(w, 'openrouter_ask', return_value={'clips': []}) as ask:
            self.assertEqual([], w.select_youtube_speech(job, units, 200))
            self.assertEqual(1, ask.call_count)
        unfinished = w.sentences([dict(start=0, end=130, text='Not finished')])
        with patch.object(w, 'openrouter_ask') as ask:
            self.assertEqual([], w.select_youtube_speech(job, unfinished, 140))
            ask.assert_not_called()

    def test_later_window_preserves_original_ids_and_actual_timing(self):
        units = self.selection_units(52)
        prompts = []
        def choose(job, prompt):
            prompts.append(prompt)
            transcript = json.loads(prompt.split('\nTRANSCRIPT=')[1])
            if prompt.startswith('SEMANTIC_PROPOSAL'):
                if transcript[0][0] == 0:
                    return {'clips': []}
                if transcript[0][0] > 24:
                    return {'clips': []}
            return self.selection(units, 24, 25)
        with patch.object(w, 'openrouter_ask', side_effect=choose):
            clips = w.select_youtube_speech(dict(minimum=120, maximum=300.0, count=4), units, 3380)
        self.assertEqual(['24:25'], [clip['id'] for clip in clips])
        self.assertAlmostEqual(1559.88, clips[0]['start'])
        self.assertAlmostEqual(1689.98, clips[0]['end'])
        later = next(p for p in prompts if p.startswith('SEMANTIC_PROPOSAL') and
                     json.loads(p.split('\nTRANSCRIPT=')[1])[0][0] == 23)
        self.assertIn('24', json.loads(later.split('\nALLOWED_END_BOUNDS=')[1].split('\nTRANSCRIPT=')[0]))

    def test_all_windows_reviewed_before_global_limit(self):
        units = self.selection_units(52)
        job = dict(minimum=120, maximum=300, count=1)
        first = self.selection(units, 0, 1, score=30)
        later = self.selection(units, 30, 31, score=80)
        with patch.object(w, 'openrouter_ask', side_effect=[first, first, later, later, {'clips': []}]) as ask:
            clips = w.select_youtube_speech(job, units, 3380)
        self.assertEqual(5, ask.call_count)
        self.assertEqual(['30:31'], [clip['id'] for clip in clips])

    def test_compact_allowed_bounds_preserve_gaps(self):
        self.assertEqual({'0': [[2, 3], [5, 5]], '4': [[6, 6]]},
                         w.youtube_end_bounds({(0, 2): {}, (0, 3): {}, (0, 5): {}, (4, 6): {}}))

    def test_probe_rotation_and_sample_aspect(self):
        raw = dict(format=dict(duration='200'), streams=[dict(codec_type='video', width=1920, height=1080,
                   sample_aspect_ratio='1:1', side_data_list=[dict(rotation=90)])])
        with patch.object(w, 'run', return_value=json.dumps(raw)):
            info = w.probe(dict(ffprobe='ffprobe', source='fixture'))
        self.assertLess(info['width'], info['height'])
        raw['streams'][0].update(width=720, height=576, sample_aspect_ratio='16:15', side_data_list=[])
        with patch.object(w, 'run', return_value=json.dumps(raw)):
            info = w.probe(dict(ffprobe='ffprobe', source='fixture'))
        self.assertAlmostEqual(768, info['width'])

    def test_source_rejections_and_cli_error_status(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            media = root / 'source.mp4'
            media.write_bytes(b'fixture')
            job = dict(action='youtube_batch', sources=[dict(videoId='abcdefghijk', url='https://youtu.be/abcdefghijk', title='Fixture')],
                       root=temp, output=str(root / 'out'), result=str(root / 'result.json'), ffmpeg='ffmpeg', ffprobe='ffprobe',
                       openrouter_key='fake', openrouter_model='fake')
            for info, expected in ((dict(width=90, height=160, audio=True, duration=200), 'горизонтальный'),
                                   (dict(width=160, height=90, audio=False, duration=200), 'звуковой'),
                                   (dict(width=160, height=90, audio=True, duration=50), 'короче')):
                with patch.object(w, 'download_youtube', return_value=dict(source=str(media))), patch.object(w, 'probe', return_value=info), patch.object(w, 'transcribe') as speech:
                    result = w.execute(job)
                self.assertEqual('error', result['status'])
                self.assertIn(expected, result['sources'][0]['error'])
                speech.assert_not_called()
            job['sources'][0]['url'] = 'https://www.youtube.com/shorts/abcdefghijk'
            with patch.object(w, 'download_youtube') as download:
                self.assertIn('Shorts', w.execute(job)['sources'][0]['error'])
                download.assert_not_called()
            jobfile = root / 'job.json'
            jobfile.write_text(json.dumps(job), encoding='utf-8')
            with patch.object(sys, 'argv', ['worker.py', '--job', str(jobfile)]), patch.object(w, 'execute', return_value=dict(action='youtube_batch', status='partial', summary='1 error')), patch.object(w, 'emit') as emit:
                self.assertEqual(1, w.main())
                self.assertEqual('error', emit.call_args.args[0])

    def test_partial_exports_are_reported_not_success(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            media = root / 'source.mp4'
            media.write_bytes(b'fixture')
            job = dict(action='youtube_batch', sources=[dict(videoId='abcdefghijk', url='https://youtu.be/abcdefghijk', title='Fixture')],
                       root=temp, output=str(root / 'out'), result=str(root / 'result.json'), ffmpeg='ffmpeg', ffprobe='ffprobe',
                       openrouter_key='fake', openrouter_model='fake')
            with patch.object(w, 'download_youtube', return_value=dict(source=str(media))), patch.object(w, 'probe', return_value=dict(width=160, height=90, audio=True, duration=400)), patch.object(w, 'transcribe', return_value=[dict(start=0, end=130, text='Complete.')]), patch.object(w, 'select_youtube_speech', return_value=[dict(start=0, end=130), dict(start=140, end=270)]), patch.object(w, 'export', side_effect=[dict(outputs=['completed.mp4']), RuntimeError('disk full')]):
                result = w.execute(job)
            self.assertEqual('error', result['status'])
            self.assertEqual(['completed.mp4'], result['sources'][0]['outputs'])
            self.assertEqual('disk full', result['sources'][0]['error'])
            self.assertTrue(media.exists())

    @unittest.skipUnless(shutil.which('ffmpeg') and shutil.which('ffprobe'), 'FFmpeg required')
    def test_real_batch_export_and_retry_preserves_transcript(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            fixture = root / 'fixture.mp4'
            subprocess.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'color=blue:s=160x90:r=5:d=135',
                            '-f', 'lavfi', '-i', 'sine=frequency=440:duration=135', '-c:v', 'libx264',
                            '-c:a', 'aac', '-shortest', str(fixture)], check=True)
            job = dict(action='youtube_batch', sources=[dict(videoId='abcdefghijk', url='https://youtu.be/abcdefghijk', title='Good'),
                       dict(videoId='badbadbad00', url='https://youtu.be/badbadbad00', title='Blocked')], root=str(root),
                       output=str(root / 'out'), result=str(root / 'result.json'), ffmpeg=shutil.which('ffmpeg'),
                       ffprobe=shutil.which('ffprobe'), openrouter_key='fake', openrouter_model='fake')
            transcription_calls = []
            class Whisper:
                def __init__(self, *args, **kwargs):
                    self.model = types.SimpleNamespace(device='cpu')
                def transcribe(self, path, **kwargs):
                    transcription_calls.append(path)
                    words = [types.SimpleNamespace(start=0, end=60, word=' Setup.'),
                             types.SimpleNamespace(start=60.1, end=125, word=' Explanation and conclusion.')]
                    segment = types.SimpleNamespace(no_speech_prob=0, avg_logprob=0, end=125, words=words)
                    return iter([segment]), types.SimpleNamespace(language='en')
            downloads = []
            def downloader(args):
                if 'badbadbad00' in args[-1]:
                    raise RuntimeError('HTTP Error 403: Forbidden')
                if '--skip-download' in args:
                    return json.dumps(dict(live_status='not_live', availability='public'))
                downloads.append(args)
                target = root / 'youtube' / 'abcdefghijk' / 'source.mp4'
                shutil.copyfile(fixture, target)
                return str(target)
            original_run = w.run
            def commands(args, **kwargs):
                return downloader(args) if args[:3] == [sys.executable, '-m', 'yt_dlp'] else original_run(args, **kwargs)
            answer = {'clips': [dict(first_unit=0, last_unit=1, opening_quote='Setup.',
                                    closing_quote='Explanation and conclusion.', score=90,
                                    complete=True, title='Idea', reason='Complete explanation and conclusion.')]}
            with patch.object(w, 'run', side_effect=commands), patch.dict(sys.modules, {'whisper_runtime': types.SimpleNamespace(create_model=lambda *args: Whisper())}):
                with patch.object(w, 'openrouter_ask', side_effect=RuntimeError('LLM unavailable')):
                    failed = w.execute(job)
                self.assertEqual('error', failed['status'])
                self.assertTrue(Path(failed['sources'][0]['source']).exists())
                self.assertTrue(Path(failed['sources'][0]['transcript']).exists())
                with patch.object(w, 'openrouter_ask', return_value=answer):
                    result = w.execute(job)
            self.assertEqual(1, len(downloads))
            self.assertEqual(1, len(transcription_calls))
            self.assertEqual('partial', result['status'])
            self.assertEqual((1, 1), (result['succeeded'], result['failed']))
            good, bad = result['sources']
            self.assertEqual('success', good['status'])
            self.assertIn('403', bad['error'])
            self.assertEqual(1, len(good['outputs']))
            info = w.probe(dict(source=good['outputs'][0], ffprobe=job['ffprobe']))
            self.assertTrue(120 <= info['duration'] <= 300)
            self.assertGreater(info['width'], info['height'])
            self.assertTrue(info['audio'])
            self.assertEqual('partial', json.loads(Path(job['result']).read_text(encoding='utf-8'))['status'])
            # The same long ranges are valid for explicit local export jobs; old defaults remain unchanged.
            local = dict(job, action='export', source=str(fixture), output=str(root / 'local'), minimum=120, maximum=300,
                         clips=[dict(start=0, end=125)])
            self.assertEqual(1, len(w.execute(local)['outputs']))


if __name__ == '__main__':
    unittest.main()
