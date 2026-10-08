using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VideoBatch {
    public sealed class AccountSheet {
        public string Name;
        public List<string[]> Rows = new List<string[]>();
        public List<string[]> LinkRows = new List<string[]>();
        public List<int> SourceRows = new List<int>();
        public int HeaderRow;
        public override string ToString() { return Name; }
        public string[] Headers => Rows.Count == 0 ? new string[0] : Rows[HeaderRow];
    }
    public static class AccountWorkbook {
        public static readonly string[] Keys = { "Name", "Url", "Login", "Password", "AccessNotes", "Proxy", "Country", "Status", "PurchaseUrl", "Notes" };
        public static readonly string[] Captions = { "Название", "Ссылка на аккаунт", "Логин / почта", "Пароль", "Данные для входа (целиком)", "Прокси", "Страна", "Статус", "Ссылка на покупку", "Заметки" };
        static readonly string[][] Aliases = {
            new[] { "название", "название канала", "название аккаунта", "канал", "аккаунт", "name", "channel name", "account name" },
            new[] { "ссылка", "ссылка на канал", "ссылка на аккаунт", "url", "channel url", "account url", "link" },
            new[] { "логин", "почта", "email", "e-mail", "login", "username", "логин/почта" },
            new[] { "пароль", "password", "pass" },
            new[] { "данные", "данные для входа", "данные входа", "доступ", "credentials", "access", "login data" },
            new[] { "прокси", "proxy" }, new[] { "страна", "country", "гео", "geo" },
            new[] { "статус", "status", "состояние" },
            new[] { "покупка", "ссылка на покупку", "где купил", "где покупал", "продавец", "purchase", "purchase url", "seller", "источник" },
            new[] { "заметка", "заметки", "примечание", "notes", "note", "комментарий" }
        };
        static string Normal(string value) { return Regex.Replace((value ?? "").Trim().ToLowerInvariant().Replace('ё', 'е'), @"\s+", " "); }
        public static int GuessColumn(string[] headers, string key) {
            int field = Array.IndexOf(Keys, key); if (field < 0) return -1;
            for (int i = 0; i < headers.Length; i++) if (Aliases[field].Contains(Normal(headers[i]))) return i;
            return -1;
        }
        public static string GuessPlatform(string name) { string n = Normal(name); return n.Contains("tiktok") || n.Contains("tik tok") || n.Contains("тикток") || n.Contains("тик ток") ? "TikTok" : "YouTube"; }
        public static string GuessLanguage(string name) { string n = Normal(name); return n.Contains("англ") || n.Contains("english") || Regex.IsMatch(n, @"\ben\b") ? "EN" : "RU"; }
        public static List<AccountSheet> Read(string path) {
            if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase)) throw new IOException("Сохрани таблицу в Excel как .xlsx и попробуй снова.");
            try {
                using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read)) {
                    var workbook = Xml(zip, "xl/workbook.xml"); var rels = Relationships(Xml(zip, "xl/_rels/workbook.xml.rels"));
                    var strings = new List<string>();
                    var sharedRel = rels.Values.FirstOrDefault(r => r.Item2.EndsWith("/sharedStrings", StringComparison.Ordinal));
                    string sharedPath = sharedRel == null ? "xl/sharedStrings.xml" : Resolve("xl/workbook.xml", sharedRel.Item1);
                    if (zip.GetEntry(sharedPath) != null) strings = Xml(zip, sharedPath).Descendants().Where(x => x.Name.LocalName == "si").Select(Text).ToList();
                    var result = new List<AccountSheet>();
                    foreach (var sheet in workbook.Descendants().Where(x => x.Name.LocalName == "sheet")) {
                        string id = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
                        if (id == null || !rels.TryGetValue(id, out var rel) || !rel.Item2.EndsWith("/worksheet", StringComparison.Ordinal)) continue;
                        string sheetPath = Resolve("xl/workbook.xml", rel.Item1); var doc = Xml(zip, sheetPath);
                        var hyperlinks = new Dictionary<string, string>();
                        string sheetRel = PathForRelationships(sheetPath);
                        if (zip.GetEntry(sheetRel) != null) {
                            var links = Relationships(Xml(zip, sheetRel));
                            foreach (var link in doc.Descendants().Where(x => x.Name.LocalName == "hyperlink")) {
                                string linkId = link.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
                                if (linkId != null && links.TryGetValue(linkId, out var target) && (target.Item1.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || target.Item1.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                                    hyperlinks[(string)link.Attribute("ref") ?? ""] = target.Item1;
                            }
                        }
                        var item = new AccountSheet { Name = (string)sheet.Attribute("name") ?? "Вкладка" };
                        int total = 0, max = 0;
                        foreach (var row in doc.Descendants().Where(x => x.Name.LocalName == "row")) {
                            if (++total > 20000) throw new IOException("В одной вкладке больше 20 000 строк. Раздели таблицу на части.");
                            var cells = new Dictionary<int, string>(); var rowLinks = new Dictionary<int, string>(); int next = 0;
                            foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "c")) {
                                string reference = (string)cell.Attribute("r") ?? ""; int column = reference == "" ? next : ColumnIndex(reference);
                                if (column < 0 || column >= 256) throw new IOException("Поддерживается до 256 столбцов. Удали лишние столбцы из копии таблицы.");
                                next = column + 1; string kind = (string)cell.Attribute("t") ?? "";
                                string value = cell.Elements().FirstOrDefault(x => x.Name.LocalName == "v")?.Value ?? "";
                                if (kind == "s") value = int.TryParse(value, out int n) && n >= 0 && n < strings.Count ? strings[n] : "";
                                else if (kind == "inlineStr") value = Text(cell);
                                // Keep the caption for Name, but the real target for URL/PurchaseUrl.
                                if (hyperlinks.TryGetValue(reference, out var url)) rowLinks[column] = url;
                                cells[column] = value;
                            }
                            if (cells.Count == 0 || cells.Values.All(string.IsNullOrWhiteSpace)) continue;
                            max = Math.Max(max, cells.Keys.Max() + 1); var values = new string[cells.Keys.Max() + 1];
                            foreach (var pair in cells) values[pair.Key] = pair.Value;
                            item.Rows.Add(values.Select(v => v ?? "").ToArray());
                            var linkValues = new string[values.Length]; foreach (var pair in rowLinks) linkValues[pair.Key] = pair.Value;
                            item.LinkRows.Add(linkValues.Select(v => v ?? "").ToArray());
                            item.SourceRows.Add(int.TryParse((string)row.Attribute("r"), out int physical) ? physical : total);
                        }
                        if (item.Rows.Count == 0) continue;
                        item.Rows = item.Rows.Select(row => row.Concat(Enumerable.Repeat("", max - row.Length)).ToArray()).ToList();
                        item.LinkRows = item.LinkRows.Select(row => row.Concat(Enumerable.Repeat("", max - row.Length)).ToArray()).ToList();
                        item.HeaderRow = Enumerable.Range(0, Math.Min(25, item.Rows.Count)).OrderByDescending(i => Keys.Count(k => GuessColumn(item.Rows[i], k) >= 0)).First();
                        result.Add(item);
                    }
                    if (result.Count == 0) throw new IOException("В таблице не нашлось заполненных вкладок.");
                    return result;
                }
            } catch (InvalidDataException) { throw new IOException("Не удалось прочитать .xlsx. Открой таблицу в Excel и сохрани новую копию без пароля."); }
        }
        static int ColumnIndex(string reference) { int n = 0; foreach (char c in reference.ToUpperInvariant()) { if (c < 'A' || c > 'Z') break; n = checked(n * 26 + c - 'A' + 1); } return n - 1; }
        static string Text(XElement root) { return string.Concat(root.Descendants().Where(x => x.Name.LocalName == "t" && !x.Ancestors().Any(a => a.Name.LocalName == "rPh")).Select(x => x.Value)); }
        static XDocument Xml(ZipArchive zip, string path) {
            var entry = zip.GetEntry(path); if (entry == null) throw new InvalidDataException();
            if (entry.Length > 40 * 1024 * 1024) throw new IOException("Вкладка слишком большая. Сохрани отдельную таблицу только с аккаунтами.");
            using (var stream = entry.Open()) using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 40 * 1024 * 1024 })) return XDocument.Load(reader);
        }
        static Dictionary<string, Tuple<string, string>> Relationships(XDocument doc) {
            return doc.Descendants().Where(x => x.Name.LocalName == "Relationship").ToDictionary(x => (string)x.Attribute("Id"), x => Tuple.Create((string)x.Attribute("Target") ?? "", (string)x.Attribute("Type") ?? ""));
        }
        static string Resolve(string part, string target) {
            var uri = new Uri(new Uri("https://xlsx.local/" + part), target.Replace('\\', '/'));
            if (uri.Host != "xlsx.local") throw new InvalidDataException();
            return Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        }
        static string PathForRelationships(string part) { int split = part.LastIndexOf('/'); return part.Substring(0, split + 1) + "_rels/" + part.Substring(split + 1) + ".rels"; }
        public static List<AccountRecord> ConvertRows(AccountSheet sheet, IDictionary<string, int> columns, string platform, string language, out int skipped) {
            if (!columns.TryGetValue("Name", out int name) || name < 0) throw new ArgumentException("Выбери столбец с названием аккаунта.");
            var used = columns.Values.Where(c => c >= 0).ToList();
            if (used.Distinct().Count() != used.Count) throw new ArgumentException("Один столбец выбран для нескольких полей. Оставь каждый столбец в одном поле.");
            var result = new List<AccountRecord>(); skipped = 0;
            for (int i = sheet.HeaderRow + 1; i < sheet.Rows.Count; i++) {
                string Value(string[] values, string key) {
                    if (!columns.TryGetValue(key, out int c) || c < 0 || c >= values.Length) return "";
                    if ((key == "Url" || key == "PurchaseUrl") && i < sheet.LinkRows.Count && c < sheet.LinkRows[i].Length && !string.IsNullOrEmpty(sheet.LinkRows[i][c])) return sheet.LinkRows[i][c];
                    return values[c];
                }
                var row = sheet.Rows[i]; string title = Value(row, "Name").Trim(); if (title == "") { skipped++; continue; }
                string originalStatus = Value(row, "Status").Trim(); string normalized = Normal(originalStatus), state = "Проверить";
                if (new[] { "рабочий", "работает", "активен", "active", "working", "ok" }.Contains(normalized)) state = "Рабочий";
                else if (new[] { "не рабочий", "нерабочий", "не работает", "not working" }.Contains(normalized)) state = "Не работает";
                else if (new[] { "заблокирован", "бан", "banned", "blocked" }.Contains(normalized)) state = "Заблокирован";
                else if (new[] { "архив", "archive" }.Contains(normalized)) state = "Архив";
                var a = new AccountRecord { Name = title, Platform = platform, Language = language, Country = Value(row, "Country").Trim(), Status = state,
                    Url = Value(row, "Url").Trim(), Login = Value(row, "Login"), Password = Value(row, "Password"), AccessNotes = Value(row, "AccessNotes"), Proxy = Value(row, "Proxy"), PurchaseUrl = Value(row, "PurchaseUrl").Trim(), Notes = Value(row, "Notes") };
                if (originalStatus != "" && normalized != Normal(state)) a.Notes += (a.Notes == "" ? "" : "\r\n") + "Статус в Excel: " + originalStatus;
                try { AccountLibraryStore.Validate(a); }
                catch (ArgumentException) { throw new ArgumentException("Проверь строку " + (i < sheet.SourceRows.Count ? sheet.SourceRows[i] : i + 1) + ": название, ссылку на аккаунт и ссылку на покупку. Ссылки должны начинаться с http:// или https://."); }
                result.Add(a);
            }
            if (result.Count == 0) throw new ArgumentException("Нет аккаунтов для импорта. Проверь строку заголовков и столбец «Название».");
            return result;
        }
    }
}
