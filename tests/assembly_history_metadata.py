"""Real outputs across separate processes: persistence, migration and tags embedded in media."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from datetime import datetime, timezone, timedelta
from assembly_integration import png

parser = argparse.ArgumentParser()
parser.add_argument('--host', required=True)
parser.add_argument('--dotnet', default='dotnet')
parser.add_argument('--ffmpeg', default='ffmpeg')
parser.add_argument('--ffprobe', default='ffprobe')
parser.add_argument('--portable', action='store_true')
args = parser.parse_args()

def command(items):
    result = subprocess.run([str(x) for x in items], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=120)
    assert result.returncode == 0, result.stderr
    return result.stdout

def add(parent, name, text):
    ET.SubElement(parent, name).text = str(text)

def set_scope(scope):
    node = doc.find('HistoryScope')
    if node is None: add(doc, 'HistoryScope', scope)
    else: node.text = scope

def report(file, suffix):
    file = Path(file)
    return file.parent / '.videobatch-reports' / (file.name + suffix)

def probe(file):
    return json.loads(command([args.ffprobe, '-v', 'error', '-show_streams', '-show_format', '-of', 'json', file]))

with tempfile.TemporaryDirectory(prefix='history-metadata-') as temp:
    root = Path(temp)
    for folder in ['videos', 'music', 'images']:
        (root / folder).mkdir()
    command([args.ffmpeg, '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=180x320:rate=30:duration=1.5', '-c:v', 'libx264', '-threads', '1', root / 'videos/v.mp4'])
    for i, hz in enumerate([440, 880]):
        command([args.ffmpeg, '-v', 'error', '-f', 'lavfi', '-i', f'sine=frequency={hz}:sample_rate=48000:duration=1', root / f'music/{i}.wav'])
    for i, color in enumerate([(255, 0, 0, 255), (0, 255, 0, 255)]):
        png(root / f'images/{i}.png', (30, 30, 320, 120), color)
    doc = ET.Element('AssemblyTemplate')
    for k, v in dict(Id='studio-a', Materials=root, Videos='videos', Music='music', Output=root / 'out', Width=180, Height=320, Fps=30, Count=1, Transition='cut', RenderMode='cpu', PackStudioVersion=1, ObjectStudioVersion=1).items():
        add(doc, k, v)
    titles = ET.SubElement(doc, 'TitleBank'); add(titles, 'string', 'My test title')
    metadata = ET.SubElement(doc, 'OutputMetadata')
    add(metadata, 'DeviceMetadata', 'true'); add(metadata, 'CaptureDate', 'true'); add(metadata, 'Software', 'Telegram — пользовательская метка')
    brands = ET.SubElement(metadata, 'Brands'); add(brands, 'string', 'Apple')
    scenes = ET.SubElement(doc, 'Scenes'); scene = ET.SubElement(scenes, 'AssemblyScene'); add(scene, 'Duration', 1)
    layers = ET.SubElement(scene, 'Layers'); layer = ET.SubElement(layers, 'AssemblyLayer')
    for k, v in dict(Source='images', ObjectId='hook', X=0, Y=0, Width=1, Height=1).items(): add(layer, k, v)
    path = root / 'template.xml'; history = root / 'history'

    def call(mode='render'):
        ET.ElementTree(doc).write(path, encoding='utf-8', xml_declaration=True)
        return json.loads(command([args.dotnet, Path(args.host).resolve(), mode, path, args.ffmpeg, args.ffprobe, history, 'portable' if args.portable else 'native']))

    def capacity(expected):
        value = call('capacity')
        assert value['Exact'] and int(value['Remaining']) == expected, value

    def render(count):
        doc.find('Count').text = str(count)
        value = call()
        assert not value['Errors'] and len(value['Outputs']) == count, value
        return value['Outputs']

    def check_tags(file, brand, software=True):
        media = probe(file); tags = media['format']['tags']
        assert tags['make'] == brand and tags['model']
        native = 'com.apple.quicktime' if brand == 'Apple' else 'com.android'
        assert tags[native + ('.make' if brand == 'Apple' else '.manufacturer')] == brand
        assert tags[native + '.model'] == tags['model']
        if software:
            assert tags['software'] == 'Telegram — пользовательская метка'
        else:
            assert 'software' not in tags, tags
        local = datetime.fromisoformat(tags['creationdate'])
        assert datetime.now(timezone.utc) - timedelta(days=30, seconds=3) <= local <= datetime.now(timezone.utc)
        for stamp in tags['creation_time'].split(';'):
            assert abs((datetime.fromisoformat(stamp.replace('Z', '+00:00')) - local).total_seconds()) < 1
        if brand == 'Apple':
            assert tags['com.apple.quicktime.creationdate'] == tags['creationdate']
        assert tags['title'] == 'My Test Title' and tags['description'] == 'My Test Title'
        stream = next(s for s in media['streams'] if s['codec_type'] == 'video')
        assert (stream['width'], stream['height'], stream['codec_name']) == (180, 320, 'h264')
        assert abs(float(stream['duration']) - 1) < .1
        assert any(s['codec_type'] == 'audio' and s['codec_name'] == 'aac' for s in media['streams'])
        assert report(file, '.metadata.txt').exists()
        assert not list(Path(file).parent.glob('*.txt')) and not list(Path(file).parent.glob('*.xml'))
        return ET.parse(report(file, '.assembly.xml')).getroot().findtext('Signature')

    capacity(2)
    set_scope('RU')
    ru_first = render(1)[0]; ru_signature = check_tags(ru_first, 'Apple')
    set_scope('EN')
    capacity(2)  # EN ledger is independent from RU on the same materials.
    set_scope('RU')
    capacity(1)
    set_scope('Shared')
    first = render(1)[0]; signature = check_tags(first, 'Apple')
    set_scope('Shared')
    capacity(1)  # New process, same materials.
    second = render(1)[0]; second_signature = check_tags(second, 'Apple')
    assert signature != second_signature
    # Removing every output/report, copying material filenames and changing the studio ID
    # must leave the same permanent blocked combinations.
    shutil.rmtree(root / 'out')
    shutil.copyfile(root / 'videos/v.mp4', root / 'videos/copied.mp4')
    (root / 'images/0.png').rename(root / 'images/renamed.png')
    doc.find('Id').text = 'studio-b'; brands.find('string').text = 'Samsung'
    # Two images are two visual combinations; a second song or device tag
    # must not revive an already rendered combination.
    capacity(0)
    blocked = call(); assert not blocked['Outputs'] and blocked['Errors'], blocked
    png(root / 'images/new.png', (30, 30, 320, 120), (0, 0, 255, 255))
    capacity(1)
    rest = render(1)
    remaining_signatures = [check_tags(file, 'Samsung') for file in rest]
    assert len({signature, second_signature, *remaining_signatures}) == 3
    capacity(0)
    # Changing software alone must not expand the remaining visual space.
    metadata.find('Software').text = ''
    capacity(0)
    png(root / 'images/another.png', (30, 30, 320, 120), (255, 255, 0, 255))
    capacity(1)
    extra = render(1)
    for file in extra: check_tags(file, 'Samsung', software=False)
    capacity(0)
    ru_ledger = history / 'markets' / 'ru.xml'
    assert len(ET.parse(ru_ledger).getroot().findall('Records/AssemblyHistoryRecord')) == 1
    ledger = history / 'all-studios.xml'
    state = ET.parse(ledger).getroot()
    assert len(state.findall('Records/AssemblyHistoryRecord')) == 4
    assert len(set(n.text for n in state.findall('Combinations/string'))) == 4
    # Corruption must fail closed without overwriting or auto-loading a stale backup.
    ledger.write_text('corrupted history', encoding='utf-8')
    failure = call(); assert failure['Errors'] and not failure['Outputs'], failure
    assert ledger.read_text(encoding='utf-8') == 'corrupted history'
    print('PASS: five real MP4s, music/device-only changes excluded from visual combinations, restart/new-studio persistence, deleted outputs/reports, renamed/copied inputs, exhaustion/new materials, Apple/Android native tags, UTC and offset date, Unicode software, real codec/audio and clean output folder', flush=True)
