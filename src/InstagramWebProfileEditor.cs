using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VideoBatch {
    // Applies presets through the saved Instagram WebView2 session, not Android.
    internal static class InstagramWebProfileEditor {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly object Gate = new object();
        static bool Busy;

        public static Task ApplyAsync(string profileId, string profileName, string upstream, string expectedUsername,
            string displayName, string bio, string avatarPath, Action<string> log) {
            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(upstream))
                throw new InvalidOperationException("Не выбран профиль или прокси.");
            if (string.IsNullOrWhiteSpace(expectedUsername))
                throw new InvalidOperationException("Укажите @username Instagram для сверки.");
            if (string.IsNullOrWhiteSpace(displayName) || bio == null || string.IsNullOrWhiteSpace(avatarPath) || !File.Exists(avatarPath))
                throw new InvalidOperationException("Проверьте имя, описание и файл аватара.");
            lock (Gate) {
                if (Busy) throw new InvalidOperationException("Другой профиль Instagram уже оформляется.");
                Busy = true;
            }
            var completion = new TaskCompletionSource<bool>();
            var ct = CancellationToken.None;
            var thread = new Thread(() => {
                using (var dispatcher = new Control()) {
                    dispatcher.CreateControl();
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    dispatcher.BeginInvoke(new Action(async () => {
                        try {
                            await RunSta(profileId, profileName, upstream, expectedUsername, displayName, bio, avatarPath, log, ct);
                            completion.TrySetResult(true);
                        } catch (Exception error) {
                            completion.TrySetException(error);
                        } finally {
                            lock (Gate) Busy = false;
                            Application.ExitThread();
                        }
                    }));
                    Application.Run();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }

        static async Task RunSta(string profileId, string profileName, string upstream, string expectedUsername,
            string displayName, string bio, string avatarPath, Action<string> log, CancellationToken ct) {
            if (InstagramWebWindow.IsProfileOpen(profileId))
                throw new InvalidOperationException("Профиль уже открыт. Закройте окно Instagram и повторите.");
            using (var window = new InstagramWebWindow(profileId, profileName, upstream)) {
                var applied = await window.ApplyProfileBackground(expectedUsername, displayName, bio, avatarPath, ct, log);
                if (!applied) throw new InvalidOperationException("Instagram не подтвердил изменения профиля.");
            }
        }

        internal static async Task<bool> ApplyOnView(WebView2 view, string expectedUsername, string displayName, string bio, string avatarPath, CancellationToken ct, Action<string> log) {
            if (view == null || view.IsDisposed || view.CoreWebView2 == null) throw new InvalidOperationException("Окно Instagram не готово.");
            string scriptPath = Path.Combine(Path.GetDirectoryName(typeof(InstagramWebProfileEditor).Assembly.Location) ?? "",
                "tools", "uploader", "instagram-profile-edit.js");
            if (!File.Exists(scriptPath)) throw new InvalidOperationException("Не найден instagram-profile-edit.js.");
            log?.Invoke("Проверяю вход…");
            view.CoreWebView2.Navigate("https://www.instagram.com/");
            await WaitNavigation(view, ct, 45);
            var session = await BrowserSessionProbe.ReadAsync(view, "Instagram", ct);
            if (session.Authorized != true) {
                string directory = Path.Combine(Store.Root, "diagnostics");
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "instagram-preset-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
                File.WriteAllText(file + ".json", await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({url:location.origin+location.pathname,ready:document.readyState,text:(document.body?.innerText||'').slice(0,4000),links:[...document.querySelectorAll('a[href]')].slice(0,60).map(a=>({path:new URL(a.href,location.href).pathname,label:a.getAttribute('aria-label')||a.innerText,visible:!!a.getClientRects().length}))})"));
                using (var image = File.Create(file + ".png"))
                    await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                log?.Invoke("Диагностика входа: " + file + ".json / .png");
                throw new InvalidOperationException(session.Detail ?? "Instagram не вошёл в сохранённую сессию.");
            }
            string identity = (session.AccountName ?? "").Trim().TrimStart('@');
            if (!string.Equals(identity, expectedUsername.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Открыт другой аккаунт Instagram: @" + identity + ". Ожидался @" + expectedUsername + ".");
            log?.Invoke("Открываю редактирование профиля…");
            await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(File.ReadAllText(scriptPath, Encoding.UTF8));
            view.CoreWebView2.Navigate("https://www.instagram.com/accounts/edit/");
            await WaitNavigation(view, ct, 45);
            await EnsureHelper(view);
            var fill = await Call(view, "fill", new Dictionary<string, object> { { "name", displayName }, { "bio", bio } }, ct);
            if (!Convert.ToBoolean(fill["ok"])) ThrowJs(fill);
            log?.Invoke("Загружаю аватар…");
            await Call(view, "reveal_avatar", null, ct);
            await SetAvatarAsync(view.CoreWebView2, avatarPath);
            var submit = await Call(view, "submit", null, ct);
            if (!Convert.ToBoolean(submit["ok"])) ThrowJs(submit);
            await Task.Delay(2000, ct);
            var read = await Call(view, "read", null, ct);
            if (!Convert.ToBoolean(read["ok"])) ThrowJs(read);
            string savedName = Convert.ToString(read["name"]) ?? "";
            string savedBio = Convert.ToString(read["bio"]) ?? "";
            if (savedName.Trim() != displayName.Trim() || savedBio.Trim() != bio.Trim())
                throw new InvalidOperationException("После сохранения имя или описание не совпали с пресетом.");
            log?.Invoke("Имя, описание и аватар сохранены.");
            return true;
        }

        static void ThrowJs(Dictionary<string, object> result) {
            string code = Convert.ToString(result.ContainsKey("error") ? result["error"] : "edit_failed");
            if (code == "auth_required") throw new InvalidOperationException("Instagram просит повторный вход или код.");
            if (code == "edit_form_missing") throw new InvalidOperationException("Страница редактирования Instagram не распознана.");
            throw new InvalidOperationException("Не удалось изменить профиль Instagram (" + code + ").");
        }

        static async Task EnsureHelper(WebView2 view) {
            string path = Path.Combine(Path.GetDirectoryName(typeof(InstagramWebProfileEditor).Assembly.Location) ?? "",
                "tools", "uploader", "instagram-profile-edit.js");
            string probe = await view.CoreWebView2.ExecuteScriptAsync("typeof globalThis.videoBatchInstagramProfileEdit==='function'");
            if (probe != "true") await view.CoreWebView2.ExecuteScriptAsync(File.ReadAllText(path, Encoding.UTF8));
        }

        static async Task<Dictionary<string, object>> Call(WebView2 view, string action, object payload, CancellationToken ct) {
            string call = payload == null
                ? "globalThis.videoBatchInstagramProfileEdit(" + Json.Serialize(action) + ",null)"
                : "globalThis.videoBatchInstagramProfileEdit(" + Json.Serialize(action) + "," + Json.Serialize(payload) + ")";
            await view.CoreWebView2.ExecuteScriptAsync(call + ".then(r=>globalThis.videoBatchProfileResult=r)");
            var wait = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < wait) {
                ct.ThrowIfCancellationRequested();
                string raw = await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify(globalThis.videoBatchProfileResult||null)");
                string text = Json.Deserialize<string>(raw);
                if (text != null && text != "null")
                    return Json.Deserialize<Dictionary<string, object>>(text);
                await Task.Delay(120, ct);
            }
            throw new TimeoutException("Instagram не ответил на действие «" + action + "».");
        }

        static async Task WaitNavigation(WebView2 view, CancellationToken ct, int seconds) {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) {
                ct.ThrowIfCancellationRequested();
                string source = view.CoreWebView2?.Source ?? "";
                if (!string.IsNullOrEmpty(source) && view.CoreWebView2.IsDocumentPlayingAudio == false) {
                    string ready = await view.CoreWebView2.ExecuteScriptAsync("document.readyState");
                    if (ready == "\"complete\"" || ready == "\"interactive\"") return;
                }
                await Task.Delay(300, ct);
            }
            throw new TimeoutException("Страница Instagram не загрузилась вовремя.");
        }

        static async Task SetAvatarAsync(CoreWebView2 core, string absolutePath) {
            absolutePath = Path.GetFullPath(absolutePath);
            if (!File.Exists(absolutePath)) throw new FileNotFoundException("Файл аватара не найден.", absolutePath);
            await core.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
            var doc = Json.Deserialize<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":0,\"pierce\":true}"));
            int root = Convert.ToInt32(((Dictionary<string, object>)doc["root"])["nodeId"]);
            var found = Json.Deserialize<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("DOM.querySelector",
                "{\"nodeId\":" + root + ",\"selector\":\"input[type=\\\"file\\\"]\"}"));
            int nodeId = Convert.ToInt32(found["nodeId"]);
            if (nodeId <= 0) throw new InvalidOperationException("Поле загрузки аватара не найдено.");
            string payload = Json.Serialize(new Dictionary<string, object> { { "nodeId", nodeId }, { "files", new[] { absolutePath } } });
            await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", payload);
            await Task.Delay(800);
        }
    }
}