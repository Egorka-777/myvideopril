"""Local word timestamps for imported speech. Never modifies the source audio."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import tempfile

MODEL = "small"
VERSION = "2026-11-04-imported-audio-v1"


def emit(stage, message, percent=None):
    print(json.dumps(dict(stage=stage, message=message, percent=percent), ensure_ascii=False), flush=True)


def fingerprint(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                         prefix=".subtitles-", suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(value, stream, ensure_ascii=False, allow_nan=False)
        os.replace(temporary, path)
    finally:
        if temporary and temporary.exists():
            temporary.unlink()


def timing_from_segments(segments, duration, report=lambda value: None):
    """Keep ASR word boundaries; do not spread a transcript over an audio clock."""
    words, previous = [], 0.0
    for segment in segments:
        if segment.no_speech_prob <= .6 and segment.avg_logprob >= -1.2:
            for word in segment.words or []:
                text = word.word.strip()
                start, end = float(word.start), float(word.end)
                if not text or not math.isfinite(start) or not math.isfinite(end):
                    continue
                start, end = max(previous, 0.0, start), min(duration, end)
                if end <= start:
                    continue
                words.append((text, start, end))
                previous = end
        report(min(99, max(0, int(100 * segment.end / max(.01, duration)))))
    if not words:
        raise ValueError("Речь не найдена. Проверьте, что выбран файл озвучки, а не музыка или тишина.")
    return dict(Text=" ".join(w[0] for w in words), Alignment=dict(
        characters=[w[0] + " " for w in words],
        character_start_times_seconds=[w[1] for w in words],
        character_end_times_seconds=[w[2] for w in words]))


def execute(job, model_factory=None):
    if model_factory is None:
        from faster_whisper import WhisperModel
        model_factory = WhisperModel
    emit("loading", "Загружаю локальную модель речи…", 0)
    model = model_factory(MODEL, device="cpu", compute_type="int8", cpu_threads=2,
                          num_workers=1, download_root=job["models"], local_files_only=True)
    for index, item in enumerate(job["items"]):
        audio = Path(item["audio"])
        emit("speech", f"Распознаю {index + 1}/{len(job['items'])}: {audio.name}", int(index * 100 / len(job["items"])))
        if fingerprint(audio) != item["hash"]:
            raise ValueError("Аудиофайл изменился перед распознаванием: " + audio.name)
        # PyAV decodes MP3/WAV/M4A/FLAC/OGG directly. No external child process,
        # no audio upload and no language translation.
        segments, info = model.transcribe(str(audio), word_timestamps=True, vad_filter=True,
                                         condition_on_previous_text=False, beam_size=5, task="transcribe")
        duration = float(info.duration)
        if not math.isfinite(duration) or duration <= 0:
            raise ValueError("Не удалось прочитать длительность аудио: " + audio.name)
        result = timing_from_segments(segments, duration, lambda value: emit(
            "speech", f"Распознаю {index + 1}/{len(job['items'])}: {audio.name}",
            int((index + value / 100) * 100 / len(job["items"]))))
        if fingerprint(audio) != item["hash"]:
            raise ValueError("Аудиофайл изменился во время распознавания: " + audio.name)
        result.update(AudioHash=item["hash"], Source="faster-whisper/" + MODEL,
                      Language=info.language, Version=VERSION)
        atomic_json(item["output"], result)
        emit("cached", f"Субтитры сохранены: {audio.name}", int((index + 1) * 100 / len(job["items"])))
    emit("done", "Распознавание завершено", 100)


def prepare(root, models):
    from faster_whisper.utils import download_model
    from huggingface_hub.errors import LocalEntryNotFoundError
    try:
        model_path = download_model(MODEL, cache_dir=str(models), local_files_only=True)
        if not all((Path(model_path) / name).is_file() for name in ("model.bin", "config.json", "tokenizer.json")):
            raise FileNotFoundError("Incomplete local speech model")
        emit("loading", "Использую уже скачанную модель речи.")
    except (LocalEntryNotFoundError, FileNotFoundError):
        emit("download", "Первый запуск: скачиваю модель речи small. Аудиофайлы никуда не отправляются.")
        model_path = download_model(MODEL, cache_dir=str(models))
    # Validate the native runtime before recording a ready installation.
    from faster_whisper import WhisperModel
    WhisperModel(MODEL, device="cpu", compute_type="int8", cpu_threads=2,
                 download_root=str(models), local_files_only=True)
    atomic_json(Path(root) / "runtime.json", dict(python=sys.executable, models=str(models),
                modelFile=str(Path(model_path) / "model.bin"), version=VERSION))
    emit("ready", "Распознавание речи готово. Следующие запуски работают локально.", 100)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--job")
    parser.add_argument("--prepare")
    parser.add_argument("--models")
    args = parser.parse_args()
    try:
        if args.prepare:
            prepare(args.prepare, args.models)
        elif args.job:
            execute(json.loads(Path(args.job).read_text(encoding="utf-8-sig")))
        else:
            parser.error("--job or --prepare is required")
    except Exception as error:
        emit("error", str(error))
        print(str(error), file=sys.stderr, flush=True)
        return 1
    return 0


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    sys.exit(main())
