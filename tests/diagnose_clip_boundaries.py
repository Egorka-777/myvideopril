"""Focused, read-only diagnostics for the two reported exported clip endings."""
import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import numpy as np

ROOT = Path(r'C:\Users\hackerman\AppData\Local\VideoBatchDesktop\highlights')
TOOLS = Path(r'D:\VideoBatch\VideoBatch\tools')
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools' / 'highlights'))
from whisper_runtime import create_model


def run(args):
    return subprocess.run([str(a) for a in args], check=True, capture_output=True).stdout


def pcm(path):
    return np.frombuffer(run([TOOLS/'ffmpeg.exe', '-v', 'error', '-i', path, '-f', 'f32le', '-ac', 1, '-ar', 16000, '-']), dtype=np.float32)


def recognize(model, path, offset, label, boundary):
    before = time.perf_counter()
    segments, info = model.transcribe(str(path), word_timestamps=True, vad_filter=True, condition_on_previous_text=False, beam_size=5)
    results = []
    for s in segments:
        results.append(dict(start=round(s.start+offset,3), end=round(s.end+offset,3), text=s.text,
            no_speech_prob=s.no_speech_prob, avg_logprob=s.avg_logprob,
            would_filter=s.no_speech_prob > .6 or s.avg_logprob < -1.2,
            words=[dict(start=round(w.start+offset,3), end=round(w.end+offset,3), text=w.word, probability=round(w.probability,4)) for w in s.words or []]))
    print(json.dumps(dict(label=label, elapsed=round(time.perf_counter()-before,3), language=info.language, boundary=boundary, segments=results), ensure_ascii=False), flush=True)
    return results


def main():
    t = time.perf_counter()
    model = create_model('small', ROOT)
    print('MODEL_LOAD_SECONDS', round(time.perf_counter()-t,3), flush=True)
    for video in (sys.argv[1:] or ('TDHfPgCLSck', 'UJwS9HlI9wY')):
        folder = ROOT/'youtube'/video
        selection = json.loads((folder/'coherent-check.json').read_text(encoding='utf-8'))
        search = json.loads((folder/'last-search.json').read_text(encoding='utf-8'))
        clip = selection['clips'][0]
        source, output = Path(search['source']), Path(selection['outputs'][0])
        a, b = clip['end']-20, clip['end']+15
        transcript = json.loads(Path(search['transcript']).read_text(encoding='utf-8'))
        print(json.dumps(dict(video=video, start=clip['start'], end=clip['end'], expected_duration=clip['end']-clip['start'], cache_model=transcript.get('model'), cache_version=transcript.get('version'), cached_words=[w for w in transcript['words'] if w['end'] >= a and w['start'] <= b]), ensure_ascii=False), flush=True)
        for label, path in [('source',source),('export',output)]:
            probe = json.loads(run([TOOLS/'ffprobe.exe','-v','error','-show_entries','format=start_time,duration:stream=index,codec_type,start_time,duration,time_base','-of','json',path]))
            print(json.dumps(dict(video=video,label=label,probe=probe)), flush=True)
        with tempfile.TemporaryDirectory(prefix='vb-boundary-') as td:
            td = Path(td)
            context, tail = td/'context.wav', td/'tail.wav'
            t = time.perf_counter()
            run([TOOLS/'ffmpeg.exe','-v','error','-nostdin','-y','-i',source,'-vn','-map','0:a:0','-ac',1,'-af',f'aresample=16000:async=1:first_pts=0,atrim=start={a}:end={b},asetpts=PTS-STARTPTS','-ar',16000,context])
            tail_start = clip['end']-clip['start']-20
            run([TOOLS/'ffmpeg.exe','-v','error','-nostdin','-y','-ss',tail_start,'-i',output,'-vn','-ac',1,'-ar',16000,tail])
            print('EXTRACTION_SECONDS',video, round(time.perf_counter()-t,3), flush=True)
            x,y = pcm(context), pcm(tail)
            # Cross-correlate 15s of clip tail against the source context. A positive lag
            # means exported audio occurs later than its intended source timestamp.
            template=y[16000:16*16000]
            fft_len = 1 << (len(x)+len(template)-2).bit_length()
            conv = np.fft.irfft(np.fft.rfft(x,fft_len)*np.fft.rfft(template[::-1],fft_len),fft_len)
            corr=conv[len(template)-1:len(x)]
            lag=int(np.argmax(corr))
            match=x[lag:lag+len(template)]
            print(json.dumps(dict(video=video,label='waveform_alignment',source_offset_seconds=lag/16000-1,correlation=float(np.dot(match,template)/np.sqrt(np.dot(match,match)*np.dot(template,template))),source_samples=len(x),tail_samples=len(y))),flush=True)
            recognize(model,context,a,video+' source context',clip['end'])
            recognize(model,tail,a,video+' exported tail',clip['end'])
            if video == 'UJwS9HlI9wY':
                for vad in (False, True):
                    before = time.perf_counter()
                    segs, _ = model.transcribe(str(context), word_timestamps=True, vad_filter=vad, condition_on_previous_text=False, beam_size=5)
                    print(json.dumps(dict(label='UJ alternative context',vad=vad,segments=[dict(start=s.start+a,end=s.end+a,text=s.text,no_speech_prob=s.no_speech_prob,avg_logprob=s.avg_logprob,words=[dict(start=w.start+a,end=w.end+a,text=w.word) for w in s.words or []]) for s in segs],elapsed=time.perf_counter()-before),ensure_ascii=False),flush=True)
                for end in (135.96, 136.16, 136.28):
                    extended = td/f'extended-{end}.wav'
                    run([TOOLS/'ffmpeg.exe','-v','error','-y','-i',context,'-t',end-a,extended])
                    recognize(model,extended,a,video+f' source cut at {end}',end)
                short = td/'short.wav'
                run([TOOLS/'ffmpeg.exe','-v','error','-y','-ss',130,'-i',source,'-t',12,'-vn','-ac',1,'-ar',16000,short])
                recognize(model,short,130,video+' short source context',clip['end'])
            silence = subprocess.run([str(TOOLS/'ffmpeg.exe'),'-hide_banner','-i',str(context),'-af','silencedetect=noise=-35dB:d=0.12','-f','null','-'],capture_output=True,text=True,check=True)
            print(json.dumps(dict(video=video,label='silence_intervals_context_relative',origin=a,lines=[s for s in silence.stderr.splitlines() if 'silence_' in s])),flush=True)


if __name__ == '__main__':
    main()
