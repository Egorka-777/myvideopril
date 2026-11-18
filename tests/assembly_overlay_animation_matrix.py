"""Render every UI image entrance, both exits and all playback modes with real FFmpeg."""
import argparse
from contextlib import nullcontext
import json
from pathlib import Path
import tempfile

import assembly_animated_overlays as animated
from assembly_integration import png, run


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--ffmpeg', default='ffmpeg')
    parser.add_argument('--ffprobe', default='ffprobe')
    parser.add_argument('--evidence', type=Path)
    args = parser.parse_args()
    args.host = str(Path(args.host).resolve())
    with (nullcontext(args.evidence) if args.evidence else tempfile.TemporaryDirectory(prefix='overlay-animation-matrix-')) as temp:
        root = Path(temp).resolve()
        root.mkdir(parents=True, exist_ok=True)
        print('Evidence root:', root, flush=True)
        (root / 'videos').mkdir()
        (root / 'static').mkdir()
        png(root / 'static' / 'green.png', (24, 32, 80, 88), (0, 255, 0, 255))
        png(root / 'red.png', (70, 220, 290, 420), (255, 0, 0, 255))
        run([args.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i',
             'color=c=blue:s=360x640:r=30:d=3.2', '-c:v', 'libx264',
             '-threads', '1', '-pix_fmt', 'yuv420p', root / 'videos' / 'blue.mp4'])
        sources = animated.fixtures(args, root)
        for extension, source in [('png', root / 'red.png'), *sources.items()]:
            for effect in ['cut', 'fade', 'zoomsoft', 'zoomout', 'pull', 'swipe', 'random']:
                key = extension + '-' + effect
                frame = animated.render(args, root, key, source, effect=effect, exit_effect='fade')
                settled = frame(1.6)
                animated.red(animated.pixel(settled, 180, 320), (key, 'settled'))
                early = frame(.45)
                # Random chooses a non-cut entrance. Each must visibly develop,
                # preserve the background, and settle with continuing motion.
                if effect != 'cut':
                    assert early != settled, (key, 'entrance did not animate')
                    assert animated.red_energy(early) < animated.red_energy(settled) * .95, (key, 'entrance absent')
                if effect == 'fade':
                    mid = animated.pixel(frame(.7), 180, 320)
                    assert 70 < mid[0] < 190 and 70 < mid[2] < 190, (key, 'entrance fade', mid)
                mid = animated.pixel(frame(2.6), 180, 320)
                assert 65 < mid[0] < 190 and 65 < mid[2] < 190, (key, 'exit fade', mid)
                if extension != 'png':
                    animated.red(animated.pixel(frame(1.1), 260, 320), (key, 'right motion phase'))
                    animated.red(animated.pixel(frame(1.6), 100, 320), (key, 'left motion phase'))
                    animated.red(animated.pixel(frame(2.1), 260, 320), (key, 'motion continues'))
                print('PASS:', key, 'entrance, fade exit, delay/end, background and motion', flush=True)
            for playback in ['once', 'repeat', 'continuous']:
                key = extension + '-' + playback
                frame = animated.render(args, root, key, source, playback=playback, exit_effect='cut')
                animated.red(animated.pixel(frame(.6), 180, 320), (key, 'starts'))
                if playback in ['once', 'repeat']:
                    animated.blue(animated.pixel(frame(1.6), 180, 320), (key, 'ends/gap'))
                if playback == 'once':
                    animated.blue(animated.pixel(frame(2.1), 180, 320), (key, 'does not restart'))
                else:
                    animated.red(animated.pixel(frame(2.1), 180, 320), (key, 'continues/restarts'))
                print('PASS:', key, 'playback and cut exit', flush=True)
    print('PASS: 35 entrance/fade-exit cases + 15 playback/cut-exit cases')


if __name__ == '__main__':
    main()
