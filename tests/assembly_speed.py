"""Same production plan, old vs single-pass CPU renderer; actual MP4, not a timing estimate."""
import argparse
import json
from pathlib import Path
import re
import statistics
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from assembly_integration import png, template, value

def run(args, timeout=600):
    p = subprocess.run([str(x) for x in args], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=timeout)
    if p.returncode:
        raise RuntimeError(p.stderr[-5000:] + '\n' + p.stdout[-1000:])
    return p

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--host', required=True); ap.add_argument('--dotnet', default='dotnet')
    ap.add_argument('--ffmpeg', default='ffmpeg'); ap.add_argument('--ffprobe', default='ffprobe')
    ap.add_argument('--portable', action='store_true'); ap.add_argument('--output', default='artifacts/assembly-speed.json')
    args = ap.parse_args(); host = Path(args.host).resolve(); reports=[]
    with tempfile.TemporaryDirectory(prefix='assembly-speed-') as temp:
        root=Path(temp); (root/'Видео').mkdir(); (root/'Торговля').mkdir()
        for index,folder in enumerate(['head0','head1','bot','cta']):
            (root/folder).mkdir(); png(root/folder/'image.png',(36,100 if folder=='bot' else 32,324,200),(240-index*5,240-index*3,240,255))
        for group,folder in enumerate(['Видео','Торговля']):
            for i in range(2):
                run([args.ffmpeg,'-v','error','-y','-f','lavfi','-i',f'testsrc2=s=720x1280:r=30:d=6,hue=h={i*35+group*70}','-c:v','libx264','-threads','2',root/folder/f'{i}.mp4'])
        run([args.ffmpeg,'-v','error','-y','-f','lavfi','-i','sine=frequency=440:duration=6',root/'music.wav'])
        for effect,width,height in [('fade',720,1280),('pull',360,640)]:
            path=root/'template.xml'; template(path,root,effect,1,'benchmark-'+effect)
            doc=ET.parse(path); top=doc.getroot(); top.find('Width').text=str(width); top.find('Height').text=str(height)
            value(top,'RenderMode','cpu'); doc.write(path,encoding='utf-8',xml_declaration=True)
            out=root/effect; out.mkdir()
            result=json.loads(run([args.dotnet,host,'benchmark',path,args.ffmpeg,args.ffprobe,out,'portable' if args.portable else 'native']).stdout)
            old=statistics.median(result['LegacySeconds']); new=statistics.median(result['SinglePassSeconds'])
            # Different encoder presets need not create identical pixels. Compare decoded video.
            quality=run([args.ffmpeg,'-hide_banner','-i',out/'reference.mp4','-i',out/'single-pass.mp4','-filter_complex_threads','1','-lavfi','ssim','-f','null','-'])
            score=float(re.findall(r'All:([\d.]+)',quality.stderr)[-1]); assert score > .92, (effect,score)
            assert result['Report']['EncodePasses']==1 and result['Report']['EncodeAttempts']==1
            assert result['Report']['RasterCacheHits']>=4, 'unchanged images were rasterized again'
            assert new < old*1.2, ('single-pass performance regression',effect,old,new)
            report=dict(Effect=effect,Width=width,Height=height,LegacyMedianSeconds=old,SinglePassMedianSeconds=new,Speedup=old/new,SSIM=score,**result)
            reports.append(report); print(json.dumps(report),flush=True)
            # Force an unavailable encoder: the production fallback must retry exactly once.
            fallback=json.loads(run([args.dotnet,host,'fallback',path,args.ffmpeg,args.ffprobe,out,'portable' if args.portable else 'native']).stdout)
            assert fallback['Encoder']=='libx264' and fallback['EncodeAttempts']==2 and 'hardware_missing_test_encoder' in fallback['Fallback']
        doc=ET.parse(path); doc.getroot().find('Id').text='cancel-during-encoding'; doc.write(path,encoding='utf-8',xml_declaration=True)
        cancelled=json.loads(run([args.dotnet,host,'cancel-encode',path,args.ffmpeg,args.ffprobe,root/'cancel-history','portable' if args.portable else 'native']).stdout)
        assert cancelled['Cancelled'] and not cancelled['Outputs'] and not cancelled['Errors'], cancelled
        assert not (root/'cancel-history/cancel-during-encoding.xml').exists(), 'cancelled encoding consumed a combination'
        assert not list((root/'out').glob('.assembly-*')), 'cancelled encoder left temporary scenes or rasters'
    output=Path(args.output); output.parent.mkdir(parents=True,exist_ok=True); output.write_text(json.dumps(reports,indent=2),encoding='utf-8')
    print('PASS: same-plan CPU benchmarks, decoded-picture SSIM, raster cache and actual encoder-error fallback')

if __name__=='__main__': main()
