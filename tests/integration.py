import json,subprocess,tempfile,time,os,hashlib,wave
from pathlib import Path
import numpy as np
ROOT=Path(__file__).resolve().parents[1];PS='/tmp/pwsh-videobatch/pwsh';FF='/usr/bin/ffmpeg';FP='/usr/bin/ffprobe'
checks=[]
def run(args,timeout=180):
 p=subprocess.run(list(map(str,args)),capture_output=True,timeout=timeout)
 assert p.returncode==0,(p.stdout.decode(errors='replace'),p.stderr.decode(errors='replace'))
 return p.stdout

def probe(path):return json.loads(run([FP,'-v','error','-show_streams','-show_format','-of','json',path]))
def profile(slot=1,**kw):
 p=dict(Format='mp4',Fps='source',Resolution='source',Compression='quality',Metadata='clear',FileDate='now',Speed=1,Crop=0,Gray=0,Crf=22,Bitrate=4,Slot=slot,CustomDate='/Date(1706933100000)/',Short=False,MusicPath='',Brightness=0,Contrast=1,MusicVolume=1,MusicStart=0,ScalePercent=100,FpsPercent=100,BitratePercent=100);p.update(kw);return p

def ranges(**kw):
 p=dict(Crop=dict(Min=.3,Max=1.5),Gray=dict(Min=0,Max=5),Brightness=dict(Min=-2,Max=2),Contrast=dict(Min=98,Max=102),Crf=dict(Min=20,Max=23),Format='mp4',Fps='source',Resolution='source',Volume=100,Loop=True,TechnicalVariants=False);p.update(kw);return p

def create(root,name,inputs,profiles=None,**kw):
 work=root/name;work.mkdir();(work/'out').mkdir()
 p=dict(Inputs=list(map(str,inputs)),Music=[],Narrations=[],BackgroundDb=-7.5,Shorts=False,Count=len(profiles or [1]),Output=str(work/'out'),FFmpeg=FF,FFprobe=FP,Profiles=profiles or [profile()],Ranges=ranges());p.update(kw)
 (work/'job.json').write_text(json.dumps(p,ensure_ascii=False),encoding='utf-8')
 return work,[PS,'-NoProfile','-File',ROOT/'tests/RunTest.ps1','-JobPath',work/'job.json']
def execute(*args,**kw):
 w,c=create(*args,**kw);run(c);return w,json.loads((w/'result.json').read_text())
def good(state,count):assert not state['Errors'] and not state['Cancelled'] and len(state['Outputs'])==count,state

def audio(path,start=0,length=None):
 a=[FF,'-v','error','-ss',str(start),'-i',path];a+=['-t',str(length)] if length else []
 return np.frombuffer(run(a+['-vn','-ac','1','-ar','48000','-f','f32le','-']),dtype='<f4')
def check(label):checks.append(label);print('PASS:',label,flush=True)
with tempfile.TemporaryDirectory(prefix='videobatch-native-') as temp:
 root=Path(temp);source=root/"Тест & видео 'один'.mp4"
 run([FF,'-v','error','-f','lavfi','-i','testsrc2=size=320x180:rate=30:duration=4','-f','lavfi','-i','sine=frequency=440:duration=4','-c:v','libx264','-threads','2','-c:a','aac','-metadata','title=SOURCE_TITLE',source]);original=source.read_bytes();os.utime(source,(1600000000,1600000000))
 profiles=[profile(),profile(2,Speed=.98,Fps='25',Crop=1,Gray=3,FileDate='keep'),profile(3,Format='mov',Speed=1.05,Fps='24',Gray=100,Crop=2,Metadata='custom',FileDate='custom'),profile(4,Format='mkv',Speed=1.02,Fps='29.97',Compression='bitrate',Metadata='keep'),profile(5,Speed=1.03,Fps='60',Metadata='now')]
 w,state=execute(root,'five',[source],profiles);good(state,5)
 for path,p in zip(state['Outputs'],profiles):
  data=probe(path);v=next(s for s in data['streams'] if s['codec_type']=='video');a=next(s for s in data['streams'] if s['codec_type']=='audio');assert(v['width'],v['height'])==(320,180);assert v['codec_name']=='h264' and a['codec_name']=='aac'
  assert abs(float(data['format']['duration'])-4/p['Speed'])<.2
  n,d=map(float,v['avg_frame_rate'].split('/'));expected=30 if p['Fps']=='source' else float(p['Fps']);assert abs(n/d-expected)<.002
  if p['Metadata']=='keep':assert 'SOURCE_TITLE' in json.dumps(data)
  else:assert 'SOURCE_TITLE' not in json.dumps(data)
  if p['Metadata']=='custom':assert data['format']['tags']['creation_time'].startswith('2024-02-03')
  if p['FileDate']=='keep':assert abs(Path(path).stat().st_mtime-1600000000)<2
 assert source.read_bytes()==original and source.stat().st_mtime==1600000000
 check('Five profiles: containers/codecs/FPS/speed/sync/metadata/dates and Unicode paths; originals unchanged')
 gray=np.frombuffer(run([FF,'-v','error','-i',state['Outputs'][2],'-frames:v','1','-f','rawvideo','-pix_fmt','rgb24','-threads','1','-']),dtype=np.uint8).reshape(-1,3).astype(float)
 assert np.mean(abs(gray[:,0]-gray[:,1])+abs(gray[:,1]-gray[:,2]))<4;check('Desaturation verified in decoded pixels')
 portrait=root/'portrait.mp4';run([FF,'-v','error','-f','lavfi','-i','testsrc2=size=180x320:rate=25:duration=2','-c:v','libx264','-threads','2',portrait])
 w,s=execute(root,'portrait',[portrait],[profile(Crop=1,Resolution='720')]);good(s,1);v=probe(s['Outputs'][0])['streams'][0];assert(v['width'],v['height'])==(180,320)
 rotated=root/'rotated.mp4';run([FF,'-v','error','-display_rotation:v:0','90','-i',source,'-c','copy',rotated]);w,s=execute(root,'rotate',[rotated]);good(s,1);v=probe(s['Outputs'][0])['streams'][0];assert(v['width'],v['height'])==(180,320)
 check('Portrait/silent video and rotation metadata')
 track=root/'Музыка & пример.wav';run([FF,'-v','error','-f','lavfi','-i','aevalsrc=0.15*sin(2*PI*(250*t+60*t*t)):s=48000:d=20','-c:a','pcm_s16le',track])
 w,s=execute(root,'shorts',[source],Shorts=True,Count=10,Music=[str(track)]);good(s,10);hashes=set()
 for i,path in enumerate(s['Outputs']):
  d=probe(path);assert abs(float(d['format']['duration'])-4)<.06;hashes.add(hashlib.sha256(Path(path).read_bytes()).hexdigest())
  if i<3:
   samples=audio(path,.7,.08);freq=np.fft.rfftfreq(len(samples),1/48000)[np.argmax(abs(np.fft.rfft(samples*np.hanning(len(samples)))))]
   target=250+120*([0,16,8][i]+.74);assert abs(freq-target)<35,(freq,target)
 assert len(hashes)==10;check('Ten distinct encoded shorts, original duration, music replacement and actual start/end/middle cuts')
 short=root/'short.wav';run([FF,'-v','error','-f','lavfi','-i','sine=frequency=1700:duration=0.6',short]);w,s=execute(root,'loop',[source],Shorts=True,Count=2,Music=[str(short)]);good(s,2);assert np.sqrt(np.mean(audio(s['Outputs'][0],2,1)**2))>.01
 w,s=execute(root,'queue',[source,portrait],Shorts=True,Count=2,Music=[str(track),str(short)]);good(s,4)
 check('Music looping and multiple-input queue including silent portrait')
 for name,opts in [('range',dict(Ranges=ranges(Crop=dict(Min=2,Max=1)))),('count',dict(Count=11)),('loopoff',dict(Ranges=ranges(Loop=False),Music=[str(short)]))]:
  args=dict(Shorts=True,Count=1,Music=[str(track)]);args.update(opts);w,s=execute(root,name,[source],**args);assert s['Errors'] and not s['Outputs'],s
 broken=root/'broken.mp4';broken.write_text('broken');w,s=execute(root,'broken',[broken,source]);assert len(s['Errors'])==1 and len(s['Outputs'])==1,s
 check('Invalid settings/short tracks rejected; damaged video does not stop next valid input')
 clips=root/'clips';clips.mkdir();run([FF,'-v','error','-f','lavfi','-i','sine=frequency=800:duration=0.5',clips/'0001.wav']);run([FF,'-v','error','-f','lavfi','-i','sine=frequency=1300:duration=1.1',clips/'0002.wav'])
 srt=root/'text.srt';srt.write_text('1\n00:00:00,500 --> 00:00:01,200\nОдин.\n\n2\n00:00:02,000 --> 00:00:03,000\nДва.\n',encoding='utf-8-sig')
 def make_audio(name,srtfile,cancel=False):
  w=root/name;w.mkdir();(w/'out').mkdir();payload=dict(Srt=str(srtfile),Video=str(source),Output=str(w/'out'),Cache=str(root/'cache'),FFmpeg=FF,FFprobe=FP,Provider='clips',Voice='',Key='',Clips=str(clips));(w/'job.json').write_text(json.dumps(payload));
  if cancel:(w/'cancel').touch()
  run([PS,'-NoProfile','-File',ROOT/'tests/RunTest.ps1','-JobPath',w/'job.json','-Kind','audio']);return w,json.loads((w/'result.json').read_text())
 w,s=make_audio('audio',srt);good(s,1);narration=s['Outputs'][0]
 with wave.open(narration) as f:assert f.getnframes()==4*48000;pcm=np.frombuffer(f.readframes(f.getnframes()),dtype='<i2')
 for lo,hi in [(0,.5),(1.25,2),(3,4)]:assert not np.any(pcm[round(lo*48000):round(hi*48000)])
 for lo,hi in [(.55,.95),(2.05,2.95)]:assert np.max(abs(pcm[round(lo*48000):round(hi*48000)]))>100
 w,s=execute(root,'narration',[source],[profile(Speed=1.05)],Narrations=[dict(Video=str(source),Slot=1,Audio=narration)]);good(s,1);assert np.max(abs(audio(s['Outputs'][0],1.35,.3)))<.001
 check('SRT phrase alignment, sample-accurate pauses, duration fitting and narration replacement')
 overlap=root/'overlap.srt';overlap.write_text('1\n00:00:00,000 --> 00:00:02,000\nA\n\n2\n00:00:01,000 --> 00:00:03,000\nB');w,s=make_audio('overlap',overlap);assert s['Errors'] and not list((w/'out').iterdir())
 long=root/'long.srt';long.write_text('1\n00:00:00,000 --> 00:00:00,100\nA');w,s=make_audio('toolong',long);assert s['Errors'] and not list((w/'out').iterdir())
 w,s=make_audio('cancel-audio',srt,True);assert s['Cancelled'] and not list((w/'out').iterdir()),s
 check('Invalid/overlapping/overlong narration and audio cancellation cleanup')
 w,c=create(root,'cancel-video',[source]*10,profiles);p=subprocess.Popen(list(map(str,c)),stdout=subprocess.PIPE,stderr=subprocess.PIPE);deadline=time.monotonic()+15
 while time.monotonic()<deadline and not (w/'progress.txt').exists():time.sleep(.1)
 (w/'cancel').touch();stdout,stderr=p.communicate(timeout=20);assert p.returncode==0,stderr
 s=json.loads((w/'result.json').read_text());assert s['Cancelled'] and not list((w/'out').glob('.processing_*')),s
 check('Cancellation kills encoding and removes partial video')
 print(str(len(checks))+' native-core integration groups passed.',flush=True)
