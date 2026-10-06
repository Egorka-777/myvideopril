"""Render real MP4s with the production planner/engine; inspect frames, audio and history."""
import argparse
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import wave
import zlib
import xml.etree.ElementTree as ET


def run(args):
    p = subprocess.run([str(a) for a in args], capture_output=True, text=True, timeout=240)
    if p.returncode:
        raise RuntimeError(f'{args[0]} failed: {p.stderr[-4000:]}\n{p.stdout[-1000:]}')
    return p.stdout


def png(path, rect, color):
    # Standard-library RGBA PNG, already the full output canvas for portable Linux tests.
    width, height = 360, 640
    rows = []
    for y in range(height):
        row = bytearray(width * 4)
        if rect[1] <= y < rect[3]:
            for x in range(rect[0], rect[2]):
                row[x * 4:x * 4 + 4] = bytes(color)
        rows.append(b'\0' + row)
    def chunk(name, data):
        return struct.pack('>I', len(data)) + name + data + struct.pack('>I', zlib.crc32(name + data) & 0xffffffff)
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(b''.join(rows))) + chunk(b'IEND', b''))


def value(parent, name, text):
    ET.SubElement(parent, name).text = str(text)


def layer(parent, name, source, unique=False, fixed=False, start=0, end=0):
    node = ET.SubElement(parent, 'AssemblyLayer')
    for k, v in dict(Name=name, Source=source, Unique=str(unique).lower(), Fixed=str(fixed).lower(), X=0, Y=0, Width=1, Height=1, Start=start, End=end).items():
        value(node, k, v)


def template(path, root, transition='fade', count=3, ident='integration', timed=False):
    doc = ET.Element('AssemblyTemplate')
    for k, v in dict(Id=ident, Materials=root, Videos='Видео', Music=root / 'music.wav', Output=root / 'out', Width=360, Height=640, Fps=30, Count=count, Transition=transition, TransitionDuration=.3, RandomVideoStart='false').items():
        value(doc, k, v)
    scenes = ET.SubElement(doc, 'Scenes')
    for i, duration in enumerate([1.5, 1.5, 2]):
        scene = ET.SubElement(scenes, 'AssemblyScene')
        value(scene, 'Name', f'Scene {i + 1}'); value(scene, 'Duration', duration)
        if i == 1:
            value(scene, 'Videos', 'Торговля')
        layers = ET.SubElement(scene, 'Layers')
        if i < 2:
            layer(layers, 'Headline', f'head{i}', unique=True, start=.4 if timed else 0, end=1.0 if timed else 0)
        else:
            layer(layers, 'Bot', 'bot', fixed=True)
            layer(layers, 'CTA', 'cta')
    ET.ElementTree(doc).write(path, encoding='utf-8', xml_declaration=True)


def main():
    ap = argparse.ArgumentParser(); ap.add_argument('--host', required=True); ap.add_argument('--dotnet', default='dotnet')
    ap.add_argument('--ffmpeg', default='ffmpeg'); ap.add_argument('--ffprobe', default='ffprobe'); ap.add_argument('--portable', action='store_true')
    args = ap.parse_args()
    host = Path(args.host).resolve()
    assert run([args.dotnet, host, 'self-test']).strip() == 'True'
    with tempfile.TemporaryDirectory(prefix='assembly-real-') as temp:
        root = Path(temp) / 'материалы с пробелами'; root.mkdir()
        for folder in ['Видео', 'Торговля', 'head0', 'head1', 'bot', 'cta']:
            (root / folder).mkdir()
        for folder, colors in [('Видео', ['green', 'blue', 'yellow', 'cyan']), ('Торговля', ['purple', 'orange'])]:
            for i, color in enumerate(colors):
                run([args.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i', f'color=c={color}:s=360x640:r=30:d=7', '-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv420p', root / folder / f'{i}.mp4'])
        for folder in ['head0', 'head1']:
            for i in range(3):
                png(root / folder / f'{i}.png', (36, 32, 324, 110), (220 + i * 10, 220 if folder == 'head0' else 225, 220 if folder == 'head0' else 230, 255))
        png(root / 'bot' / 'bot.png', (80, 180, 280, 390), (255, 0, 0, 255))
        png(root / 'cta' / 'cta.png', (36, 480, 324, 590), (240, 240, 240, 255))
        run([args.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=8', root / 'music.wav'])
        path = root / 'template.xml'; history = root / 'history'
        def call(extra=()):
            cmd = [args.dotnet, host, 'render', path, args.ffmpeg, args.ffprobe, history, 'portable' if args.portable else 'native'] + list(extra)
            return json.loads(run(cmd))
        def frame(file, time):
            return subprocess.check_output([args.ffmpeg, '-v', 'error', '-ss', str(time), '-i', str(file), '-frames:v', '1', '-pix_fmt', 'rgb24', '-f', 'rawvideo', '-'])
        def pixel(data, x, y):
            return tuple(data[(y * 360 + x) * 3:(y * 360 + x) * 3 + 3])
        def verify(file):
            probe = json.loads(run([args.ffprobe, '-v', 'error', '-show_streams', '-show_format', '-of', 'json', file]))
            video = next(s for s in probe['streams'] if s['codec_type'] == 'video')
            assert video['width'] == 360 and video['height'] == 640
            assert abs(float(video['duration']) - 5) < .12, video['duration']
            assert any(s['codec_type'] == 'audio' for s in probe['streams'])
            assert min(pixel(frame(file, .75), 100, 60)) > 190, 'first headline missing'
            assert min(pixel(frame(file, 2.25), 100, 60)) > 190, 'second headline missing'
            bot = pixel(frame(file, 4), 180, 260)
            assert bot[0] > 230 and bot[1] < 25 and bot[2] < 25, bot
            assert min(pixel(frame(file, 4), 100, 520)) > 210, 'CTA missing'
            audio = subprocess.check_output([args.ffmpeg, '-v', 'error', '-i', str(file), '-vn', '-ac', '1', '-ar', '48000', '-f', 's16le', '-'])
            for t in [1.5, 3]:
                samples = struct.unpack('<' + 'h' * 960, audio[int((t - .01) * 48000) * 2:int((t + .01) * 48000) * 2])
                assert sum(v * v for v in samples) / len(samples) > 100000, 'music gap at scene boundary'
            recipe = ET.parse(str(file) + '.assembly.xml').getroot()
            videos = [n.findtext('Video/Path') for n in recipe.findall('Scenes/AssemblyScenePlan')]
            assert len(set(videos)) == 3 and Path(videos[1]).parent.name == 'Торговля'
        template(path, root)
        result = call(); assert len(result['Outputs']) == 3 and not result['Errors'], result
        for file in result['Outputs']:
            verify(file)
        stopped = call(); assert not stopped['Outputs'] and any('Закончились' in e for e in stopped['Errors']), stopped
        state = ET.parse(history / 'integration.xml').getroot()
        assert len(state.findall('UsedImages/string')) == 6, 'history did not consume headlines'
        for effect in ['cut', 'slideleft', 'hblur']:
            template(path, root, effect, 1, 'effect-' + effect)
            result = call(); assert len(result['Outputs']) == 1 and not result['Errors'], result; verify(result['Outputs'][0])
        # Timed overlay: visible in the middle, absent before/after its interval.
        template(path, root, 'cut', 1, 'timed', True); result = call(); assert result['Outputs'] and not result['Errors'], result
        early = pixel(frame(result['Outputs'][0], .1), 100, 60); late = pixel(frame(result['Outputs'][0], 1.2), 100, 60)
        assert min(early) < 190 and min(late) < 190
        assert min(pixel(frame(result['Outputs'][0], .75), 100, 60)) > 190
        template(path, root, 'fade', 1, 'cancel')
        cancelled = call(['100']); assert cancelled['Cancelled'] and not cancelled['Outputs'], cancelled
        assert not (history / 'cancel.xml').exists(), 'cancelled batch consumed headlines'
        assert not list((root / 'out').glob('.assembly-*')), 'temporary render folders leaked'
    print('PASS: 3-scene MP4, two headlines, fixed bot + CTA, continuous music, fade/cut/slide/blur, timing, history and cancellation')


if __name__ == '__main__':
    main()
