"""Deterministic model-contract tests + real FFmpeg roundtrip; no paid services."""
import importlib.util
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("highlights", Path(__file__).parents[1]/"tools/highlights/worker.py")
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)


class HighlightsTests(unittest.TestCase):
    def setUp(self):
        w.CANCEL = None

    def test_sentences_and_complete_windows(self):
        words = [dict(start=0,end=10,text=" Утверждение."), dict(start=10.2,end=23,text=" Пояснение."), dict(start=24,end=43,text=" Ответ."),dict(start=43.1,end=66,text=" Очень длинная законченная мысль.")]
        units = w.sentences(words)
        pool = w.candidates(units,20,40,70)
        self.assertTrue(any(c['start']==0 and abs(c['end']-23.18)<.001 for c in pool))
        self.assertTrue(all(20<=c['end']-c['start']<=40 for c in pool))
        # No arbitrary 40s slice out of a 43s single sentence.
        self.assertEqual([],w.candidates(w.sentences([dict(start=0,end=43,text="Длинная мысль.")]),20,40,43))
        with patch.object(w,"ask",return_value={"clips":[dict(id=0,score=90,complete=True,title="Тезис",reason="Есть объяснение"),dict(id=9999,score=99,complete=True),dict(id=1,score=98,complete=False)]}) as model:
            selected=w.select_speech(pool,units,70)
        self.assertEqual(1,len(selected))
        prompt=model.call_args.args[0]
        self.assertIn('CONTEXT=',prompt)
        self.assertIn('qualification',prompt)
        self.assertIn('first_unit',prompt)
        self.assertNotIn('CANDIDATES=[]',prompt)

    def test_no_fake_highlights(self):
        with patch.object(w,"ask",return_value={"clips":[]}):
            self.assertEqual([],w.select_speech([dict(id=0,start=0,end=23,text="Обычная фраза")],[dict(id=0,start=0,end=23,text="Обычная фраза")],25))
        with self.assertRaises(ValueError):
            w.number(float('nan'))
        with self.assertRaises(ValueError):
            w.number(True)

    def test_overlap_text_and_count(self):
        base=lambda a,b,s,text:dict(start=a,end=b,score=s,text=text)
        clips=[base(0,25,80,'Один.'),base(1,24,90,'Один!'),base(25,50,70,'Два'),base(50,75,60,'Один')]
        self.assertEqual([1,25],[c['start'] for c in w.deduplicate(clips,10)])
        self.assertEqual(1,len(w.deduplicate(clips,1)))
        self.assertEqual([(10,40),(45,70)],w.silent_ranges([dict(start=0,end=10),dict(start=40,end=45)],70,20))

    def test_cancel(self):
        with tempfile.TemporaryDirectory() as temp:
            w.CANCEL=str(Path(temp)/'stop')
            Path(w.CANCEL).touch()
            with self.assertRaises(w.Cancelled):w.run([sys.executable,'-c','print(1)'])
        w.CANCEL=None

    @unittest.skipUnless(shutil.which('ffmpeg') and shutil.which('ffprobe'),'FFmpeg required')
    def test_real_export_visual_and_integrity(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);source=root/'исходник с пробелом.mp4'
            # Long GOP and a distinctive red beginning expose keyframe-snapped seek errors.
            subprocess.run(['ffmpeg','-v','error','-f','lavfi','-i','color=blue:s=192x108:r=25:d=46','-f','lavfi','-i','sine=frequency=440:duration=46','-vf',"drawbox=x=0:y=0:w=iw:h=ih:color=red:t=fill:enable='between(t,7.2,7.6)'",'-c:v','libx264','-g','250','-pix_fmt','yuv420p','-c:a','aac',str(source)],check=True)
            original=w.fingerprint(source)
            job=dict(source=str(source),root=str(root),ffmpeg=shutil.which('ffmpeg'),ffprobe=shutil.which('ffprobe'),minimum=20,maximum=40,mode='visual',action='export',output=str(root/'out'),clips=[dict(start=7.25,end=29.25)])
            result=w.execute(job)
            output=Path(result['outputs'][0]);self.assertTrue(output.exists())
            pix=subprocess.check_output(['ffmpeg','-v','error','-i',str(output),'-frames:v','1','-vf','scale=1:1','-f','rawvideo','-pix_fmt','rgb24','pipe:1'])
            self.assertGreater(pix[0],180);self.assertLess(pix[2],70)
            self.assertEqual(original,w.fingerprint(source))
            self.assertFalse(list((root/'out').glob('*.partial.mp4')))
            # No audio path does not import Whisper or require it.
            self.assertEqual([],w.transcribe(job,dict(audio=False),root,temp))
            sheet=w.contact_sheet(job,0,30,temp);self.assertTrue(sheet.is_file())
            valid=dict(start=2,end=26,score=85,complete=True,title='Событие',reason='Изменение')
            with patch.object(w,'ask',return_value={'clips':[valid,dict(valid,start=-2),dict(valid,end=45)]}) as model:
                found=w.select_visual(job,[(0,40)],temp)
                self.assertEqual(1,len(found));self.assertEqual('visual',found[0]['kind'])
                self.assertTrue(model.call_args.args[2])
            # Entire Auto job with actual FFmpeg and a deterministic semantic response fixture.
            auto=dict(job,mode='auto',action='analyze')
            words=[dict(start=0,end=12,text=' Утверждение.'),dict(start=12.1,end=25,text=' Его объяснение.')]
            with patch.object(w,'ready'),patch.object(w,'transcribe',return_value=words),patch.object(w,'ask',side_effect=[{'clips':[dict(id=0,score=88,complete=True,title='Мысль',reason='Завершена')]},{'clips':[]}]):
                analysis=w.execute(auto)
            self.assertEqual(1,len(analysis['clips']))
            self.assertEqual(original,analysis['fingerprint'])
            job['expected_fingerprint']='changed-source'
            with self.assertRaises(ValueError):w.execute(job)
            job.pop('expected_fingerprint')
            job['clips']=[dict(start=1,end=10)]
            with self.assertRaises(ValueError):w.execute(job)


if __name__=='__main__': unittest.main()
