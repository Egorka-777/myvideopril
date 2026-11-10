"""Real FFmpeg/production-engine tests for advertising freeze insertions."""
import argparse
import array
import json
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

from assembly_integration import run, value


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--host', required=True)
    parser.add_argument('--ffmpeg', default='ffmpeg')
    parser.add_argument('--ffprobe', default='ffprobe')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='assembly-insertions-') as temp:
        root = Path(temp)
        # A horizontal moving white box and a pitch change after two seconds
        # distinguish a genuine resumed timeline from a restarted/advanced clip.
        base = root / 'base.mp4'
        run([args.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i',
             "color=c=blue:s=360x640:r=30:d=4,drawbox=x=0:y=100:w=40:h=40:c=white:t=fill,scroll=horizontal=.025",
             '-f', 'lavfi', '-i', "aevalsrc='0.3*sin(2*PI*if(lt(t,2),440,660)*t)':s=48000:d=4",
             '-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv420p', '-c:a', 'aac', base])
        ad = root / 'ad.mp4'
        run([args.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i', 'color=c=red:s=180x180:r=30:d=1',
             '-f', 'lavfi', '-i', 'sine=frequency=1000:sample_rate=48000:duration=1',
             '-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv420p', '-c:a', 'aac', ad])
        audio = root / 'ad.wav'
        run([args.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i',
             'sine=frequency=1200:sample_rate=48000:duration=1', audio])
        photo = root / 'ad.png'
        run([args.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i',
             'color=c=green:s=180x180', '-frames:v', '1', '-threads', '1', photo])

        def render(key, insertions):
            doc = ET.Element('AssemblyTemplate')
            for name, text in dict(Id=key, Materials=root, Videos=base, Output=root / key,
                                   Width=360, Height=640, Fps=30, Count=1, Transition='cut',
                                   RenderMode='cpu', LoopShortVideos='false', AllowVideoReuse='true',
                                   PackStudioVersion=1, SceneTimingVersion=1).items():
                value(doc, name, text)
            scene = ET.SubElement(ET.SubElement(doc, 'Scenes'), 'AssemblyScene')
            value(scene, 'Name', 'Main')
            value(scene, 'Duration', 4)
            items = ET.SubElement(doc, 'Insertions')
            for source, start, end, image_duration in insertions:
                item = ET.SubElement(items, 'AssemblyInsertion')
                for name, text in dict(Source=source, Start=start, End=end or 0,
                                       RandomTime=str(end is not None).lower(), ImageDuration=image_duration).items():
                    value(item, name, text)
            template = root / (key + '.xml')
            ET.ElementTree(doc).write(template, encoding='utf-8', xml_declaration=True)
            report = json.loads(run(['dotnet', args.host, 'run', template, args.ffmpeg, args.ffprobe, root / ('history-' + key)]))
            assert not report['Errors'] and len(report['Outputs']) == 1, report
            return Path(report['Outputs'][0])

        def frame(path, at):
            return subprocess.check_output([args.ffmpeg, '-v', 'error', '-i', str(path), '-ss', str(at),
                                            '-frames:v', '1', '-pix_fmt', 'rgb24', '-threads', '1', '-f', 'rawvideo', '-'], timeout=60)

        def pixel(data, x, y):
            at = (y * 360 + x) * 3
            return tuple(data[at:at + 3])

        def difference(a, b):
            return sum(abs(x - y) for x, y in zip(a, b)) / len(a)

        def pitch(path, at):
            data = subprocess.check_output([args.ffmpeg, '-v', 'error', '-i', str(path), '-ss', str(at),
                                           '-t', '0.2', '-vn', '-ac', '1', '-ar', '48000', '-f', 'f32le', '-'], timeout=60)
            samples = array.array('f', data)
            crossings = sum(samples[i] < 0 <= samples[i + 1] for i in range(len(samples) - 1))
            return crossings / .2, max(abs(x) for x in samples)

        baseline = render('baseline', [])
        for key, source, frequency in [('video', ad, 1000), ('audio', audio, 1200), ('photo', photo, 0)]:
            output = render(key, [(source, 1, None, 1)])
            info = json.loads(run([args.ffprobe, '-v', 'error', '-show_streams', '-show_format', '-of', 'json', output]))
            video = next(s for s in info['streams'] if s['codec_type'] == 'video')
            assert abs(float(video['duration']) - 5) < .04, (key, video['duration'])
            assert difference(frame(output, .5), frame(baseline, .5)) < 3
            assert difference(frame(output, 2.5), frame(baseline, 1.5)) < 3, key
            assert difference(frame(output, 3.5), frame(baseline, 2.5)) < 3, key
            frozen_a, frozen_b = frame(output, 1.2), frame(output, 1.8)
            # Ad is smaller than frame, leaving the frozen underlying border visible.
            border_a = b''.join(frozen_a[y * 360 * 3:y * 360 * 3 + 80 * 3] for y in range(80, 180))
            border_b = b''.join(frozen_b[y * 360 * 3:y * 360 * 3 + 80 * 3] for y in range(80, 180))
            assert difference(border_a, border_b) < 1, (key, 'underlying video moved')
            if key == 'video':
                r, g, b = pixel(frozen_a, 180, 320)
                assert r > 200 and g < 30 and b < 30, (key, (r, g, b))
            elif key == 'photo':
                r, g, b = pixel(frozen_a, 180, 320)
                assert g > 90 and r < 30 and b < 30, (key, (r, g, b))
            else:
                assert difference(frozen_a, frame(baseline, 29 / 30)) < 3
            hz, peak = pitch(output, 1.3)
            assert (abs(hz - frequency) < 15 if frequency else peak < .002), (key, hz, peak)
            for at, expected in [(.4, 440), (2.4, 440), (3.4, 660)]:
                hz, _ = pitch(output, at)
                assert abs(hz - expected) < 15, (key, at, hz)
            print(key + ': duration, frozen frame, resumed timeline and isolated audio OK')
        # Both pauses use BASE timeline seconds, not shifted output seconds.
        output = render('multiple', [(audio, 1, None, 1), (photo, 2, None, .5)])
        info = json.loads(run([args.ffprobe, '-v', 'error', '-show_format', '-of', 'json', output]))
        assert abs(float(info['format']['duration']) - 5.5) < .06
        assert difference(frame(output, 4), frame(baseline, 2.5)) < 3
        output = render('random', [(ad, 1, 1.5, 1)])
        assert difference(frame(output, .5), frame(baseline, .5)) < 3
        assert difference(frame(output, 3), frame(baseline, 2)) < 3
        print('Multiple base-time insertions and random-range insertion OK')


if __name__ == '__main__':
    main()
