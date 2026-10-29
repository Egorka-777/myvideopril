VideoBatch — перенос на другой ПК
=================================

Что внутри архива
-----------------
  app\              — программа (exe, ffmpeg, uploader, telegram tools)
  user-data\        — настройки, студия, очередь, vault, telegram, live
  materials\        — материалы сборки (картинки, озвучки, сцены)
  videos\           — копия %USERPROFILE%\Videos\VideoBatch (если была)
  upload-staging\   — копия C:\VideoBatch\Upload (если была)
  Restore-VideoBatchPortable.ps1
  Start-VideoBatch.cmd

На старом ПК перед сборкой
--------------------------
  Закройте VideoBatch (чтобы settings.xml и очередь не писались во время копирования).

На новом ПК
-----------
  1. Распакуйте архив в любую папку, например E:\VideoBatch
  2. Дважды щёлкните Launch-VideoBatch-Portable.bat
     (копия с русским именем: Запуск VideoBatch.bat)
  3. Первый запуск 1–3 минуты — копирование настроек. Дальше снова только этот bat-файл.

Что скрипт восстановления делает
--------------------------------
  • Копирует user-data в %LOCALAPPDATA%\VideoBatchDesktop
  • Подменяет пути D:\VideoBatch\..., C:\Users\...\..., C:\VideoBatch\Upload
  • Пишет install-root.txt на папку app внутри комплекта
  • Переносит videos в вашу папку «Видео\VideoBatch»

Отдельно на новом ПК нужно
--------------------------
  • Dolphin Anty (те же Profile ID, что в настройках)
  • WebView2 Runtime (обычно уже есть в Windows)
  • Сеть / прокси как на старом ПК, если использовали

Безопасность
------------
  В архиве accounts.vault и settings.xml с токенами. Не выкладывайте архив в облако без шифрования.
