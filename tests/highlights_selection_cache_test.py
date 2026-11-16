"""Paid selection is retained across local failures; no network calls."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1] / 'tools/highlights'))
spec = importlib.util.spec_from_file_location('cache_worker', Path(__file__).parents[1] / 'tools/highlights/worker.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)


class SelectionCacheTests(unittest.TestCase):
    def test_raw_paid_empty_answer_is_cached_before_selection(self):
        class Response:
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def read(self):
                return json.dumps({'choices': [dict(finish_reason='stop', message=dict(content='{"clips":[]}'))]}).encode()
        with tempfile.TemporaryDirectory() as temp:
            job=dict(openrouter_model='fixture',openrouter_key='secret',selection_cache=temp)
            with patch.object(w.urllib.request,'urlopen',return_value=Response()) as http:
                self.assertEqual({'clips':[]},w.openrouter_ask(job,'prompt'))
                self.assertEqual({'clips':[]},w.openrouter_ask(job,'prompt'))
                http.assert_called_once()
            saved=list(Path(temp).glob('youtube-answer-*.json'))
            self.assertEqual(1,len(saved))
            self.assertNotIn('secret',saved[0].read_text())

    def test_boundary_failure_does_not_discard_other_clip(self):
        with tempfile.TemporaryDirectory() as temp:
            media=Path(temp)/'source.mp4';media.write_bytes(b'fixture')
            job=dict(action='youtube_batch',root=temp,output=temp,result=str(Path(temp)/'result.json'),
                     ffmpeg='ffmpeg',ffprobe='ffprobe',openrouter_key='fake',openrouter_model='fixture',
                     sources=[dict(videoId='abcdefghijk',title='Fixture')])
            clips=[dict(start=0,end=130),dict(start=140,end=270)]
            with patch.object(w,'download_youtube',return_value=dict(source=str(media))), \
                 patch.object(w,'probe',return_value=dict(width=160,height=90,audio=True,duration=400)), \
                 patch.object(w,'transcribe',return_value=[dict(start=0,end=130,text='Complete.')]), \
                 patch.object(w,'select_youtube_speech',return_value=clips), \
                 patch('speech_boundaries.refine_clips',side_effect=[ValueError('decode failed'),[clips[1]]]), \
                 patch.object(w,'export',return_value=dict(outputs=['second.mp4'])) as export:
                result=w.execute(job)
                export.assert_called_once()
                self.assertEqual('partial',result['sources'][0]['status'])
                self.assertEqual(['second.mp4'],result['sources'][0]['outputs'])
                self.assertIn('decode failed',result['sources'][0]['error'])

    def test_retry_after_boundary_failure_reuses_paid_selection(self):
        with tempfile.TemporaryDirectory() as temp:
            media = Path(temp) / 'source.mp4'
            media.write_bytes(b'fixture')
            job = dict(action='youtube_batch', root=temp, output=str(Path(temp) / 'out'),
                       result=str(Path(temp) / 'result.json'), ffmpeg='ffmpeg', ffprobe='ffprobe',
                       openrouter_key='fake', openrouter_model='fixture-model',
                       sources=[dict(videoId='abcdefghijk', title='Fixture', url='https://youtu.be/abcdefghijk')])
            with patch.object(w, 'download_youtube', return_value=dict(source=str(media))), \
                 patch.object(w, 'probe', return_value=dict(width=160, height=90, audio=True, duration=400)), \
                 patch.object(w, 'transcribe', return_value=[dict(start=0, end=130, text='Complete.')]), \
                 patch.object(w, 'select_youtube_speech', return_value=[dict(start=0, end=130)]) as select, \
                 patch('speech_boundaries.refine_clips', side_effect=ValueError('Local alignment failed')), \
                 patch.object(w, 'export') as export:
                first = w.execute(job)
                second = w.execute(job)
                self.assertEqual(1, select.call_count)
                self.assertEqual('error', first['status'])
                self.assertEqual('error', second['status'])
                self.assertIn('Local alignment failed', second['sources'][0]['error'])
                export.assert_not_called()
                job['count'] = 1
                w.execute(job)
                self.assertEqual(2, select.call_count, 'Changed settings require fresh selection')
            saved = list((Path(temp) / 'cache').glob('*/youtube-selection.json'))
            self.assertEqual(1, len(saved))
            self.assertEqual([dict(start=0, end=130)], json.loads(saved[0].read_text())['clips'])


if __name__ == '__main__':
    unittest.main()
