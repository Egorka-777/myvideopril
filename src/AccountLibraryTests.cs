using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace VideoBatch {
    public static partial class AccountLibraryTests {
        static void Check(bool ok, string message) { if (!ok) throw new Exception("Accounts regression: " + message); }
        static string Temp() { string root = Path.Combine(Path.GetTempPath(), "vb-accounts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
        public static bool RunStoreTests() {
            string root = Temp();
            try {
                File.WriteAllText(Path.Combine(root, "settings.xml"), "do-not-change-upload-profiles");
                var store = new AccountLibraryStore(root); Check(store.Load().Count == 0, "empty first launch");
                var a = Demo("Русский канал", "YouTube", "RU", 1); a.AccessNotes = "reserve@example.test\r\nrecovery-data";
                store.Upsert(a); var read = new AccountLibraryStore(root).Load().Single();
                Check(read.Name == a.Name && read.Password == a.Password && read.Proxy == a.Proxy && read.AccessNotes == a.AccessNotes, "DPAPI reopen preserves all fields");
                string bytes = Encoding.UTF8.GetString(File.ReadAllBytes(store.Path));
                Check(!bytes.Contains(a.Password) && !bytes.Contains(a.Login) && !bytes.Contains(a.Name) && !bytes.Contains(a.Proxy), "vault contains no cleartext metadata/secrets");
                a.Name = "Новое имя"; store.Upsert(a); store.SetStatus(a.Id, "Заблокирован");
                Check(store.Load().Count == 1 && store.Load()[0].Status == "Заблокирован", "stable edit and quick status");
                Check(File.Exists(store.Path + ".bak"), "encrypted automatic backup");
                Check(store.Import(new[] { a.Copy() }) == 0, "repeat import skips duplicate URL");
                var other = a.Copy(); other.Id = Guid.NewGuid().ToString("N"); other.Language = "EN";
                Check(store.Import(new[] { other }) == 1, "separate market is a separate record");
                var blankUrl = Demo("Без ссылки", "TikTok", "RU", 9); blankUrl.Url = "";
                Check(store.Import(new[] { blankUrl, blankUrl.Copy() }) == 1, "no-URL duplicate uses name/login");
                Parallel.For(0, 50, i => new AccountLibraryStore(root).Upsert(Demo("Account " + i, "TikTok", "EN", i + 100)));
                Check(store.Load().Count == 53, "50 independent concurrent writes are not lost");
                store.Remove(a.Id); Check(store.Load().Count == 52, "remove one record only");
                Check(File.ReadAllText(Path.Combine(root, "settings.xml")) == "do-not-change-upload-profiles", "upload settings untouched");
                var corrupt = File.ReadAllBytes(store.Path); corrupt[corrupt.Length - 20] ^= 0x7f; File.WriteAllBytes(store.Path, corrupt);
                bool refused = false; try { store.Upsert(a); } catch (IOException) { refused = true; }
                Check(refused && File.ReadAllBytes(store.Path).SequenceEqual(corrupt), "corrupt vault refused without overwriting");
                File.Copy(store.Path + ".bak", store.Path, true); Check(store.Load().Count == 53, "previous encrypted snapshot can be restored");
                Console.WriteLine("Accounts store: DPAPI, backup, reload, concurrency, duplicate import and corruption checks PASS"); return true;
            } finally { Directory.Delete(root, true); }
        }
        public static bool RunWorkbookTests() {
            string root = Temp();
            try {
                string path = Path.Combine(root, "accounts.xlsx"); WriteWorkbook(path); var sheets = AccountWorkbook.Read(path);
                Check(sheets.Count == 4 && sheets[0].HeaderRow == 1, "four tabs and title before headers");
                var store = new AccountLibraryStore(root); int total = 0;
                foreach (var sheet in sheets) {
                    var map = AccountWorkbook.Keys.ToDictionary(k => k, k => AccountWorkbook.GuessColumn(sheet.Headers, k));
                    var records = AccountWorkbook.ConvertRows(sheet, map, AccountWorkbook.GuessPlatform(sheet.Name), AccountWorkbook.GuessLanguage(sheet.Name), out int skipped);
                    Check(records.Count == 2 && skipped == 1, "sparse rows and missing-name skip");
                    Check(records[0].Url.StartsWith("https://") && records[0].Name == "Первый канал", "shared strings and real URL behind hyperlink caption");
                    Check(records[0].PurchaseUrl == "https://seller.example.test/order" && records[0].Password == "00123", "purchase hyperlink and string password leading zeros");
                    Check(records[0].Notes.Contains("Статус в Excel: неизвестный") && records[0].Status == "Проверить", "unknown status is preserved as note");
                    Check(records[1].Name == "Второй канал" && records[1].Status == "Рабочий", "rich inline strings and normalized status");
                    total += store.Import(records); Check(store.Import(records) == 0, "repeat workbook import");
                    map["Password"] = map["Name"]; bool duplicate = false;
                    try { AccountWorkbook.ConvertRows(sheet, map, "YouTube", "RU", out _); } catch (ArgumentException) { duplicate = true; }
                    Check(duplicate, "ambiguous column mapping rejected");
                }
                Check(total == 8 && store.Load().Select(a => a.Platform + a.Language).Distinct().Count() == 4, "all four account groups imported");
                Check(AccountLibraryStore.Matches(store.Load()[0], "первый пример") && !AccountLibraryStore.Matches(store.Load()[0], "00123"), "search excludes passwords");
                Console.WriteLine("Accounts Excel: four tabs, sparse/shared/inline strings, hyperlinks, mapping and duplicate checks PASS"); return true;
            } finally { Directory.Delete(root, true); }
        }
        static IEnumerable<Control> Controls(Control parent) {
            foreach (Control c in parent.Controls) { yield return c; foreach (var child in Controls(c)) yield return child; }
        }
        static T Find<T>(Control parent, string name) where T : Control { return Controls(parent).OfType<T>().Single(c => c.Name == name); }
        static void Pump() { Application.DoEvents(); }
        public static bool RunUiTests() {
            string root = Temp(); string oldRoot = Store.Root;
            try {
                Store.Root = root; var store = new AccountLibraryStore(root);
                var first = Demo("Первый аккаунт", "YouTube", "RU", 1); first.AccessNotes = "reserve@test.invalid\r\nrecovery-code";
                store.Upsert(first); store.Upsert(Demo("Второй аккаунт", "TikTok", "EN", 2));
                using (var host = new Form { ClientSize = new Size(1040, 730), BackColor = Theme.Background })
                using (var panel = new AccountLibraryPanel(store) { Dock = DockStyle.Fill }) {
                    host.Controls.Add(panel); host.Show(); Pump(); panel.SelectId(first.Id);
                    Check(panel.SelectedAccount.Id == first.Id, "select account");
                    Check(!Controls(panel).OfType<TextBox>().Any(t => t.Text.Contains(first.Password)), "secrets hidden by default");
                    Find<Button>(panel, "RevealAccountSecrets").PerformClick(); Pump();
                    Check(Controls(panel).OfType<TextBox>().Any(t => t.Text == first.Password), "explicit reveal"); panel.HideSecrets();
                    Check(!Controls(panel).OfType<TextBox>().Any(t => t.Text.Contains(first.Password)), "hide again");
                    panel.ApplySearch("Второй"); Check(panel.SelectedAccount.Platform == "TikTok", "search selection"); panel.ApplySearch(""); panel.SelectId(first.Id);
                    Find<ComboBox>(panel, "AccountStatus").SelectedItem = "Заблокирован"; Pump();
                    Check(store.Load().Single(a => a.Id == first.Id).Status == "Заблокирован", "quick status persists");
                    panel.RefreshData(); Check(panel.SelectedAccount.Id == first.Id, "selected account survives refresh");
                    host.ClientSize = new Size(840, 600); Pump();
                    Check(Find<Button>(panel, "AddAccount").Right <= Find<Button>(panel, "AddAccount").Parent.ClientSize.Width, "toolbar fits compact width");
                    host.Close();
                }
                using (var wizard = new AccountWizard(first)) {
                    wizard.Show(); Pump(); Find<TextBox>(wizard, "AccountName").Text = "После редактирования";
                    Find<Button>(wizard, "AccountNext").PerformClick(); Pump();
                    Find<CheckBox>(wizard, "ShowAccountEditSecrets").Checked = true;
                    Check(Find<TextBox>(wizard, "AccountAccessNotes").Text == first.AccessNotes, "multiline access data retained by editor");
                    Find<TextBox>(wizard, "AccountProxy").Text = "socks5://login:password@127.0.0.2:1080";
                    Find<Button>(wizard, "AccountNext").PerformClick(); Pump(); Find<Button>(wizard, "AccountNext").PerformClick(); Pump();
                    Check(wizard.Result.Id == first.Id && wizard.Result.Name == "После редактирования" && wizard.Result.AccessNotes == first.AccessNotes, "three-step edit keeps ID and multiline access data"); store.Upsert(wizard.Result);
                }
                using (var cancelled = new AccountWizard()) { cancelled.Show(); Pump(); cancelled.Close(); Check(cancelled.Result == null, "cancel adds nothing"); }
                string xlsx = Path.Combine(root, "accounts.xlsx"); WriteWorkbook(xlsx);
                using (var dialog = new AccountImportDialog(AccountWorkbook.Read(xlsx))) {
                    dialog.Show(); Pump();
                    // Remove the intentionally nameless fixture to avoid a confirmation dialog.
                    var sheets = AccountWorkbook.Read(xlsx); sheets[0].Rows.RemoveAt(sheets[0].Rows.Count - 1);
                    dialog.Close();
                    using (var clean = new AccountImportDialog(sheets)) { clean.Show(); Pump(); Find<Button>(clean, "ConfirmAccountImport").PerformClick(); Pump(); Check(clean.Result.Count == 2, "actual import dialog confirms mapped rows"); }
                }
                using (var shell = new AppShell(new Preferences())) {
                    shell.Show(); Pump(); var nav = Controls(shell).OfType<Button>().Single(b => b.Tag is NavSection section && section == NavSection.AccountLibrary);
                    nav.PerformClick(); Pump(); Check(Controls(shell).OfType<AccountLibraryPanel>().Single().Visible, "native shell navigation opens independent accounts section"); shell.Close();
                }
                Console.WriteLine("Accounts UI: native list, masking, status, wizard, Excel dialog and shell navigation PASS"); return true;
            } finally { Store.Root = oldRoot; Directory.Delete(root, true); }
        }
        public static bool WriteScreenshots(string destination) {
            string root = Temp(); string oldRoot = Store.Root;
            try {
                Store.Root = root; Directory.CreateDirectory(destination); var store = new AccountLibraryStore(root);
                for (int i = 0; i < 50; i++) { var a = Demo(new[] { "Market Daily", "Трейдинг каждый день", "City Stories", "Мой портфель", "Finance Tips" }[i % 5] + (i < 5 ? "" : " " + (i + 1)), i % 2 == 0 ? "YouTube" : "TikTok", i % 3 == 0 ? "EN" : "RU", i); a.Status = i % 7 == 0 ? "Заблокирован" : i % 5 == 0 ? "Проверить" : "Рабочий"; store.Upsert(a); }
                using (var shell = new AppShell(new Preferences())) {
                    shell.Show(); Pump(); Controls(shell).OfType<Button>().Single(b => b.Tag is NavSection section && section == NavSection.AccountLibrary).PerformClick(); Pump();
                    var panel = Controls(shell).OfType<AccountLibraryPanel>().Single(); panel.ApplySearch("Market Daily"); Pump(); Capture(shell, Path.Combine(destination, "accounts-card.png"));
                    panel.ApplySearch(""); Capture(shell, Path.Combine(destination, "accounts-list.png")); shell.ClientSize = new Size(1100, 700); Pump(); Capture(shell, Path.Combine(destination, "accounts-compact.png")); shell.Close();
                }
                using (var wizard = new AccountWizard()) { wizard.Show(); Pump(); Capture(wizard, Path.Combine(destination, "accounts-add.png")); wizard.Close(); }
                string xlsx = Path.Combine(root, "accounts.xlsx"); WriteWorkbook(xlsx);
                using (var dialog = new AccountImportDialog(AccountWorkbook.Read(xlsx))) { dialog.Show(); Pump(); Capture(dialog, Path.Combine(destination, "accounts-excel.png")); dialog.Close(); }
                return true;
            } finally { Store.Root = oldRoot; Directory.Delete(root, true); }
        }
        static void Capture(Form form, string path) { Pump(); using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path); } }
        static AccountRecord Demo(string name, string platform, string language, int n) {
            return new AccountRecord { Name = name, Platform = platform, Language = language, Country = language == "RU" ? "Россия" : "США", Status = "Рабочий",
                Url = "https://" + (platform == "YouTube" ? "youtube.com" : "tiktok.com") + "/@demo" + n, Login = "demo" + n + "@example.test", Password = "test-secret-value-" + n,
                Proxy = "socks5://demo:sample@127.0.0.1:1080", PurchaseUrl = "https://seller.example.test/order/" + n, Notes = "Пример карточки. Куплен у продавца, доступ проверен. Здесь можно записать дату покупки и что нужно сделать." };
        }
        static void WriteWorkbook(string path) {
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships", p = "http://schemas.openxmlformats.org/package/2006/relationships";
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) {
                var names = new[] { "YouTube русские", "YouTube английские", "TikTok русские", "TikTok английские" };
                Put(zip, "xl/workbook.xml", new XDocument(new XElement(s + "workbook", new XAttribute(XNamespace.Xmlns + "r", r), new XElement(s + "sheets", names.Select((name, i) => new XElement(s + "sheet", new XAttribute("name", name), new XAttribute("sheetId", i + 1), new XAttribute(r + "id", "s" + i)))))));
                Put(zip, "xl/_rels/workbook.xml.rels", new XDocument(new XElement(p + "Relationships", names.Select((name, i) => new XElement(p + "Relationship", new XAttribute("Id", "s" + i), new XAttribute("Type", r.NamespaceName + "/worksheet"), new XAttribute("Target", "worksheets/sheet" + i + ".xml"))))));
                Put(zip, "xl/sharedStrings.xml", new XDocument(new XElement(s + "sst", new XElement(s + "si", new XElement(s + "t", "Первый канал")))));
                XElement Inline(string reference, string value) { return new XElement(s + "c", new XAttribute("r", reference), new XAttribute("t", "inlineStr"), new XElement(s + "is", new XElement(s + "t", value))); }
                for (int i = 0; i < 4; i++) {
                    var headers = new[] { "Название", "Ссылка", "Логин", "Пароль", "Страна", "Заметка", "Покупка", "Статус", "Прокси", "Данные для входа" };
                    var data = new XElement(s + "sheetData",
                        new XElement(s + "row", new XAttribute("r", 1), Inline("A1", "Мои аккаунты")),
                        new XElement(s + "row", new XAttribute("r", 3), headers.Select((h, j) => Inline(((char)('A' + j)) + "3", h))),
                        new XElement(s + "row", new XAttribute("r", 5), new XElement(s + "c", new XAttribute("r", "A5"), new XAttribute("t", "s"), new XElement(s + "v", 0)), Inline("B5", "Открыть канал"), Inline("C5", "user@example.test"), Inline("D5", "00123"), Inline("E5", "Пример"), Inline("F5", "Проверить"), Inline("G5", "Заказ"), Inline("H5", "неизвестный")),
                        new XElement(s + "row", new XAttribute("r", 8), new XElement(s + "c", new XAttribute("r", "A8"), new XAttribute("t", "inlineStr"), new XElement(s + "is", new XElement(s + "r", new XElement(s + "t", "Второй ")), new XElement(s + "r", new XElement(s + "t", "канал")))), Inline("B8", "https://example.test/account/" + i), Inline("H8", "рабочий")),
                        new XElement(s + "row", new XAttribute("r", 10), Inline("C10", "nameless@example.test")));
                    Put(zip, "xl/worksheets/sheet" + i + ".xml", new XDocument(new XElement(s + "worksheet", new XAttribute(XNamespace.Xmlns + "r", r), data, new XElement(s + "hyperlinks", new XElement(s + "hyperlink", new XAttribute("ref", "B5"), new XAttribute(r + "id", "h1")), new XElement(s + "hyperlink", new XAttribute("ref", "G5"), new XAttribute(r + "id", "h2"))))));
                    Put(zip, "xl/worksheets/_rels/sheet" + i + ".xml.rels", new XDocument(new XElement(p + "Relationships", new XElement(p + "Relationship", new XAttribute("Id", "h1"), new XAttribute("Type", r.NamespaceName + "/hyperlink"), new XAttribute("Target", "https://example.test/first/" + i), new XAttribute("TargetMode", "External")), new XElement(p + "Relationship", new XAttribute("Id", "h2"), new XAttribute("Type", r.NamespaceName + "/hyperlink"), new XAttribute("Target", "https://seller.example.test/order"), new XAttribute("TargetMode", "External")))));
                }
            }
        }
        static void Put(ZipArchive zip, string path, XDocument document) { using (var stream = zip.CreateEntry(path).Open()) document.Save(stream); }
    }
}
