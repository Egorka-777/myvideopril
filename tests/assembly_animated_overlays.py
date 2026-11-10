"""Real AssemblyHost renders for animated image layers; no mocked decoders.

Run against a freshly built tests/AssemblyHost DLL, with native rasterization.
Optional --asset PATH (repeatable) adds smoke renders of user-provided media;
synthetic fixtures carry the deterministic alpha/motion/loop assertions.
"""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

from assembly_integration import layer, png, run, value


WIDTH, HEIGHT, FPS = 360, 640, 30
START, END, DURATION = .4, 2.8, 3.2


def set_value(node, name, text):
    child = node.find(name)
    if child is None:
        value(node, name, text)
    else:
        child.text = str(text)


def decode(args, file, at, alpha=False):
    decoder = ['-c:v', 'libvpx-vp9'] if alpha and Path(file).suffix == '.webm' else []
    data = subprocess.check_output(
        [str(x) for x in [args.ffmpeg, '-v', 'error', '-threads', '1', *decoder,
                         '-i', file, '-ss', at, '-frames:v', '1', '-an',
                         '-pix_fmt', 'rgba' if alpha else 'rgb24', '-threads', '1',
                         '-f', 'rawvideo', '-']], timeout=60)
    channels = 4 if alpha else 3
    assert len(data) == WIDTH * HEIGHT * channels, (file, at, len(data))
    return data


def pixel(frame, x, y, channels=3):
    offset = (y * WIDTH + x) * channels
    return tuple(frame[offset:offset + channels])


def blue(rgb, label):
    assert rgb[2] > 190 and rgb[0] < 45 and rgb[1] < 45, (label, rgb)


def red(rgb, label, opacity=1):
    if opacity == 1:
        assert rgb[0] > 195 and rgb[1] < 45 and rgb[2] < 55, (label, rgb)
    else:
        assert 85 < rgb[0] < 175 and rgb[1] < 45 and 85 < rgb[2] < 175, (label, rgb)


def fixtures(args, root):
    frames = root / 'frames'
    frames.mkdir()
    # Ten frames, exactly one second. The rectangle jumps horizontally halfway
    # through; the overlap at x=180 remains red for stable entrance assertions.
    for index in range(10):
        left = 70 if index < 5 else 150
        png(frames / f'{index:02}.png', (left, 220, left + 140, 420), (255, 0, 0, 255))
    sources = {}
    for extension, codec in [
            ('webm', ['-c:v', 'libvpx-vp9', '-lossless', '1', '-pix_fmt', 'yuva420p', '-auto-alt-ref', '0']),
            ('mov', ['-c:v', 'qtrle', '-pix_fmt', 'argb']),
            ('gif', ['-filter_complex', '[0:v]split[a][b];[a]palettegen=reserve_transparent=1[p];'
                     '[b][p]paletteuse=alpha_threshold=128', '-loop', '0', '-gifflags', '-offsetting']),
            ('mp4', ['-vf', 'format=rgb24,format=yuv420p', '-c:v', 'libx264', '-crf', '18'])]:
        folder = root / extension
        folder.mkdir()
        source = folder / ('moving.' + extension)
        run([args.ffmpeg, '-v', 'error', '-y', '-filter_complex_threads', '1',
             '-framerate', '10', '-i', frames / '%02d.png', '-frames:v', '10',
             *codec, '-threads', '1', source])
        info = json.loads(run([args.ffprobe, '-v', 'error', '-show_streams', '-show_format', '-of', 'json', source]))
        stream = next(s for s in info['streams'] if s['codec_type'] == 'video')
        duration = float(stream.get('duration') or info['format']['duration'])
        assert abs(duration - 1) < .06, (extension, duration)
        expected_codec = dict(webm='vp9', mov='qtrle', gif='gif', mp4='h264')[extension]
        assert stream['codec_name'] == expected_codec, (extension, stream)
        first = decode(args, source, .2, alpha=True)
        second = decode(args, source, .7, alpha=True)
        assert pixel(first, 100, 320, 4)[0] > 220
        assert pixel(second, 260, 320, 4)[0] > 220
        if extension != 'mp4':
            assert pixel(first, 10, 10, 4)[3] < 10, (extension, 'fixture lost transparency')
            assert pixel(first, 100, 320, 4)[3] > 245
        else:
            assert pixel(first, 10, 10, 4)[3] == 255
        sources[extension] = source
    return sources


def render(args, root, key, source, global_layer=False, effect='cut', transformed=False, scene_count=1,
           green_screen=None, playback='continuous', pause=.5, exit_effect='cut', photo_duration=1):
    doc = ET.Element('AssemblyTemplate')
    for name, text in dict(Id=key, Materials=root, Videos='videos', Output=root / key,
                           Width=WIDTH, Height=HEIGHT, Fps=FPS, Count=1, Transition='cut',
                           RenderMode='cpu', RandomVideoStart='false', AllowVideoReuse='true',
                           LoopShortVideos='false', SceneTimingVersion=1,
                           PackStudioVersion=1, ObjectStudioVersion=1).items():
        value(doc, name, text)
    scenes = ET.SubElement(doc, 'Scenes')
    target = None
    for index in range(scene_count):
        scene = ET.SubElement(scenes, 'AssemblyScene')
        value(scene, 'Name', f'Scene {index + 1}')
        value(scene, 'Duration', DURATION / scene_count)
        layers = ET.SubElement(scene, 'Layers')
        if index == 0:
            target = layers
    if global_layer:
        target = ET.SubElement(doc, 'GlobalLayers')
    layer(target, 'Moving overlay', source, start=START, end=END)
    animated = target[-1]
    for name, text in dict(PlacementConfigured='true', AppearanceTransition=effect,
                           AppearanceDuration=.6, PlaybackMode=playback, RepeatPause=pause,
                           ExitTransition=exit_effect, ExitDuration=.4, PhotoDuration=photo_duration,
                           Opacity=.5 if transformed else 1,
                           X=.25 if transformed else 0, Y=.25 if transformed else 0,
                           Width=.5 if transformed else 1, Height=.5 if transformed else 1).items():
        set_value(animated, name, text)
    if green_screen is not None:
        value(animated, 'GreenScreen', str(green_screen).lower())
    # A PNG is composited together with every moving layer, not a separate mock
    # or a separate engine. It must remain unchanged before/during/after motion.
    static = ET.SubElement(doc, 'GlobalLayers') if doc.find('GlobalLayers') is None else doc.find('GlobalLayers')
    layer(static, 'Legacy PNG', root / 'static' / 'green.png')
    value(static[-1], 'PlacementConfigured', 'true')
    path = root / (key + '.xml')
    ET.ElementTree(doc).write(path, encoding='utf-8', xml_declaration=True)
    result = json.loads(run([args.dotnet, args.host, 'render', path, args.ffmpeg,
                             args.ffprobe, root / ('history-' + key), 'native']))
    assert not result['Errors'] and len(result['Outputs']) == 1 and not result.get('Cancelled'), (key, result)
    output = Path(result['Outputs'][0])
    info = json.loads(run([args.ffprobe, '-v', 'error', '-show_streams', '-of', 'json', output]))
    video = next(s for s in info['streams'] if s['codec_type'] == 'video')
    assert (video['width'], video['height']) == (WIDTH, HEIGHT), (key, video)
    assert abs(float(video['duration']) - DURATION) < .12, (key, video)
    report = output.parent / '.videobatch-reports' / (output.name + '.assembly.xml')
    recipe = ET.parse(report).getroot()
    assert len(recipe.findall('Scenes/AssemblyScenePlan')) == scene_count
    if green_screen is not None:
        # Host loads the saved XML into the production model; its serialized
        # recipe proves the bool survived that load rather than being ignored.
        saved = [node for node in recipe.iter('AssemblyLayer')
                 if node.findtext('Name') == 'Moving overlay']
        # Layer plans serialize their model under <Layer>, not AssemblyLayer.
        saved += [node for node in recipe.iter('Layer')
                  if node.findtext('Name') == 'Moving overlay']
        assert saved and all(node.findtext('GreenScreen') == str(green_screen).lower()
                             for node in saved), (key, 'GreenScreen XML roundtrip', saved)
    cache = {}

    def frame(at):
        if at not in cache:
            cache[at] = decode(args, output, at)
        return cache[at]

    for at in [.2, 1.1, 3.0]:
        green = pixel(frame(at), 50, 60)
        assert green[1] > 190 and green[0] < 45 and green[2] < 45, (key, 'legacy PNG', at, green)
        if transformed or Path(source).suffix.lower() != '.mp4' or at in [.2, 3.0]:
            blue(pixel(frame(at), 340, 600), (key, 'outside layer', at))
    blue(pixel(frame(.2), 180, 320), (key, 'before delayed start'))
    blue(pixel(frame(3.0), 180, 320), (key, 'after explicit end'))
    return frame


def verify_motion(frame, key, transparent=True, transformed=False, entrance=False):
    # These pairs have identical source phases one and two loops later. Sampling
    # inside each half avoids boundaries, GIF centisecond rounding and GOP seeks.
    left, right = (140, 220) if transformed else (100, 260)
    opacity = .5 if transformed else 1
    for at in ([1.6, 2.6] if entrance else [.6, 1.6, 2.6]):
        red(pixel(frame(at), left, 320), (key, 'left phase', at), opacity)
        other = pixel(frame(at), right, 320)
        if transparent:
            blue(other, (key, 'vacated right restores background', at))
        else:
            assert max(other) < 45, (key, 'opaque MP4 background', at, other)
    for at in [1.1, 2.1]:
        red(pixel(frame(at), right, 320), (key, 'right phase', at), opacity)
        other = pixel(frame(at), left, 320)
        if transparent:
            blue(other, (key, 'vacated left restores background', at))
        else:
            assert max(other) < 45, (key, 'opaque MP4 background', at, other)
    corner = (100, 170) if transformed else (10, 10)
    rgb = pixel(frame(1.6), *corner)
    if transparent:
        blue(rgb, (key, 'transparent media corner'))
    else:
        assert max(rgb) < 45, (key, 'MP4 must remain opaque', rgb)
    if transformed:
        blue(pixel(frame(1.6), 75, 320), (key, 'scaled content must not cover original position'))
        blue(pixel(frame(1.6), 140, 240), (key, 'scaled content height/vertical placement'))


def green_fixture(args, root):
    folder = root / 'green-screen'
    folder.mkdir()
    source = folder / 'moving-green.mp4'
    run([args.ffmpeg, '-v', 'error', '-y', '-filter_complex_threads', '1',
         '-f', 'lavfi', '-i', f'color=c=0x00ff00:s={WIDTH}x{HEIGHT}:r=10:d=1',
         '-framerate', '10', '-i', root / 'frames' / '%02d.png',
         '-filter_complex', '[0:v][1:v]overlay=shortest=1:format=auto,format=yuv420p',
         '-frames:v', '10', '-c:v', 'libx264', '-crf', '18', '-threads', '1', source])
    first, second = decode(args, source, .2), decode(args, source, .7)
    corner = pixel(first, 10, 10)
    assert corner[1] > 220 and corner[0] < 30 and corner[2] < 30, ('green fixture', corner)
    red(pixel(first, 100, 320), 'green fixture left')
    red(pixel(second, 260, 320), 'green fixture right')
    return source


def verify_green_screen(frame, enabled):
    for at, active, vacant in [(.6, 140, 220), (1.1, 220, 140),
                               (1.6, 140, 220), (2.1, 220, 140), (2.6, 140, 220)]:
        red(pixel(frame(at), active, 320), ('GreenScreen', enabled, 'moving red', at), .5)
        for x, y in [(vacant, 320), (100, 170)]:
            color = pixel(frame(at), x, y)
            if enabled:
                blue(color, ('GreenScreen enabled restores blue', at, x, y))
            else:
                assert color[0] < 45 and 85 < color[1] < 175 and 85 < color[2] < 175, (
                    'GreenScreen disabled preserves half-opacity green over blue', at, color)
        blue(pixel(frame(at), 75, 320), ('GreenScreen keeps horizontal position/scale', enabled, at))
        blue(pixel(frame(at), 140, 140), ('GreenScreen keeps vertical position/scale', enabled, at))


def red_energy(frame):
    # Green static badge has no red energy; blue background has none either.
    return sum(max(frame[i] - frame[i + 2], 0) for i in range(0, len(frame), 3))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, help='Freshly built AssemblyHost.dll')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--ffmpeg', default='ffmpeg')
    parser.add_argument('--ffprobe', default='ffprobe')
    parser.add_argument('--asset', type=Path, action='append', default=[], help='Optional real-media smoke render (repeatable)')
    args = parser.parse_args()
    args.host = str(Path(args.host).resolve())
    with tempfile.TemporaryDirectory(prefix='animated-overlays-') as temp:
        root = Path(temp)
        (root / 'videos').mkdir()
        (root / 'static').mkdir()
        png(root / 'static' / 'green.png', (24, 32, 80, 88), (0, 255, 0, 255))
        run([args.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i',
             f'color=c=blue:s={WIDTH}x{HEIGHT}:r={FPS}:d={DURATION}',
             '-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv420p', root / 'videos' / 'blue.mp4'])
        sources = fixtures(args, root)
        for extension, source in sources.items():
            frame = render(args, root, extension + '-scene', source)
            verify_motion(frame, extension, transparent=extension != 'mp4')
            print('PASS:', extension, 'scene layer: moving frames, two loops, delay/end, alpha/opacity, static PNG', flush=True)
        for extension in ['webm', 'mov', 'gif', 'mp4']:
            frame = render(args, root, extension + '-once', sources[extension], playback='once')
            red(pixel(frame(.6), 100, 320), (extension, 'once starts'))
            for at in [1.5, 2.1, 2.6]:
                blue(pixel(frame(at), 180, 320), (extension, 'once disappears', at))
            print('PASS:', extension, 'one playback disappears without frozen last frame', flush=True)
        frame = render(args, root, 'repeat-pause', sources['webm'], playback='repeat', exit_effect='fade')
        blue(pixel(frame(1.6), 180, 320), 'repeat gap after end')
        red(pixel(frame(2.1), 100, 320), 'repeat restarts from first phase after gap')
        fading = pixel(frame(1.2), 180, 320)
        assert 65 < fading[0] < 185 and 65 < fading[2] < 185, ('fade exit', fading)
        print('PASS: repeat end-relative pause, restart and fade exit', flush=True)
        frame = render(args, root, 'photo-once-exit', root / 'frames' / '00.png', playback='once', exit_effect='fade')
        blue(pixel(frame(1.6), 180, 320), 'photo show duration ends')
        fading = pixel(frame(1.2), 180, 320)
        assert 65 < fading[0] < 185 and 65 < fading[2] < 185, ('photo fade exit', fading)
        print('PASS: photo duration and fade exit', flush=True)
        # One global animated layer crosses a real scene boundary at t=1.6;
        # it must retain its roll-relative phase instead of restarting there.
        frame = render(args, root, 'webm-global-two-scenes', sources['webm'], global_layer=True, scene_count=2)
        verify_motion(frame, 'global')
        print('PASS: global WebM across two scenes, continuous animation phase', flush=True)
        frame = render(args, root, 'mov-transformed', sources['mov'], transformed=True)
        verify_motion(frame, 'scale-position-opacity', transformed=True)
        print('PASS: half scale, position and 50% opacity', flush=True)
        for effect in ['fade', 'swipe', 'zoomsoft']:
            frame = render(args, root, 'webm-' + effect, sources['webm'], effect=effect, global_layer=effect == 'swipe')
            verify_motion(frame, effect, entrance=True)
            early, settled = red_energy(frame(.45)), red_energy(frame(1.6))
            assert early < settled * .95, (effect, 'entrance absent', early, settled)
            if effect == 'fade':
                # At half fade real RGB is (125, 0, 126): max(R-B, 0)
                # deliberately remains zero until alpha exceeds 0.5. Assert
                # the actual red/blue blend instead of misclassifying it as stalled.
                middle = pixel(frame(.7), 180, 320)
                assert 70 < middle[0] < 190 and 70 < middle[2] < 190 and middle[1] < 45, (effect, middle)
            else:
                # Zoomsoft also blends alpha: at .7 its real center is
                # (125, 0, 128), so measure red energy after the midpoint but
                # still before the entrance ends at t=1.0.
                progress_at = .85 if effect == 'zoomsoft' else .7
                progress = red_energy(frame(progress_at))
                assert settled * .02 < progress < settled * .95, (
                    effect, 'entrance progress', progress_at, progress, settled)
            print('PASS:', effect, 'entrance preserves alpha/background and continuing motion/loops', flush=True)
        green_source = green_fixture(args, root)
        for enabled in [False, True]:
            frame = render(args, root, 'green-screen-' + str(enabled).lower(), green_source,
                           transformed=True, green_screen=enabled)
            verify_green_screen(frame, enabled)
            print('PASS: GreenScreen', enabled, 'XML roundtrip, moving red, background, loops, opacity, placement, timing', flush=True)
        # Turning keying on must not turn existing transparent source pixels
        # opaque. Run the same moving-alpha oracle with the toggle enabled.
        frame = render(args, root, 'green-screen-preserve-webm-alpha', sources['webm'],
                       transformed=True, green_screen=True)
        verify_motion(frame, 'GreenScreen preserves source alpha', transformed=True)
        print('PASS: GreenScreen retains original WebM alpha', flush=True)
        for index, asset in enumerate(args.asset):
            assert asset.is_file(), ('Missing optional asset', asset)
            frame = render(args, root, f'user-asset-{index}', asset.resolve(), transformed=True)
            # Unknown artwork has no deterministic color/alpha/motion oracle.
            # Force decoding during its active interval as well as the timing,
            # PNG and outside-box assertions performed by render().
            for at in [.6, 1.1, 1.6, 2.1, 2.6]:
                frame(at)
            print('PASS: optional native render/decode smoke only:', asset, flush=True)
    print('PASS: all synthetic animated-overlay render assertions')


if __name__ == '__main__':
    main()
