using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class ProfilePresetTests {
        public static bool RunSelfTests() {
            string root = Path.Combine(Path.GetTempPath(), "profile-presets-test-" + Guid.NewGuid().ToString("N"));
            try {
                string seed = Path.Combine(root, "seed"); Directory.CreateDirectory(Path.Combine(seed, "avatars"));
                string avatar = Path.Combine(seed, "avatars", "test.png");
                using (var bitmap = new Bitmap(24, 24)) { bitmap.SetPixel(0, 0, Color.Red); bitmap.Save(avatar, ImageFormat.Png); }
                var seeds = Enumerable.Range(1, 50).Select(i => new ProfilePreset { Id = "seed-" + i, Name = "Имя " + i, Bio = "Описание " + i, AvatarPath = "avatars/test.png" }).ToList();
                File.WriteAllText(Path.Combine(seed, "presets.json"), new JavaScriptSerializer().Serialize(seeds));
                string appRoot = Path.Combine(root, "app");
                var store = new ProfilePresetStore(appRoot, seed);
                Check(store.Load().Count == 50, "50 seed presets");
                Check(store.ListAvatars().Count == 1, "Content deduplicated avatars");
                var first = store.Load()[0];
                Check(Path.IsPathRooted(first.AvatarPath) && File.Exists(first.AvatarPath), "Portable seed avatar copied");
                Check(!File.ReadAllText(store.DataPath).Contains(appRoot.Replace("\\", "\\\\")), "Persisted avatar is relative");
                Check(!store.GetUsed("TikTok", first.Id), "Unused initially");
                store.MarkUsed(" TikTok ", first.Id, "account-a");
                Check(store.GetUsed("TIKTOK", first.Id), "Network normalization");
                Check(!store.GetUsed("Instagram", first.Id) && !store.GetUsed("Telegram", first.Id), "Independent network usage");
                first.Name = "Изменено"; store.Save(first);
                Check(store.Load().Count == 50 && store.GetUsed("TikTok", first.Id), "Editing retains usage");
                store.MarkUsed("TikTok", first.Id, "account-a");
                ExpectFailure(() => store.MarkUsed("TikTok", first.Id, "account-b"), "Cannot consume twice");
                store.MarkUsed("Instagram", first.Id, "account-b"); store.MarkUsed("Telegram", first.Id, "account-c");
                store.Delete(first.Id);
                store = new ProfilePresetStore(appRoot, seed);
                Check(store.Load().Count == 49 && store.GetUsed("TikTok", first.Id), "Delete and reload retain usage");
                Check(store.GetUsed("Instagram", first.Id) && store.GetUsed("Telegram", first.Id), "All receipts retained");
                ExpectFailure(() => store.Save(first), "Deleted identity cannot be resurrected");
                var custom = new ProfilePreset { Name = "Новый", Bio = "Описание", AvatarPath = avatar }; store.Save(custom);
                Check(store.Load().Count == 50, "Create preset");
                custom.Bio = ""; ExpectFailure(() => store.Save(custom), "Require bio"); custom.Bio = "Описание";
                custom.AvatarPath = Path.Combine(root, "missing.png"); ExpectFailure(() => store.Save(custom), "Require existing avatar");
                custom.AvatarPath = avatar;
                TestApply(store, custom).GetAwaiter().GetResult();
                // Existing libraries do not depend on the bundled seed directory after initialization.
                Directory.Delete(seed, true);
                Check(new ProfilePresetStore(appRoot, seed).Load().Count == 50, "No seed reread/reset");
                File.WriteAllText(store.DataPath, "{broken");
                ExpectFailure(() => store.Load(), "Corrupt library surfaces");
                Check(File.ReadAllText(store.DataPath) == "{broken", "Corrupt data not overwritten");
                Console.WriteLine("PASS profile presets: 50 seeds, portable avatars, CRUD, durable per-network usage, errors, reentrancy, receipt retry");
                return true;
            } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        static async Task TestApply(ProfilePresetStore store, ProfilePreset preset) {
            int calls = 0;
            var failed = new ProfilePresetApplySession(store, "TikTok", "failure", p => { calls++; throw new InvalidOperationException("Network failure"); });
            try { await failed.ApplyAsync(preset); throw new Exception("Failed callback accepted"); } catch (InvalidOperationException) { }
            Check(calls == 1 && !store.GetUsed("TikTok", preset.Id) && !failed.Busy, "Failure not consumed");
            var ready = new TaskCompletionSource<bool>();
            var success = new ProfilePresetApplySession(store, "TikTok", "success", p => { calls++; return ready.Task; });
            Task running = success.ApplyAsync(preset);
            try { await success.ApplyAsync(preset); throw new Exception("Reentry accepted"); } catch (InvalidOperationException) { }
            ready.SetResult(true); await running;
            Check(calls == 2 && store.GetUsed("TikTok", preset.Id) && success.PendingReceipt == null, "Success consumed once");
            try { await success.ApplyAsync(preset); throw new Exception("Consumed preset applied"); } catch (InvalidOperationException) { }
            Check(calls == 2, "Used preset never reaches callback");
            var parallelPreset = new ProfilePreset { Name = "Параллельный", Bio = "Проверка", AvatarPath = preset.AvatarPath };
            store.Save(parallelPreset);
            var barrier = new TaskCompletionSource<bool>(); int parallelCalls = 0;
            var firstWindow = new ProfilePresetApplySession(store, "Telegram", "first", p => { parallelCalls++; return barrier.Task; });
            var secondWindow = new ProfilePresetApplySession(store, "Telegram", "second", p => { parallelCalls++; return Task.CompletedTask; });
            Task firstApply = firstWindow.ApplyAsync(parallelPreset);
            bool duplicateRejected = false; try { await secondWindow.ApplyAsync(parallelPreset); } catch (InvalidOperationException) { duplicateRejected = true; }
            Check(duplicateRejected && parallelCalls == 1, "Same-network concurrent preset rejected before remote change");
            barrier.SetResult(true); await firstApply; store.Delete(parallelPreset.Id);
            int receiptCalls = 0;
            var receiptReady = new TaskCompletionSource<bool>();
            var receipt = new ProfilePresetApplySession(store, "Instagram", "receipt", p => { receiptCalls++; return receiptReady.Task; });
            Task pending = receipt.ApplyAsync(preset);
            string original = File.ReadAllText(store.DataPath);
            File.WriteAllText(store.DataPath, "{broken");
            receiptReady.SetResult(true);
            bool writeFailed = false;
            try { await pending; } catch { writeFailed = true; }
            Check(writeFailed && receipt.PendingReceipt != null, "Confirmed network success retains pending receipt on persistence error");
            File.WriteAllText(store.DataPath, original);
            await receipt.ApplyAsync(preset);
            Check(receiptCalls == 1 && store.GetUsed("Instagram", preset.Id) && receipt.PendingReceipt == null, "Receipt retry does not repeat social change");
        }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void ExpectFailure(Action action, string message) {
            bool failed = false; try { action(); } catch { failed = true; }
            Check(failed, message);
        }
#if PROFILE_PRESET_TEST_HOST
        [STAThread]
        public static int Main() { try { return RunSelfTests() ? 0 : 1; } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }
#endif
    }
#if PROFILE_PRESET_TEST_HOST
    // Test-only stub rejects accidental use of the actual user's application store.
    public static class Store {
        public static string Root { get { throw new InvalidOperationException("Tests must inject a temporary storage root."); } }
    }
#endif
}
