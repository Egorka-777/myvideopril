using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    // Applies only inside the already foreground official app on the bound emulator.
    // AccountId is correlation data, NEVER evidence of platform identity. Neither
    // TikTokAccount.Name nor YouTubeChannel.Name is a verified platform username.
    // This helper does not launch profiles, change usernames or mark presets used.
    internal static class AndroidProfileEditor {
        static readonly object gate = new object();
        static bool applying;
        static readonly Dictionary<string, string> errors = new Dictionary<string, string> {
            { "identity_required", "Нужен явно заданный username TikTok/Instagram. Название строки и Dolphin ID не подтверждают аккаунт." },
            { "identity_mismatch", "Открыт другой аккаунт. Изменения остановлены." },
            { "identity_unverified", "Не удалось подтвердить username открытого аккаунта. Изменения остановлены." },
            { "auth_required", "Приложение требует вход, пароль или код. Выполните это вручную; изменения остановлены." },
            { "wrong_app", "Откройте нужный аккаунт в официальном Android-приложении на emulator-5554." },
            { "unsupported_screen", "Экран Android не поддерживается или неоднозначен. Изменения остановлены." },
            { "unsupported_instagram", "Редактирование Instagram этим Android-адаптером пока не поддерживается. Изменений нет." },
            { "avatar_unverifiable", "Этот адаптер не умеет проверять сохранённый аватар. Пресет с аватаром не применён; изменений нет." },
            { "clipboard_unavailable", "Помощник UTF-8 буфера Android недоступен или не подтвердил текст. Сохранение этого поля не выполнялось." },
            { "verification_failed", "Не все сохранённые поля подтверждены. Проверьте приложение вручную; пресет нельзя отмечать использованным." },
            { "adb_failed", "Ошибка ADB. Результат не подтверждён; проверьте приложение вручную перед повтором." },
            { "busy", "Другое изменение Android-профиля ещё выполняется." },
            { "invalid_request", "Некорректные параметры изменения Android-профиля." },
            { "internal_error", "Ошибка изменения Android-профиля. Результат не подтверждён; проверьте приложение вручную." }
        };

        public static async Task ApplyAsync(string network, string accountId, string expectedUsername,
            string displayName, string bio, string avatarPath, Action<string> log) {
            string platform = (network ?? "").Trim().ToLowerInvariant();
            string expected = (expectedUsername ?? "").Trim();
            if (expected.StartsWith("@", StringComparison.Ordinal)) expected = expected.Substring(1);
            if (!Regex.IsMatch(expected, @"\A[A-Za-z0-9_.]{1,30}\z"))
                throw new InvalidOperationException(errors["identity_required"]);
            if ((platform != "tiktok" && platform != "instagram") || string.IsNullOrWhiteSpace(accountId)
                || string.IsNullOrWhiteSpace(displayName) || bio == null || avatarPath == null)
                throw new InvalidOperationException(errors["invalid_request"]);
            lock (gate) {
                if (applying) throw new InvalidOperationException(errors["busy"]);
                applying = true;
            }
            try {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string python = Path.Combine(local, "Programs", "Python", "Python311", "python.exe");
                string worker = Path.Combine(Path.GetDirectoryName(typeof(AndroidProfileEditor).Assembly.Location), "tools", "android", "profile_edit.py");
                if (!File.Exists(python)) throw new InvalidOperationException("Не найден установленный Python 3.11.");
                if (!File.Exists(worker)) throw new InvalidOperationException("Не найден tools/android/profile_edit.py рядом с приложением.");
                var start = new ProcessStartInfo(python, "\"" + worker + "\"") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(worker)
                };
                start.EnvironmentVariables["PYTHONUTF8"] = "1";
                using (var process = new Process { StartInfo = start }) {
                    process.Start();
                    var stderr = DrainErrors(process.StandardError);
                    var json = new JavaScriptSerializer();
                    var job = new {
                        network = platform, account_id = accountId, expected_username = expected,
                        display_name = displayName, bio = bio, avatar_path = avatarPath,
                        sdk = Path.Combine(local, "PsyDroid", "sdk")
                    };
                    using (var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))) {
                        await input.WriteLineAsync(json.Serialize(job));
                    }
                    bool applied = false, invalid = false, failed = false, terminal = false;
                    string failure = null, line;
                    while ((line = await process.StandardOutput.ReadLineAsync()) != null) {
                        Dictionary<string, object> ev;
                        try { ev = json.Deserialize<Dictionary<string, object>>(line); }
                        catch (ArgumentException) { invalid = true; continue; }
                        object kind, code;
                        if (ev == null || terminal || !ev.TryGetValue("event", out kind) || !(kind is string)
                            || !ev.TryGetValue("code", out code) || !(code is string)) { invalid = true; continue; }
                        // Ignore text entirely: fixed local messages prevent adb output,
                        // profile values or unexpected worker content leaking into logs.
                        if ((string)kind == "status" && (string)code == "identity_verified") {
                            if (log != null) log("Username открытого Android-аккаунта проверен. Проверяю поля профиля…");
                        } else if ((string)kind == "error") {
                            failed = true; terminal = true;
                            if (!errors.TryGetValue((string)code, out failure)) invalid = true;
                            object mayHaveChanges;
                            if (ev.TryGetValue("may_have_changes", out mayHaveChanges) && mayHaveChanges is bool && (bool)mayHaveChanges)
                                failure = (failure ?? errors["internal_error"]) + " Возможны частичные изменения. Пресет не считать использованным.";
                        } else if ((string)kind == "applied" && (string)code == "verified") {
                            terminal = true;
                            object value;
                            var verified = ev.TryGetValue("verified", out value) ? value as Dictionary<string, object> : null;
                            object name, biography, avatar;
                            // Current worker refuses any requested avatar before mutation.
                            applied = avatarPath.Length == 0 && verified != null
                                && verified.TryGetValue("display_name", out name) && name is bool && (bool)name
                                && verified.TryGetValue("bio", out biography) && biography is bool && (bool)biography
                                && verified.TryGetValue("avatar", out avatar) && avatar is string && (string)avatar == "not_requested";
                            if (!applied) invalid = true;
                        } else invalid = true;
                    }
                    await Task.Run(() => process.WaitForExit());
                    await stderr;
                    if (process.ExitCode != 0 || invalid || failed || !applied) {
                        string message = failure ?? "Android не подтвердил все запрошенные изменения. Проверьте приложение вручную; пресет не считать использованным.";
                        if (log != null) log(message);
                        throw new InvalidOperationException(message);
                    }
                }
                if (log != null) log("Имя и описание повторно прочитаны после сохранения. Аватар не запрашивался и не изменялся.");
            } finally {
                lock (gate) applying = false;
            }
        }

        static async Task DrainErrors(StreamReader reader) {
            var buffer = new char[2048];
        static string ResolveDeviceSerial(string accountId) {
            string id = (accountId ?? "").Trim();
            if (!Regex.IsMatch(id, @"^\d+$")) throw new InvalidOperationException(errors["invalid_request"]);
            string bindingsPath = Path.Combine(Path.GetDirectoryName(typeof(AndroidProfileEditor).Assembly.Location),
                "tools", "android", "bindings.json");
            if (!File.Exists(bindingsPath)) throw new InvalidOperationException("Не найден tools/android/bindings.json.");
            var bindings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(bindingsPath));
            if (bindings == null || !bindings.TryGetValue(id, out object entry) || !(entry is Dictionary<string, object> binding))
                throw new InvalidOperationException("Для этого Dolphin ID нет привязанного Android-устройства в bindings.json.");
            string serial = binding.ContainsKey("serial") ? Convert.ToString(binding["serial"]) : null;
            if (string.IsNullOrWhiteSpace(serial) || !Regex.IsMatch(serial, @"^emulator-\d+$"))
                throw new InvalidOperationException("В bindings.json для профиля указан некорректный serial эмулятора.");
            return serial;
        }

            while (await reader.ReadAsync(buffer, 0, buffer.Length) != 0) { }
        }
    }
}
