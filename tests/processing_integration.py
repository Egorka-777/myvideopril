"""Render through the real net48 Core using a .NET 8 host; inspect decoded media, not filter strings."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import struct
import subprocess
import tempfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--host', required=True)
parser.add_argument('--dotnet', default='dotnet')
parser.add_argument('--ffmpeg', default='ffmpeg')
parser.add_argument('--ffprobe', default='ffprobe')
options = parser.parse_args()

def command(args, timeout=120):
    p = subprocess.run([str(x) for x in args], capture_output=True, timeout=timeout)
    assert p.returncode == 0, (p.stdout.decode(errors='replace'), p.stderr.decode(errors='replace'))
    return p.stdout

def probe(path):
    return json.loads(command([options.ffprobe, '-v', 'error', '-show_streams', '-show_format', '-of', 'json', path]))

def add(parent, key, value):
    node = ET.SubElement(parent, key)
    if isinstance(value, dict):
        for name, item in value.items(): add(node, name, item)
    elif isinstance(value, list):
        for item in value: add(node, 'string', item)
    else: node.text = str(value).lower() if isinstance(value, bool) else str(value)
    return node

def parameter(value, maximum=None):
    return {'Enabled': True, 'Values': {'Min': value, 'Max': value if maximum is None else maximum}}

def execute(root, name, source, processing=None, shorts=False, music=None, profile=None, count=1, narration=None, cancel=None, expect_error=False):
    folder = root / name; folder.mkdir(); output = folder / 'out'
    job = ET.Element('BatchJob'); add(job, 'Inputs', [str(source)])
    add(job, 'Output', output); add(job, 'FFmpeg', options.ffmpeg); add(job, 'FFprobe', options.ffprobe)
    add(job, 'Shorts', shorts); add(job, 'Count', count); add(job, 'BackgroundDb', -12.5)
    if music: add(job, 'Music', [str(music)])
    profiles = ET.SubElement(job, 'Profiles')
    for i in range(count):
        p = ET.SubElement(profiles, 'Profile'); add(p, 'Slot', i + 1)
        for key, value in (profile or {}).items(): add(p, key, value)
    ranges = ET.SubElement(job, 'Ranges'); add(ranges, 'TechnicalVariants', False); add(ranges, 'Volume', 100)
    if narration:
        bindings = ET.SubElement(job, 'Narrations'); binding = ET.SubElement(bindings, 'Binding')
        add(binding, 'Video', str(source)); add(binding, 'Slot', 1); add(binding, 'Audio', str(narration))
    if processing is not None:
        add(job, 'Processing', processing)
    path = folder / 'job.xml'; ET.ElementTree(job).write(path, encoding='utf-8', xml_declaration=True)
    args = [options.dotnet, options.host, 'run', path]
    if cancel is not None: args.append(cancel)
    result = json.loads(command(args))
    if cancel is not None:
        assert result['Cancelled'] and not list(output.glob('.processing_*')), result
    elif expect_error:
        assert result['Errors'] and not result['Outputs'] and not list(output.glob('.processing_*')), result
    else:
        assert not result['Cancelled'] and not result['Errors'] and len(result['Outputs']) == count, result
    return result['Outputs']

def samples(path, start=.7, duration=.8):
    raw = command([options.ffmpeg, '-v', 'error', '-ss', start, '-i', path, '-t', duration, '-vn', '-ac', '1', '-ar', '48000', '-f', 'f32le', '-'])
    return struct.unpack('<' + str(len(raw)//4) + 'f', raw)

def rms(values): return math.sqrt(statistics.fmean(x*x for x in values))
def frequency(values): return sum(a <= 0 < b for a, b in zip(values, values[1:])) / (len(values) / 48000)

def frame(path, at=.8):
    return command([options.ffmpeg, '-v', 'error', '-ss', at, '-i', path, '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'gray', '-threads', '1', '-'])

def difference(a, b):
    assert len(a) == len(b)
    return statistics.fmean(abs(x-y) for x, y in zip(a, b))

def check(name): print('PASS:', name, flush=True)

with tempfile.TemporaryDirectory(prefix='videobatch-effects-') as tmp:
    root = Path(tmp); source = root / "Оригинал & 'видео'.mp4"
    command([options.ffmpeg, '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=30:duration=4', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=4', '-c:v', 'libx264', '-threads', '2', '-c:a', 'aac', '-metadata', 'title=SOURCE_TITLE', source])
    original_hash = hashlib.sha256(source.read_bytes()).hexdigest(); original_time = source.stat().st_mtime_ns
    base = execute(root, 'legacy', source)[0]
    disabled = execute(root, 'disabled', source, {})[0]
    assert hashlib.sha256(Path(base).read_bytes()).digest() == hashlib.sha256(Path(disabled).read_bytes()).digest()
    check('disabled additions produce the same encoded file as the existing pipeline')
    trimmed = execute(root, 'trim_speed', source, {'Trim': parameter(15), 'Speed': parameter(125)})[0]
    data = probe(trimmed); assert abs(float(data['format']['duration']) - 4 * .85 / 1.25) < .1
    assert abs(frequency(samples(trimmed)) - 440) < 4
    video = next(s for s in data['streams'] if s['codec_type'] == 'video'); audio = next(s for s in data['streams'] if s['codec_type'] == 'audio')
    assert abs(float(video['duration']) - float(audio['duration'])) < .08
    check('trim and video speed change duration together, with audio sync and pitch preserved')
    pitch = execute(root, 'pitch', source, {'Pitch': parameter(150)})[0]
    assert abs(frequency(samples(pitch)) - 660) < 5 and abs(float(probe(pitch)['format']['duration']) - 4) < .1
    low = execute(root, 'low_pitch', source, {'Pitch': parameter(50)})[0]
    assert abs(frequency(samples(low)) - 220) < 4
    check('independent pitch, including lower limit, measured in decoded audio')
    # AM envelope distinguishes an actual tempo effect from a no-op on a steady sine.
    envelope = root / 'envelope.mp4'
    command([options.ffmpeg, '-v', 'error', '-f', 'lavfi', '-i', 'color=white:size=320x180:rate=30:duration=4', '-f', 'lavfi', '-i', 'aevalsrc=0.1*(1+0.7*sin(2*PI*2*t))*sin(2*PI*440*t):s=48000:d=4', '-c:v', 'libx264', '-threads', '2', '-c:a', 'aac', envelope])
    tempo = execute(root, 'tempo', envelope, {'Tempo': parameter(150)})[0]
    assert abs(frequency(samples(tempo, .3, 1)) - 440) < 5
    assert rms(samples(tempo, 3.2, .4)) < .001 and abs(float(probe(tempo)['format']['duration']) - 4) < .1
    values = samples(tempo, .2, 1.5)
    envelope_values = [rms(values[i:i+960]) for i in range(0, len(values)-959, 960)]
    assert sum(a < b > c for a, b, c in zip(envelope_values, envelope_values[1:], envelope_values[2:])) >= 4
    check('independent audio tempo changes envelope timing, preserves tone and pads the video tail')
    half = execute(root, 'half_volume', source, {'Volume': parameter(50)})[0]
    assert .45 < rms(samples(half)) / rms(samples(base)) < .55
    mod = execute(root, 'modulation', source, {'AudioModulation': parameter(50), 'ModulationFrequency': {'Min': 1, 'Max': 1}})[0]
    assert rms(samples(mod, .48, .08)) > rms(samples(mod, .98, .08)) * 1.25
    muted = execute(root, 'mute', source, {'Volume': parameter(0)})[0]; assert rms(samples(muted)) < .00001
    check('volume range, mute and real dynamic loudness modulation')
    solid = root / 'solid.mp4'
    command([options.ffmpeg, '-v', 'error', '-f', 'lavfi', '-i', 'color=white:size=320x180:rate=30:duration=4', '-c:v', 'libx264', '-threads', '2', solid])
    fade = execute(root, 'fade', solid, {'FadeIn': parameter(.5), 'FadeOut': parameter(.5)})[0]
    assert statistics.fmean(frame(fade, 0)) < 5 and statistics.fmean(frame(fade, 1)) > 230 and statistics.fmean(frame(fade, 3.9)) < 75
    assert not any(s['codec_type'] == 'audio' for s in probe(fade)['streams'])
    check('both video fades verified in decoded pixels; silent files remain silent')
    # Each spatial effect is checked on its own, so a missing effect cannot hide behind another.
    spatial = {'Mirror': True, 'Rotation': parameter(12), 'Crop': parameter(4), 'Brightness': parameter(-20), 'Contrast': parameter(60), 'Sharpness': parameter(-50), 'Noise': parameter(25), 'GridOpacity': parameter(50), 'Zoom': parameter(125)}
    baseline_frame = frame(base)
    for key, value in spatial.items():
        paths = execute(root, 'spatial_' + key, source, {key: value}, count=3 if key == 'Mirror' else 1)
        assert any(difference(frame(path), baseline_frame) > .5 for path in paths), key
        path = paths[0]
        stream = next(s for s in probe(path)['streams'] if s['codec_type'] == 'video')
        assert (stream['width'], stream['height']) == (320, 180), (key, stream)
    check('mirror, rotation, crop, brightness, contrast, blur, noise, grid and zoom each alter pixels and retain frame size')
    strong = execute(root, 'sharp_max', source, {'Sharpness': parameter(50)})[0]
    assert difference(frame(strong), baseline_frame) > .5
    check('sharpness maximum renders correctly')
    for fmt in ['mp4', 'mov', 'mkv']:
        path = execute(root, 'metadata_' + fmt, source, {'DeviceMetadata': True, 'Brands': ['OnePlus'], 'CaptureDateEnabled': True, 'CaptureDaysBack': 30, 'RandomizeCaptureTime': True}, profile={'Format': fmt})[0]
        tags = {k.lower(): v for k, v in probe(path)['format']['tags'].items()}
        assert tags['make'] == 'OnePlus' and tags['model'] in ['11', '12'] and tags['software'] == 'OxygenOS 14', tags
        from datetime import datetime, timezone, timedelta
        # New FFprobe merges native MOV time and mdta creation_time with ';'.
        # Validate every representation and equality rather than ignoring either tag.
        stamps = [datetime.fromisoformat(value.replace('Z', '+00:00')) for value in tags['creation_time'].split(';')]
        assert stamps and all(value == stamps[0] for value in stamps), tags
        stamp = stamps[0]
        assert datetime.now(timezone.utc)-timedelta(days=30, seconds=3) <= stamp <= datetime.now(timezone.utc)
        assert abs(Path(path).stat().st_mtime-stamp.timestamp()) < 2
        assert 'SOURCE_TITLE' not in json.dumps(tags)
    check('allowed brand/model/software and bounded capture/file dates survive MP4, MOV and MKV encoding')
    music = root / 'music.wav'
    command([options.ffmpeg, '-v', 'error', '-f', 'lavfi', '-i', 'sine=frequency=880:sample_rate=48000:duration=1', music])
    effects = {'Trim': parameter(10), 'Speed': parameter(125), 'Pitch': parameter(125), 'FadeIn': parameter(.2), 'FadeOut': parameter(.2), 'Rotation': parameter(-12), 'Mirror': True, 'Zoom': parameter(110), 'Noise': parameter(2)}
    short = execute(root, 'short_music', source, effects, shorts=True, music=music)[0]
    assert abs(float(probe(short)['format']['duration']) - 2.88) < .1 and abs(frequency(samples(short)) - 1100) < 6
    background = execute(root, 'long_background', source, effects, music=music)[0]
    assert abs(float(probe(background)['format']['duration']) - 2.88) < .1 and rms(samples(background)) > .01
    narration = execute(root, 'narration_background', source, effects, music=music, narration=music)[0]
    assert abs(float(probe(narration)['format']['duration']) - 2.88) < .1
    portrait = root / 'portrait.mp4'
    command([options.ffmpeg, '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=180x320:rate=25:duration=1', '-c:v', 'libx264', '-threads', '2', portrait])
    result = execute(root, 'portrait', portrait, {'Rotation': parameter(90), 'Zoom': parameter(110), 'FadeIn': parameter(10), 'FadeOut': parameter(10)})[0]
    stream = next(s for s in probe(result)['streams'] if s['codec_type'] == 'video'); assert (stream['width'], stream['height']) == (180, 320)
    check('combined effects with Shorts music, long background, narration, silent portrait and short-video fade clamp')
    variants = execute(root, 'variants', source, {'Rotation': parameter(-10, 10), 'Pitch': parameter(95, 105), 'Noise': parameter(1, 3)}, count=3)
    assert len({hashlib.sha256(Path(p).read_bytes()).digest() for p in variants}) == 3
    execute(root, 'cancel', source, effects, cancel=0)
    assert hashlib.sha256(source.read_bytes()).hexdigest() == original_hash and source.stat().st_mtime_ns == original_time
    check('fresh values per variant, cancellation cleanup and original file untouched')
    execute(root, 'pitch_invalid', source, {'Pitch': parameter(0)}, expect_error=True)
    execute(root, 'range_invalid', source, {'Rotation': parameter(12, 1)}, expect_error=True)
    execute(root, 'brands_invalid', source, {'DeviceMetadata': True, 'Brands': []}, expect_error=True)
    limits = execute(root, 'limits', source, {'Speed': parameter(200), 'Tempo': parameter(200), 'Pitch': parameter(50), 'Volume': parameter(200), 'Rotation': parameter(180), 'Contrast': parameter(150), 'Brightness': parameter(50), 'Noise': parameter(100), 'Zoom': parameter(130)})[0]
    assert abs(float(probe(limits)['format']['duration']) - 2) < .1
    check('parameter limits render and invalid ranges/empty brand selection are rejected')
print('Processing integration: OK', flush=True)
