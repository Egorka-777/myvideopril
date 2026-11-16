"""Opt-in regression on the reported local source. One paid call only if uncached.
Run --real with VIDEOBATCH_OPENROUTER_KEY supplied securely by the desktop settings.
"""
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parents[1] / 'tools/highlights'))
import worker as w


def main():
    if '--real' not in sys.argv:
        raise SystemExit('Pass --real to allow one cached/paid semantic selection.')
    root = Path(os.environ['LOCALAPPDATA']) / 'VideoBatchDesktop/highlights'
    batch = json.loads((root/'last-youtube-result.json').read_text(encoding='utf-8'))
    source = next(s for s in batch['sources'] if s['videoId'] == os.environ.get('VIDEOBATCH_REGRESSION_VIDEO','523R2Z-yIO0'))
    cache = Path(source['transcript']).parent
    words = json.loads(Path(source['transcript']).read_text(encoding='utf-8'))['words']
    job = dict(root=str(root),source=source['source'],minimum=120,maximum=300,count=4,
               openrouter_model='google/gemini-2.5-flash',
               openrouter_key=os.environ['VIDEOBATCH_OPENROUTER_KEY'],
               http_proxy=os.environ.get('VIDEOBATCH_HTTP_PROXY',''),selection_cache=str(cache),
               ffmpeg=r'D:\VideoBatch\VideoBatch\tools\ffmpeg.exe',ffprobe=r'D:\VideoBatch\VideoBatch\tools\ffprobe.exe')
    clips = w.select_youtube_speech(job,w.sentences(words),source['duration'])
    if not clips:
        raise AssertionError('No coherent passage selected; raw model answer retained.')
    import speech_boundaries
    refined = speech_boundaries.refine_clips(job,dict(duration=source['duration']),words,clips)
    report = dict(videoId=source['videoId'],clips=refined)
    report_path = root/'real-selection-regression.json'
    w.atomic_json(report_path,report)
    for c in refined:
        print(json.dumps(dict(id=c['id'],start=c['start'],end=c['end'],duration=c['end']-c['start'],title=c['title'],text=c['text']),ensure_ascii=True),flush=True)
    if '--export' in sys.argv:
        info=w.probe(job)
        output=root/'regression-exports'/source['videoId']
        result=w.export(dict(job,output=str(output),clips=refined,export_format='original',workdir=str(root/'regression-work')),info)
        report['outputs']=result['outputs'];w.atomic_json(report_path,report)
        print(json.dumps(result,ensure_ascii=True),flush=True)


if __name__ == '__main__':
    main()
