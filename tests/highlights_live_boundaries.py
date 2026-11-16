"""Opt-in acoustic check of saved real Gemini selections; no credentials needed."""
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parents[1] / 'tools/highlights'))
from speech_boundaries import refine_clips

root = Path(os.environ['LOCALAPPDATA']) / 'VideoBatchDesktop/highlights'
for video in sys.argv[1:]:
    folder = root / 'youtube' / video
    source = json.loads((folder / 'last-search.json').read_text(encoding='utf-8'))
    saved = json.loads((folder / 'coherent-check.json').read_text(encoding='utf-8'))
    words = json.loads(Path(source['transcript']).read_text(encoding='utf-8'))['words']
    job = dict(root=str(root), source=source['source'], ffmpeg=r'D:\VideoBatch\VideoBatch\tools\ffmpeg.exe')
    clips = refine_clips(job, dict(duration=source['duration']), words, saved['clips'])
    saved['clips'] = clips
    target = folder / 'acoustic-check.json'
    target.write_text(json.dumps(saved, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(dict(video=video, clips=[dict(id=c['id'], start=c['start'], end=c['end']) for c in clips]), ensure_ascii=True), flush=True)
