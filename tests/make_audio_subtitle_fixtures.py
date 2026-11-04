"""Isolated CI speech: public JFK test audio + synthetic Russian eSpeak speech."""
from pathlib import Path
import subprocess
import sys
import urllib.request

folder = Path(sys.argv[1]); folder.mkdir(parents=True, exist_ok=True)
urllib.request.urlretrieve("https://raw.githubusercontent.com/openai/whisper/main/tests/jfk.flac", folder / "jfk.flac")
subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(folder / "jfk.flac"), str(folder / "voice-en.mp3")], check=True)
subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(folder / "jfk.flac"), "-c:a", "aac", str(folder / "voice-en.m4a")], check=True)
subprocess.run(["espeak-ng", "-v", "ru", "-s", "140", "-w", str(folder / "voice-ru.wav"),
                "Привет! Сегодня мы проверяем распознавание русской речи. Субтитры должны сохранять слова и время. Спасибо за внимание."], check=True)
print("Public English and synthetic Russian audio fixtures ready; no paid API.")
