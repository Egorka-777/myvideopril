# Exercises the engine from the delivered Windows EXE; no UI emulation.
from pathlib import Path
exec(compile((Path(__file__).parent/'integration.py').read_text().split('with tempfile.TemporaryDirectory')[0],str(Path(__file__)),'exec'))
import math,datetime

def amplitude(pcm,hz):
 n=len(pcm);window=np.hanning(n)
 return 2*abs(np.sum(pcm*window*np.exp(-2j*np.pi*hz*np.arange(n)/48000)))/window.sum()
def presets(root,count):
 path=root/('presets'+str(count)+'.json')
 run([PS,'-NoProfile','-File',ROOT/'tests/ExportPresets.ps1','-Count',count,'-Output',path])
 return json.loads(path.read_text())[:count]
with tempfile.TemporaryDirectory(prefix='videobatch-update-') as temp:
 root=Path(temp);source=root/'Original.mp4'
 run([FF,'-v','error','-f','lavfi','-i','testsrc2=size=640x360:rate=30:duration=3','-f','lavfi','-i','sine=frequency=440:sample_rate=48000:duration=3','-c:v','libx264','-threads','2','-c:a','aac','-metadata','title=ORIGINAL',source]);original=source.read_bytes()
 for count in [1,2,5,10]:
  ps=presets(root,count)
  for key in ['Speed','Crop','Gray','ScalePercent','FpsPercent','BitratePercent','CustomDate']:
   assert len({p[key] for p in ps})==count,(count,key)
  assert all(p['Metadata']==p['FileDate']=='custom' and p['Compression']=='sourcebitrate' for p in ps)
  w,state=execute(root,'auto'+str(count),[source],ps);good(state,count);fpses=set();dates=set();rates=set();hashes=set()
  for i,(path,p) in enumerate(zip(state['Outputs'],ps)):
   d=probe(path);v=next(s for s in d['streams'] if s['codec_type']=='video');n,den=map(float,v['avg_frame_rate'].split('/'));fps=n/den;fpses.add(fps)
   assert abs(fps-30*p['FpsPercent']/100)<.002,(fps,p)
   assert v['width']==math.floor(640*p['ScalePercent']/100/2)*2
   assert v['height']==math.floor(360*p['ScalePercent']/100/2)*2
   assert Path(path).suffix=='.'+['mp4','mov','mkv'][i%3]
   assert abs(float(d['format']['duration'])-3/p['Speed'])<.12
   stamp=float(p['CustomDate'].split('(')[1].split('+')[0].split(')')[0])/1000
   assert abs(Path(path).stat().st_mtime-stamp)<1
   creation=d['format'].get('tags',{}).get('creation_time')
   assert creation,(path,d);dates.add(creation)
   assert abs(datetime.datetime.fromisoformat(creation.replace('Z','+00:00')).timestamp()-stamp)<1
   assert 'ORIGINAL' not in json.dumps(d);rates.add(d['format']['bit_rate']);hashes.add(hashlib.sha256(Path(path).read_bytes()).hexdigest())
  assert len(fpses)==len(dates)==len(hashes)==count
  if count>1:assert len(rates)>1
 check('Automatic 1/2/5/10: all profile groups differ, actual FPS/dimensions/containers/file and metadata dates checked')
 music=root/'background.wav';run([FF,'-v','error','-f','lavfi','-i','sine=frequency=1300:sample_rate=48000:duration=1',music])
 w,s=execute(root,'mix',[source],[profile()],Music=[str(music)]);good(s,1);mix=audio(s['Outputs'][0],.7,1);voice=audio(source,.7,1);track=audio(music,0,1)
 voice_gain=20*math.log10(amplitude(mix,440)/amplitude(voice,440));music_gain=20*math.log10(amplitude(mix,1300)/amplitude(track,1300))
 assert abs(voice_gain)<.3,voice_gain
 assert abs(music_gain+7.5)<.3,music_gain
 assert abs(float(probe(s['Outputs'][0])['format']['duration'])-3)<.08
 assert amplitude(audio(s['Outputs'][0],2.2,.5),1300)>.02
 check('Original voice retained at same level; looped background measured %.2f dB (target -7.5)'%music_gain)
 narration=root/'voice.wav';run([FF,'-v','error','-f','lavfi','-i',r'aevalsrc=0.125*sin(2*PI*800*t)*between(t\,0.5\,1.5):s=48000:d=3',narration])
 w,s=execute(root,'narration-mix',[source],[profile(Speed=1.05)],Music=[str(music)],Narrations=[dict(Video=str(source),Slot=1,Audio=str(narration))]);good(s,1)
 mixed=audio(s['Outputs'][0],.7,.5);assert amplitude(mixed,800)>.10 and amplitude(mixed,1300)>.04 and amplitude(mixed,440)<.002
 assert amplitude(audio(s['Outputs'][0],1.8,.5),800)<.001
 check('Assigned narration replaces original voice, follows speed and keeps pauses over background music')
 silent=root/'silent.mp4';run([FF,'-v','error','-i',source,'-an','-c:v','copy',silent])
 w,s=execute(root,'silent-bg',[silent],[profile()],Music=[str(music)],BackgroundDb=-12);good(s,1)
 gain=20*math.log10(amplitude(audio(s['Outputs'][0],.7,1),1300)/amplitude(track,1300));assert abs(gain+12)<.3,gain
 check('Silent input gains audio; custom background level -12 dB measured')
 chirp=root/'cuts.wav';run([FF,'-v','error','-f','lavfi','-i','aevalsrc=0.125*sin(2*PI*(1500*t+40*t*t)):s=48000:d=20',chirp])
 w,s=execute(root,'bg-cuts',[source],[profile(1),profile(2),profile(3)],Music=[str(chirp)]);good(s,3)
 for i,path in enumerate(s['Outputs']):
  pcm=audio(path,.7,.08);spec=abs(np.fft.rfft(pcm*np.hanning(len(pcm))));freq=np.fft.rfftfreq(len(pcm),1/48000);spec[freq<900]=0;peak=freq[np.argmax(spec)];target=1500+80*([0,8.5,17][i]+.74);assert abs(peak-target)<35,(peak,target)
 check('One long music file uses separate start/middle/end excerpts in normal mode')
 w,s=execute(root,'bg-pool',[source],[profile(i+1) for i in range(4)],Music=[str(music),str(chirp)]);good(s,4)
 kinds=[]
 for path in s['Outputs']:
  pcm=audio(path,.7,.2);kinds.append(amplitude(pcm,1300)>.02)
 assert kinds[0]!=kinds[1] and kinds[0]==kinds[2] and kinds[1]==kinds[3],kinds
 check('Music pool alternates tracks before reuse')
 w,s=execute(root,'shorts-auto',[source],Shorts=True,Count=10,Music=[str(chirp)],Ranges=ranges(TechnicalVariants=True));good(s,10)
 dates=set();fpses=set()
 for i,path in enumerate(s['Outputs']):
  d=probe(path);v=next(s for s in d['streams'] if s['codec_type']=='video');dates.add(d['format']['tags']['creation_time']);fpses.add(v['avg_frame_rate']);assert abs(float(d['format']['duration'])-3)<.1
  assert Path(path).suffix=='.'+['mp4','mov','mkv'][i%3]
 assert len(dates)==len(fpses)==10
 check('Shorts automatic technical variation preserves original duration and replaces original sound')
 for level in [0,1,-61]:
  w,s=execute(root,'invalid-db'+str(level),[source],Music=[str(music)],BackgroundDb=level);assert s['Errors'] and not s['Outputs'],s
 w,s=execute(root,'missing-bg',[source],Music=[str(root/'missing.wav')]);assert s['Errors'] and not s['Outputs']
 assert source.read_bytes()==original
 check('Invalid background gain and missing music report errors; original is unchanged')
 print(str(len(checks))+' update integration groups passed.',flush=True)
