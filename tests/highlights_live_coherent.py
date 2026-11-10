"""Explicit live OpenRouter + FFmpeg check on an already downloaded source.
Credentials are passed through process environment, never saved in this script.
"""
import importlib.util
import json
import os
from pathlib import Path
import sys
import time

spec = importlib.util.spec_from_file_location('worker', Path(__file__).parents[1] / 'tools/highlights/worker.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)
root = Path(os.environ['LOCALAPPDATA']) / 'VideoBatchDesktop/highlights'
source = json.loads((root / 'youtube' / sys.argv[1] / 'last-search.json').read_text(encoding='utf-8'))
words = json.loads(Path(source['transcript']).read_text(encoding='utf-8'))['words']
job = dict(source=source['source'], root=str(root), minimum=120, maximum=300, count=4,
           openrouter_key=os.environ['VIDEOBATCH_OPENROUTER_KEY'],
           http_proxy=os.environ.get('VIDEOBATCH_HTTP_PROXY', ''),
           openrouter_model='openai/gpt-4o-mini',
           ffmpeg=r'D:\VideoBatch\VideoBatch\tools\ffmpeg.exe',
           ffprobe=r'D:\VideoBatch\VideoBatch\tools\ffprobe.exe',
           output=str(Path.home() / 'Videos/VideoBatch/Нарезчик' / sys.argv[1] / 'clips'))
started = time.monotonic()
clips = w.select_youtube_speech(job, w.sentences(words), source['duration'])
result = dict(videoId=sys.argv[1], selection_seconds=round(time.monotonic()-started, 2), clips=clips, outputs=[])
w.atomic_json(root / 'youtube' / sys.argv[1] / 'coherent-check.json', result)
print(json.dumps(dict(videoId=sys.argv[1], count=len(clips), seconds=result['selection_seconds']), ensure_ascii=True), flush=True)
if not clips:
    raise RuntimeError('Live selection returned no coherent excerpts')
if '--selection-only' in sys.argv:
    sys.exit(0)
info = w.probe(job)
for clip in clips:
    result['outputs'].extend(w.export(dict(job, clips=[clip]), info)['outputs'])
    w.atomic_json(root / 'youtube' / sys.argv[1] / 'coherent-check.json', result)
print(json.dumps(dict(outputs=result['outputs']), ensure_ascii=True), flush=True)
