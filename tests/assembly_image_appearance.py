"""Real renders: one output per input, source length, image-only entrances and audio."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from assembly_integration import run, value, png, layer


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--host', required=True)
    ap.add_argument('--ffmpeg', required=True)
    ap.add_argument('--ffprobe', required=True)
    a = ap.parse_args()
    with tempfile.TemporaryDirectory(prefix='image-appearance-') as temp:
        root = Path(temp)
        (root / 'videos').mkdir()
        (root / 'images').mkdir()
        # Include an input shorter than the appearance delay and one longer than 60 seconds.
        for i, duration in enumerate([.6, 2.4, 61.2]):
            run([a.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i',
                 f'color=blue:s=360x640:r=30:d={duration}', '-f', 'lavfi', '-i',
                 f'sine=frequency=440:duration={duration}', '-c:v', 'libx264',
                 '-threads', '1', '-c:a', 'aac', '-shortest', root / 'videos' / f'{i}.mp4'])
        png(root / 'images' / 'red.png', (100, 200, 260, 440), (255, 0, 0, 255))
        png(root / 'images' / 'green.png', (100, 200, 260, 440), (0, 255, 0, 255))
        for global_layer, effect in [(False, 'cut'), (False, 'fade'), (True, 'fade'),
                                     (False, 'zoomsoft'), (False, 'zoomout'),
                                     (False, 'pull'), (False, 'swipe'), (True, 'random')]:
            key = effect + ('-global' if global_layer else '')
            doc = ET.Element('AssemblyTemplate')
            for k, v in dict(Id=key, Materials=root, Videos='videos', Output=root / key,
                             Width=360, Height=640, Fps=30, Count=6, Transition='cut',
                             DurationByVideo='true', SceneTimingVersion=1, RenderMode='cpu',
                             RandomVideoStart='true', LoopShortVideos='false').items():
                value(doc, k, v)
            scenes = ET.SubElement(doc, 'Scenes')
            scene = ET.SubElement(scenes, 'AssemblyScene')
            value(scene, 'Duration', 4)
            value(scene, 'VideoMode', 'own')
            value(scene, 'Videos', 'videos')
            layers = ET.SubElement(doc if global_layer else scene, 'GlobalLayers' if global_layer else 'Layers')
            layer(layers, 'Badge', 'images', start=.8)
            image = layers[0]
            value(image, 'AppearanceTransition', effect)
            value(image, 'AppearanceDuration', .4)
            path = root / 'template.xml'
            ET.ElementTree(doc).write(path, encoding='utf-8')
            result = json.loads(run(['dotnet', a.host, 'render', path, a.ffmpeg, a.ffprobe,
                                     root / ('history-' + key), 'native']))
            assert not result['Errors'] and len(result['Outputs']) == 6, result
            recipes = [ET.parse(Path(file).parent / '.videobatch-reports' / (Path(file).name + '.assembly.xml')).getroot() for file in result['Outputs']]
            assert len({recipe.findtext('Signature') for recipe in recipes}) == 6
            remaining = json.loads(run(['dotnet', a.host, 'capacity', path, a.ffmpeg, a.ffprobe,
                                        root / ('history-' + key)]))
            assert remaining['BatchLimit'] == 0, remaining
            lengths = []
            for file in result['Outputs']:
                info = json.loads(run([a.ffprobe, '-v', 'error', '-show_streams', '-of', 'json', file]))
                video = next(s for s in info['streams'] if s['codec_type'] == 'video')
                duration = float(video['duration'])
                lengths.append(round(duration, 1))
                assert any(s['codec_type'] == 'audio' for s in info['streams']), info
                def pixel(at, x, y):
                    data = subprocess.check_output([a.ffmpeg, '-v', 'error', '-ss', str(at), '-i',
                                                    file, '-frames:v', '1', '-f', 'rawvideo',
                                                    '-pix_fmt', 'rgb24', '-threads', '1', '-'])
                    off = (y * 360 + x) * 3
                    return tuple(data[off:off + 3])
                before = pixel(.2, 180, 320)
                assert before[2] > 180 and before[0] < 50, (key, before)
                outside = pixel(min(duration - .1, 1.5), 10, 10)
                assert outside[2] > 180 and outside[0] < 50, (key, 'background changed', outside)
                if duration > 1.5:
                    after = pixel(1.5, 180, 320)
                    assert max(after[:2]) > 150 and after[2] < 80, (key, after)
                    if effect == 'fade':
                        middle = pixel(1.0, 180, 320)
                        assert 40 < max(middle[:2]) < 210 and 40 < middle[2] < 210, (key, middle)
            assert sorted(lengths) == [.6, .6, 2.4, 2.4, 61.2, 61.2], lengths
            print('PASS:', key, 'source lengths, distinct video/image combinations, alpha/background, delay, audio', flush=True)
            if effect == 'cut':
                # The same mode must also work when the source pool has just one file.
                doc.find('Videos').text = str(root / 'videos' / '1.mp4')
                scene.find('Videos').text = str(root / 'videos' / '1.mp4')
                doc.find('Count').text = '1'
                doc.find('Id').text = 'single'
                doc.find('Output').text = str(root / 'single')
                ET.ElementTree(doc).write(path, encoding='utf-8')
                one = json.loads(run(['dotnet', a.host, 'render', path, a.ffmpeg, a.ffprobe,
                                      root / 'history-single', 'native']))
                assert not one['Errors'] and len(one['Outputs']) == 1, one
                print('PASS: single input -> single output', flush=True)


if __name__ == '__main__':
    main()
