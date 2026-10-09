using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace VideoBatch {
    public static partial class AccountLibraryTests {
        static Preferences LinkedFixture() {
            return new Preferences {
                YouTubeChannels = new List<YouTubeChannel> {
                    new YouTubeChannel { ChannelId = "yt-ru", Name = "Мой канал", ProfileId = "browser-1", Market = "RU", Kind = "shorts", ChannelUrl = "https://www.youtube.com/@Fixture/videos", ExpectedIp = "203.0.113.7" },
                    new YouTubeChannel { ChannelId = "yt-en", Name = "My channel", ProfileId = "browser-1", Market = "EN", Kind = "long", ChannelUrl = "https://youtube.com/@Fixture/shorts", ExpectedIp = "203.0.113.7" },
                    new YouTubeChannel { ChannelId = "yt-other", Name = "Другой канал", ProfileId = "browser-1", Market = "EN", ChannelUrl = "https://youtube.com/@OtherFixture" }
                },
                TikTokAccounts = new List<TikTokAccount> {
                    new TikTokAccount { AccountId = "tk-ru", Name = "TikTok RU", ProfileId = "browser-1", Market = "RU", ExpectedIp = "203.0.113.7" },
                    new TikTokAccount { AccountId = "tk-en", Name = "TikTok EN", ProfileId = "browser-1", Market = "EN" }
                }
            };
        }
        public static bool RunSyncTests() {
            string root = Temp(), oldRoot = Store.Root;
            try {
                Store.Root = root; var store = new AccountLibraryStore(root); var prefs = LinkedFixture(); Store.Save(prefs);
                var result = AccountLibrarySync.Sync(prefs, store);
                Check(result.Added == 4 && store.Load().Count == 4, "existing upload accounts appear; same channel URL shares a card across markets");
                var card = store.Load().Single(a => a.Sources.Count == 2);
                Check(card.Sources.All(s => s.ProfileId == "browser-1") && AccountLibrarySync.Markets(card).SequenceEqual(new[] { "EN", "RU" }), "browser links and markets preserved");
                Check(card.Proxy == "" && card.Login == "" && card.Password == "" && card.Country == "" && card.PurchaseUrl == "", "IP/market do not become invented proxy/credentials/country/purchase");
                Check(AccountLibrarySync.Missing(card).Length == 4 && AccountLibrarySync.DescribeSources(card, prefs).Contains("203.0.113.7"), "missing fields and explicit IP are visible separately");
                var before = File.ReadAllBytes(store.Path); Check(!AccountLibrarySync.Sync(prefs, store).Changed && before.SequenceEqual(File.ReadAllBytes(store.Path)), "repeat refresh adds nothing and does not rewrite vault");
                card.Login = "manual@test.invalid"; card.Password = "manual-secret"; card.Proxy = "socks5://u:p@127.0.0.1:1080"; card.Country = "Канада"; card.PurchaseUrl = "https://seller.example.test/order"; card.Notes = "Мои сведения"; store.Upsert(card);
                prefs.YouTubeChannels[0].Name = "Переименован в YouTube"; AccountLibrarySync.Sync(prefs, store);
                var updated = store.Load().Single(a => a.Id == card.Id);
                Check(updated.Name == "Переименован в YouTube" && updated.Password == card.Password && updated.Proxy == card.Proxy && updated.PurchaseUrl == card.PurchaseUrl && updated.Country == "Канада", "source rename follows stable ID; manual secret fields retained");
                updated.Name = "Моя карточка"; updated.Url = "https://youtube.com/@MyManualOverride"; store.Upsert(updated);
                prefs.YouTubeChannels[0].Name = "Следующее название"; prefs.YouTubeChannels[0].ChannelUrl = "https://youtube.com/@ChangedSource"; AccountLibrarySync.Sync(prefs, store);
                updated = store.Load().Single(a => a.Id == card.Id); Check(updated.Name == "Моя карточка" && updated.Url == "https://youtube.com/@MyManualOverride", "manual name/URL overrides not overwritten");
                var reloaded = Store.Load(); AccountLibrarySync.Sync(reloaded, store); Check(store.Load().Count == 4, "settings reload keeps source IDs and prevents duplicate cards");
                var tiktok = new TikTokAccount { Name = "Без ID", ProfileId = "browser-2", Market = "RU" }; reloaded.TikTokAccounts.Add(tiktok);
                AccountLibrarySync.Sync(reloaded, store); string stable = tiktok.AccountId; Check(stable != "" && Store.Load().TikTokAccounts.Any(a => a.AccountId == stable), "legacy TikTok rows acquire durable source ID");
                reloaded.TikTokAccounts.Remove(tiktok); AccountLibrarySync.Sync(reloaded, store);
                Check(store.Load().Any(a => a.Sources.Any(s => s.SourceId == stable)), "removed row leaves its card intact");
                var manual = Demo("Ручная карточка", "YouTube", "EN", 45); manual.Url = "https://youtube.com/@ManualFixture"; store.Upsert(manual);
                reloaded.YouTubeChannels.Add(new YouTubeChannel { ChannelId = "manual-link", Name = "Название в YouTube", ChannelUrl = "https://www.youtube.com/@ManualFixture/videos", Market = "EN" });
                AccountLibrarySync.Sync(reloaded, store); Check(store.Load().Single(a => a.Id == manual.Id).Sources.Count == 1, "existing manual card linked by exact canonical channel URL");
                var second = manual.Copy(); second.Id = Guid.NewGuid().ToString("N"); second.Sources.Clear(); store.Upsert(second);
                reloaded.YouTubeChannels.Add(new YouTubeChannel { ChannelId = "ambiguous", Name = "Неоднозначная строка", ChannelUrl = manual.Url, Market = "EN" });
                Check(AccountLibrarySync.Sync(reloaded, store).Unresolved == 1, "ambiguous identity is reported instead of guessed");
                var old = File.ReadAllBytes(store.Path); old[old.Length - 20] ^= 0x7f; File.WriteAllBytes(store.Path, old);
                var unset = new YouTubeChannel { Name = "Не трогать ID", ChannelId = "" }; reloaded.YouTubeChannels.Add(unset);
                bool refused = false; try { AccountLibrarySync.Sync(reloaded, store); } catch (IOException) { refused = true; }
                Check(refused && unset.ChannelId == "" && File.ReadAllBytes(store.Path).SequenceEqual(old), "damaged vault does not cause source mutation or data loss");
                Console.WriteLine("Accounts sync: source IDs, existing rows, URL identity, missing fields, manual data and reload PASS"); return true;
            } finally { Store.Root = oldRoot; Directory.Delete(root, true); }
        }
        public static bool RunScopedRemovalTests() {
            string root = Temp(), oldRoot = Store.Root;
            try {
                Store.Root = root; var store = new AccountLibraryStore(root); var prefs = LinkedFixture(); Store.Save(prefs);
                AccountLibrarySync.Sync(prefs, store); var ru = prefs.YouTubeChannels[0]; var en = prefs.YouTubeChannels[1]; var tkRu = prefs.TikTokAccounts[0]; var tkEn = prefs.TikTokAccounts[1];
                var card = store.Load().Single(a => a.Sources.Any(s => s.SourceId == ru.ChannelId)); card.AccessNotes = "saved-full-credentials"; card.Proxy = "manual-proxy"; card.PurchaseUrl = "https://seller.example.test/purchase"; store.Upsert(card);
                string file = Path.Combine(root, "source-video.mp4"); File.WriteAllText(file, "fixture"); en.Items.Add(new YouTubeItem { Video = file, Title = "Keep queue" });
                bool rejected = false; try { AccountRemoval.RemoveFromList(prefs, "EN", new[] { ru }, null, store); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && prefs.YouTubeChannels.Contains(ru), "stale or wrong market cannot remove a row");
                AccountRemoval.RemoveFromList(prefs, "RU", new[] { ru }, null, store);
                Check(!prefs.YouTubeChannels.Contains(ru) && prefs.YouTubeChannels.Contains(en) && prefs.TikTokAccounts.Contains(tkRu) && prefs.TikTokAccounts.Contains(tkEn), "YouTube RU removal leaves EN and both TikTok lists intact");
                Check(File.Exists(file) && en.Items.Single().Title == "Keep queue", "source files and sibling queue retained");
                AccountLibrarySync.Sync(prefs, store); var saved = store.Load().Single(a => a.Id == card.Id);
                Check(saved.AccessNotes == card.AccessNotes && saved.Proxy == card.Proxy && saved.PurchaseUrl == card.PurchaseUrl && AccountLibrarySync.DescribeSources(saved, prefs).Contains("убран из списка"), "card/credentials/purchase retained with removed-list marker");
                prefs.YouTubeChannels.Add(ru); Store.Save(prefs);
                AccountRemoval.RemoveFromList(prefs, "RU", null, new[] { tkRu }, store);
                Check(prefs.TikTokAccounts.Contains(tkEn) && prefs.TikTokSyncExcludedProfiles.Contains("RU|browser-1") && !prefs.TikTokSyncExcludedProfiles.Contains("EN|browser-1"), "TikTok suppression is market-specific");
                TikTokProfileSync.SyncFromYouTube(prefs, "RU"); Check(!prefs.TikTokAccounts.Any(a => a.ProfileId == "browser-1" && a.Market == "RU"), "auto-import does not re-add removed RU row");
                var oldYt = prefs.YouTubeChannels; var oldTk = prefs.TikTokAccounts; var oldExclusions = prefs.TikTokSyncExcludedProfiles;
                bool failed = false; try { AccountRemoval.RemoveFromList(prefs, "EN", null, new[] { tkEn }, store, p => { throw new IOException("fixture save failure"); }); } catch (IOException) { failed = true; }
                Check(failed && ReferenceEquals(oldYt, prefs.YouTubeChannels) && ReferenceEquals(oldTk, prefs.TikTokAccounts) && ReferenceEquals(oldExclusions, prefs.TikTokSyncExcludedProfiles), "failed persistence rolls back lists and suppression");
                var after = Store.Load(); Check(after.YouTubeChannels.Any(c => c.ChannelId == en.ChannelId) && after.TikTokAccounts.Any(a => a.AccountId == tkEn.AccountId), "reopen keeps remaining EN rows");
                Console.WriteLine("Scoped removal: RU/EN isolation, card preservation, queues, files, TikTok exclusion and rollback PASS"); return true;
            } finally { Store.Root = oldRoot; Directory.Delete(root, true); }
        }
        public static bool RunLinkedUiTests() {
            string root = Temp(), oldRoot = Store.Root;
            try {
                Store.Root = root; var prefs = LinkedFixture(); Store.Save(prefs);
                using (var shell = new AppShell(prefs)) {
                    shell.Show(); Pump(); Controls(shell).OfType<Button>().Single(b => b.Tag is NavSection section && section == NavSection.AccountLibrary).PerformClick(); Pump();
                    var panel = Controls(shell).OfType<AccountLibraryPanel>().Single();
                    Check(Find<System.Windows.Forms.ListBox>(panel, "AccountList").Items.Count == 4, "real shell populates cards from existing upload accounts");
                    var card = new AccountLibraryStore().Load().Single(a => a.Sources.Any(s => s.SourceId == "yt-en")); panel.SelectId(card.Id); Pump();
                    Check(Controls(panel).OfType<Label>().Any(l => l.Text.Contains("Не заполнено:") && l.Text.Contains("прокси")), "actual card displays missing-data checklist");
                    Check(Controls(shell).OfType<Button>().Any(b => b.Text == "Профили браузера"), "profile navigation is unambiguous");
                    Controls(panel).OfType<Button>().Single(b => b.Text == "YouTube · EN · long").PerformClick(); Pump();
                    var yt = Controls(shell).OfType<YouTubeWorkspacePanel>().Single(); var ytGrid = Controls(yt).OfType<DataGridView>().Single();
                    Check(yt.Visible && ytGrid.CurrentRow?.Tag is YouTubeChannel selected && selected.ChannelId == "yt-en", "card opens exact EN/Long row despite shared browser profile");
                    typeof(YouTubeWorkspacePanel).GetMethod("ShowRowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(yt, new object[] { prefs.YouTubeChannels[1], ytGrid.CurrentRow.Index, new Point(350, 280) }); Pump();
                    // Every context-menu label is also verified from the visible ToolStrip popup.
                    var popup = FindOpenMenu("Убрать из списка EN"); Check(popup != null && !popup.Items.Cast<ToolStripItem>().Any(x => x.Text == "Удалить аккаунт из приложения"), "actual YouTube context menu is list-specific"); popup.Close();
                    Controls(shell).OfType<Button>().Single(b => b.Tag is NavSection section && section == NavSection.AccountLibrary).PerformClick(); Pump();
                    var tkCard = new AccountLibraryStore().Load().Single(a => a.Sources.Any(s => s.SourceId == "tk-ru")); panel.SelectId(tkCard.Id); Pump();
                    Controls(panel).OfType<Button>().Single(b => b.Text == "TikTok · RU").PerformClick(); Pump();
                    var tkPanel = Controls(shell).OfType<TikTokWorkspacePanel>().Single(); var tkGrid = Controls(tkPanel).OfType<DataGridView>().Single();
                    Check(tkPanel.Visible && tkGrid.CurrentRow?.Tag is TikTokAccount tkSelected && tkSelected.AccountId == "tk-ru", "card opens exact TikTok RU row");
                    shell.Close();
                }
                Console.WriteLine("Linked UI: imported cards, missing-data markers, browser-profile label and exact source navigation PASS"); return true;
            } finally { Store.Root = oldRoot; Directory.Delete(root, true); }
        }
        static ContextMenuStrip FindOpenMenu(string caption) {
            // WinForms keeps visible dropdowns in the thread's native window list.
            ContextMenuStrip result = null;
            EnumWindows((handle, arg) => { if (Control.FromHandle(handle) is ContextMenuStrip menu && menu.Visible && menu.Items.Cast<ToolStripItem>().Any(i => i.Text == caption)) result = menu; return true; }, IntPtr.Zero);
            return result;
        }
        delegate bool EnumWindow(IntPtr handle, IntPtr argument);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr argument);
        public static bool WriteSyncScreenshots(string destination) {
            string root = Temp(), oldRoot = Store.Root;
            try {
                Store.Root = root; Directory.CreateDirectory(destination); var prefs = LinkedFixture(); Store.Save(prefs);
                using (var shell = new AppShell(prefs)) {
                    shell.Show(); Pump(); Controls(shell).OfType<Button>().Single(b => b.Tag is NavSection section && section == NavSection.AccountLibrary).PerformClick(); Pump();
                    var panel = Controls(shell).OfType<AccountLibraryPanel>().Single(); panel.SelectId(new AccountLibraryStore().Load().Single(a => a.Sources.Count == 2).Id); Pump(); Capture(shell, Path.Combine(destination, "accounts-linked-missing.png"));
                    Controls(panel).OfType<Button>().Single(b => b.Text == "YouTube · EN · long").PerformClick(); Pump(); var yt = Controls(shell).OfType<YouTubeWorkspacePanel>().Single(); var grid = Controls(yt).OfType<DataGridView>().Single();
                    typeof(YouTubeWorkspacePanel).GetMethod("ShowRowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(yt, new object[] { prefs.YouTubeChannels[1], grid.CurrentRow.Index, new Point(500, 290) }); Pump(); Capture(shell, Path.Combine(destination, "youtube-en-list.png")); FindOpenMenu("Убрать из списка EN")?.Close();
                    shell.Close();
                }
                return true;
            } finally { Store.Root = oldRoot; Directory.Delete(root, true); }
        }
    }
}
