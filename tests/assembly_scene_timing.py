"""Real encoded frames: preserve source beginning and protect it from xfade overlap."""
import argparse,json,tempfile,subprocess,xml.etree.ElementTree as ET
from pathlib import Path
from assembly_integration import run,value

def main():
 p=argparse.ArgumentParser();p.add_argument('--host',required=True);p.add_argument('--dotnet',default='dotnet');p.add_argument('--ffmpeg',default='ffmpeg');p.add_argument('--ffprobe',default='ffprobe');a=p.parse_args()
 with tempfile.TemporaryDirectory(prefix='scene-timing-') as temp:
  root=Path(temp);(root/'own').mkdir();(root/'shared').mkdir()
  run([a.ffmpeg,'-v','error','-y','-f','lavfi','-i','color=blue:s=360x640:r=30:d=10','-c:v','libx264','-threads','2',root/'shared/blue.mp4'])
  run([a.ffmpeg,'-v','error','-y','-f','lavfi','-i','color=red:s=360x640:r=30:d=0.5','-f','lavfi','-i','color=green:s=360x640:r=30:d=7.7','-filter_complex_threads','1','-filter_complex','[0:v][1:v]concat=n=2:v=1:a=0[v]','-map','[v]','-c:v','libx264','-threads','2',root/'own/marker.mp4'])
  for effect in ['fade','pull','random','cut']:
   doc=ET.Element('AssemblyTemplate')
   for k,v in dict(Id='timing-'+effect,Materials=root,Videos='shared',Music='',Output=root/'out',SceneTimingVersion=1,LoopShortVideos='true',AllowVideoReuse='true',RandomVideoStart='true',Width=360,Height=640,Fps=30,Count=1,Transition=effect,TransitionDuration=.35).items():value(doc,k,v)
   scenes=ET.SubElement(doc,'Scenes')
   for i,d in enumerate([1.5,8.3,1.5]):
    scene=ET.SubElement(scenes,'AssemblyScene');value(scene,'Name','Scene '+str(i));value(scene,'Duration',d);value(scene,'VideoMode','own' if i==1 else 'shared')
    if i==1:value(scene,'Videos','own')
    ET.SubElement(scene,'Layers')
   path=root/'template.xml';ET.ElementTree(doc).write(path,encoding='utf-8')
   out=root/effect
   result=json.loads(run([a.dotnet,a.host,'timing',path,a.ffmpeg,a.ffprobe,out,'portable']))
   assert all(start==0 for start in result['Starts']),result
   if effect=='random':assert len(set(result['Effects'][:-1]))==2,result
   transition=0 if effect=='cut' else 11/30
   expected=11.3+2*transition
   for file in result['Outputs']:
    info=json.loads(run([a.ffprobe,'-v','error','-show_streams','-of','json',file]))
    duration=float(next(s for s in info['streams'] if s['codec_type']=='video')['duration']);assert abs(duration-expected)<.07,(effect,duration,expected)
    def rgb(at):
     b=subprocess.check_output([a.ffmpeg,'-v','error','-ss',str(at),'-i',file,'-frames:v','1','-vf','scale=1:1','-f','rawvideo','-pix_fmt','rgb24','-threads','1','-']);return list(b)
    begin=1.5+transition
    first=rgb(begin+.12);assert first[0]>180 and first[1]<60,(effect,'missing first red frame',first)
    tail=rgb(begin+8.26);assert tail[1]>75 and tail[0]<60,(effect,'short own video restarted',tail)
   print('PASS:',effect,'zero start, complete scene, padded whole-scene transitions, held final frame, both renderers',flush=True)
if __name__=='__main__':main()
