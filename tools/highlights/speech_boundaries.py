"""Refine cached speech timestamps using measured local non-speech intervals.

ASR spellings are not stable and word endpoints are not acoustic endpoints.
Cached times identify the selected words; local decoding independently locates
following speech so the last pause preserves final stop-consonant releases.
Music-only openings use unpadded voice activity rather than amplitude silence.
Missing pauses use interword timing, not fixed padding. Valid original clips
survive inconclusive refinement; audio/model infrastructure errors still propagate.
"""
import math
import re
import subprocess
import sys

import numpy as np

from whisper_runtime import create_model

RATE = 16000


class BoundaryError(ValueError):
    pass


class _Inconclusive(BoundaryError):
    """Insufficient alignment evidence, not an audio/model failure."""


def _timed_cut(left, right, original, edge):
    if left > right:
        print(f"Boundary refinement: overlapping {edge} word timing at {original:.3f}s; "
              "keeping original boundary", file=sys.stderr, flush=True)
        return original
    # Retain a cut already between words; otherwise use the measured interval.
    return original if left <= original <= right else (left + right) / 2


def _tokens(text):
    return re.findall(r"[^\W_]+", text.casefold().replace("ё", "е"), re.UNICODE)


def _audio(job, start, end):
    # Decode audio only; trim on the same timestamp origin as worker.transcribe.
    args = [str(job["ffmpeg"]), "-v", "error", "-nostdin", "-t", f"{end:.6f}", "-i", str(job["source"]),
            "-vn", "-map", "0:a:0", "-ac", "1", "-af",
            f"aresample=16000:async=1:first_pts=0,atrim=start={start:.6f}:end={end:.6f},asetpts=PTS-STARTPTS",
            "-ar", str(RATE), "-f", "f32le", "-"]
    result = subprocess.run(args, capture_output=True)
    if result.returncode:
        raise BoundaryError("Boundary audio decode failed: " + result.stderr.decode("utf-8", errors="replace"))
    audio = np.frombuffer(result.stdout, dtype="<f4")
    if not len(audio):
        raise BoundaryError("Boundary audio decode returned no samples")
    return audio


def _silences(audio, origin):
    # Sample-level amplitude silence, equivalent to silencedetect -35dB / 40ms.
    # Do not smooth/merge across short releases: that would erase a word's tail.
    quiet = np.abs(audio) < 10 ** (-35 / 20)
    edges = np.diff(np.r_[False, quiet, False].astype(np.int8))
    starts, ends = np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)
    return [(origin + a / RATE, origin + b / RATE) for a, b in zip(starts, ends)
            if b - a >= .04 * RATE]


def _recognize(model, audio, origin):
    segments, _ = model.transcribe(audio, word_timestamps=True, vad_filter=False,
                                   condition_on_previous_text=False, beam_size=5)
    result = []
    for segment in segments:
        if segment.avg_logprob < -1.2:
            raise _Inconclusive("Boundary recognition is unreliable")
        for word in segment.words or []:
            for token in _tokens(word.word):
                result.append(dict(text=token, start=origin + word.start, end=origin + word.end))
    return result


def _following(decoded, word):
    """Locate the following word by its cached time, not another ASR spelling."""
    center = (word['start'] + word['end']) / 2
    matches = [i for i, item in enumerate(decoded)
               if item['start'] < word['end'] and item['end'] > word['start']]
    if not matches:
        raise _Inconclusive(f"No locally aligned following word at {center:.3f}s")
    return min(matches, key=lambda i: abs((decoded[i]['start'] + decoded[i]['end']) / 2 - center))


def _speech_gaps(audio, origin):
    # Music is not amplitude silence. Silero is already bundled with Whisper;
    # its unpadded non-speech intervals can verify an opening during music.
    from faster_whisper.vad import get_speech_timestamps, VadOptions
    speech = get_speech_timestamps(audio, VadOptions(min_silence_duration_ms=40, speech_pad_ms=0))
    edges = [origin] + [origin + s[k] / RATE for s in speech for k in ('start', 'end')]
    edges.append(origin + len(audio) / RATE)
    return list(zip(edges[::2], edges[1::2]))


def _choose(decoded, index, gaps, original, edge, duration):
    word = decoded[index]
    if edge == "end":
        if index + 1 == len(decoded):
            if duration - word["end"] < .04:
                return duration
            trailing = [(a, b) for a, b in gaps if b >= duration - 1 / RATE and a >= word["end"] - .02]
            if not trailing:
                return _timed_cut(word['end'], duration, original, edge)
            a, b = trailing[-1]
            return original if a < original < b else (a + b) / 2
        left, right = word["end"], decoded[index+1]["start"]
    else:
        if index == 0:
            if word["start"] < .5:
                return 0.0
            return _timed_cut(0.0, word['start'], original, edge)
        left, right = decoded[index-1]["end"], word["start"]
    # Word alignment may touch or fall inside the actual inter-word silence.
    # Use measured pauses intersecting that interval, not their containment.
    # This is not padding: the actual cut lies within measured quiet audio.
    candidates = [(a, b) for a, b in gaps if a < right and b > left]
    if not candidates:
        return _timed_cut(left, right, original, edge)
    a, b = candidates[-1]
    # An already safe cut is retained, including short pauses before next speech.
    return original if a < original < b else (a + b) / 2


def _boundary(model, job, duration, words, index, original, edge):
    origin, stop = max(0.0, original - 10), min(duration, original + 10)
    audio = _audio(job, origin, stop)
    gaps = _silences(audio, origin)
    audio_end = origin + len(audio) / RATE
    endpoint = audio_end if stop == duration else duration
    if edge == 'start':
        # Cached VAD timestamps correctly preserve long music-only breaks that
        # local Whisper attaches to the first word (52.34..59.98 for a single и).
        # Do not move back to a distant amplitude dip inside that music.
        nearby = [(a, b) for a, b in gaps if b >= words[index]['start']]
        if not any(a < words[index]['start'] and
                   b > (words[index - 1]['end'] if index else 0) for a, b in nearby):
            voice_gaps = [(a, b) for a, b in _speech_gaps(audio, origin) if a < original < b]
            if voice_gaps:
                return original
        return _choose(words, index, nearby, original, edge, endpoint)
    if index + 1 == len(words):
        return _choose(words, index, gaps, original, edge, endpoint)
    # Re-decoding supplies an independent next-word onset, necessary to keep a
    # final stop release (125) that cached word endpoints place too early. The
    # cached following word's interval anchors it even when its spelling changes.
    try:
        decoded = _recognize(model, audio, origin)
        following = _following(decoded, words[index + 1])
        if following == 0:
            raise _Inconclusive('No preceding word in ending context')
    except _Inconclusive as error:
        print(f"Boundary refinement: {error}; using cached word timing", file=sys.stderr, flush=True)
        return _choose(words, index, gaps, original, edge, endpoint)
    left = min(words[index]['end'], decoded[following - 1]['end'])
    right = max(words[index + 1]['start'], decoded[following]['start'])
    candidates = [(a, b) for a, b in gaps if a < right and b > left]
    if not candidates:
        return _timed_cut(decoded[following - 1]['end'], decoded[following]['start'], original, edge)
    a, b = candidates[-1]
    if a < original < b:
        return original
    # A measured pause can extend into a softly spoken next word. Never move
    # a cut past the independently decoded onset just to reach its midpoint.
    b = min(b, decoded[following]['start'])
    if a >= b:
        return _timed_cut(words[index]['end'], words[index + 1]['start'], original, edge)
    return (a + b) / 2


def _validate(clips, job, duration):
    minimum = max(120.0, float(job.get("minimum", 120)))
    maximum = min(300.0, float(job.get("maximum", 300)))
    for clip in clips:
        a, b = float(clip["start"]), float(clip["end"])
        if not (math.isfinite(a) and math.isfinite(b) and 0 <= a < b <= duration
                and minimum <= b-a <= maximum):
            raise BoundaryError("Acoustic boundaries cannot preserve the requested 120..300s duration")
        # Independent YouTube edits may intentionally overlap in the source.


def refine_clips(job, info, words, clips):
    """Return copied clip dictionaries in original order, changing start/end only.

    job: source, ffmpeg, root, optional whisper/minimum/maximum. info: duration.
    words: original cached word dictionaries identifying the selected neighbours.
    Rejects invalid input clips and propagates decode/model failures. Optional
    changes that violate duration limits keep the original valid selection.
    The runtime selects CPU or GPU; only short ending contexts are recognized.
    Does not export or modify persistent data.
    """
    if not clips:
        return []
    duration = float(info["duration"])
    _validate(clips, job, duration)
    model = create_model(job.get("whisper", "small"), job["root"])
    result = []
    for clip in clips:
        selected = [i for i, w in enumerate(words) if w["start"] >= clip["start"] - .02
                    and w["end"] <= clip["end"] + .02]
        if not selected:
            print("Boundary refinement: no cached boundary words; keeping original clip",
                  file=sys.stderr, flush=True)
            result.append(dict(clip))
            continue
        refined = dict(clip)
        for edge, index in (("start", selected[0]), ("end", selected[-1])):
            refined[edge] = _boundary(model, job, duration, words, index, float(clip[edge]), edge)
        result.append(refined)
    try:
        _validate(result, job, duration)
    except BoundaryError as error:
        print(f"Boundary refinement: {error}; keeping original valid clips",
              file=sys.stderr, flush=True)
        result = [dict(clip) for clip in clips]
    for clip, refined in zip(clips, result):
        print(f"Acoustic boundaries: {clip['start']:.3f}..{clip['end']:.3f} -> "
              f"{refined['start']:.3f}..{refined['end']:.3f}", file=sys.stderr, flush=True)
    return result
