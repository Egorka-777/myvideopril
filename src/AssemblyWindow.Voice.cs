using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        string ElevenLabsApiKey() {
            string key = WindowsSupport.Unprotect(assemblyPrefs.AssemblyElevenLabsKey);
            if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
            return WindowsSupport.Unprotect(assemblyPrefs.ProtectedElevenLabsKey).Trim();
        }

        string SpeechFolderRelative() => Path.Combine("Озвучка", AssemblyProfile.ShortLabel(studioProfile));

        string SpeechFolderAbsolute() {
            string folder = template.Resolve(SpeechFolderRelative());
            Directory.CreateDirectory(folder);
            return folder;
        }

        void OpenVoiceEditor() {
            LeaveSubtitleWorkspaceIfOpen();
            using (var dialog = new AssemblyVoiceEditor(this)) dialog.ShowDialog(this);
        }

        sealed class AssemblyVoiceEditor : Form {
            const int MinimumRows = 10;
            readonly AssemblyWindow owner;
            readonly ComboBox voices;
            readonly CheckBox speakerBoost;
            readonly NumericUpDown stability, similarity, expression, speed;
            readonly DataGridView lines;
            readonly Label message, folderLabel;
            readonly Button loadVoices, generate, save;
            readonly FlowLayoutPanel settings;
            bool busy, initializing = true;
            readonly CancellationTokenSource lifetime = new CancellationTokenSource();

            public AssemblyVoiceEditor(AssemblyWindow owner) {
                this.owner = owner;
                var speech = owner.template.Speech ?? new AssemblySpeechSettings();
                Text = "Озвучка · " + AssemblyProfile.ShortLabel(owner.studioProfile);
                Name = "AssemblyVoiceEditor"; Font = Theme.FontBody; BackColor = Theme.Background; ForeColor = Theme.TextPrimary;
                StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(760, 620); ClientSize = new Size(920, 720);
                var area = Screen.FromControl(owner).WorkingArea;
                Size = new Size(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 40));

                var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
                Controls.Add(root);

                settings = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
                root.Controls.Add(settings, 0, 0);

                folderLabel = new Label { AutoSize = false, Height = 52, ForeColor = Theme.TextSecondary };
                UpdateFolderLabel();
                Field("Файлы сохраняются сюда (по одному MP3 на строку с текстом):", folderLabel);

                voices = new ComboBox { Name = "AssemblyMaleVoice", DropDownStyle = ComboBoxStyle.DropDownList };
                if (!string.IsNullOrWhiteSpace(speech.VoiceId))
                    voices.Items.Add(new ElevenLabsVoice { voice_id = speech.VoiceId, name = "Сохранённый · " + speech.VoiceId.Substring(0, Math.Min(8, speech.VoiceId.Length)) });
                if (voices.Items.Count > 0) voices.SelectedIndex = 0;
                Field("Мужской голос (из вашего ключа в «Настройки»)", voices);

                loadVoices = Button("Загрузить мужские голоса"); loadVoices.Click += async (s, e) => await LoadVoices();
                Field(null, loadVoices);

                stability = Slider("Стабильность", 0, 1, 2, speech.Stability, "");
                similarity = Slider("Сходство с голосом", 0, 1, 2, speech.Similarity, "");
                expression = Slider("Выразительность", 0, 1, 2, speech.Style, "");
                speed = Slider("Скорость речи", .7m, 1.2m, 2, speech.Speed, "×");
                speakerBoost = new CheckBox { AutoSize = true, Text = "Speaker boost", Checked = speech.SpeakerBoost };
                Field(null, speakerBoost);
                Field("Модель eleven_multilingual_v2. Каждая непустая строка — отдельный платный запрос.", null);

                generate = Button("Создать озвучки для всех строк", null, true);
                generate.Name = "AssemblyGenerateSpeech"; generate.Click += async (s, e) => await GenerateAll();
                Field(null, generate);

                var actions = Row();
                actions.Controls.Add(Button("Открыть папку озвучки", () => Ui.Open(owner.SpeechFolderAbsolute())));
                Field(null, actions);

                lines = new DataGridView {
                    Name = "AssemblySpeechLines", Dock = DockStyle.Fill, AutoGenerateColumns = false,
                    AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersWidth = 28,
                    BackgroundColor = Theme.Card,
                    DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Color.Black, BackColor = Color.White, WrapMode = DataGridViewTriState.True },
                    AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
                };
                lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "Text", HeaderText = "Текст фразы", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
                lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "Файл", Width = 220, ReadOnly = true });
                root.Controls.Add(lines, 0, 1);
                LoadLines(owner.template.SpeechLines);

                var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
                message = new Label { Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, Text = KeyHint() };
                save = Button("Сохранить и закрыть", Close); save.Dock = DockStyle.Fill;
                bottom.Controls.Add(message, 0, 0); bottom.Controls.Add(save, 1, 0);
                root.Controls.Add(bottom, 0, 2);

                settings.SizeChanged += (s, e) => ResizeFields();
                Shown += async (s, e) => { ResizeFields(); if (!string.IsNullOrWhiteSpace(owner.ElevenLabsApiKey())) await LoadVoices(); };
                FormClosing += OnClosing;
                FormClosed += (s, e) => { lifetime.Cancel(); lifetime.Dispose(); };
                initializing = false;
            }

            string KeyHint() {
                return string.IsNullOrWhiteSpace(owner.ElevenLabsApiKey())
                    ? "Ключ ElevenLabs не найден. Откройте «Настройки» приложения и сохраните ключ там."
                    : "Ключ ElevenLabs подключён из настроек приложения.";
            }

            void UpdateFolderLabel() => folderLabel.Text = owner.SpeechFolderAbsolute();

            void LoadLines(List<AssemblySpeechLine> source) {
                lines.Rows.Clear();
                if (source != null) foreach (var line in source) {
                    var rowIndex = lines.Rows.Add(line.Text ?? "", string.IsNullOrWhiteSpace(line.AudioPath) ? "" : Path.GetFileName(line.AudioPath));
                    lines.Rows[rowIndex].Tag = line.AudioPath ?? "";
                }
                while (lines.Rows.Count < MinimumRows) lines.Rows.Add("", "");
            }

            List<AssemblySpeechLine> ReadLines() {
                var result = new List<AssemblySpeechLine>();
                foreach (DataGridViewRow row in lines.Rows) {
                    if (row.IsNewRow) continue;
                    string text = (Convert.ToString(row.Cells["Text"].Value) ?? "").Trim();
                    string file = Convert.ToString(row.Cells["File"].Value) ?? "";
                    string path = "";
                    if (!string.IsNullOrWhiteSpace(file)) {
                        var existing = (owner.template.SpeechLines ?? new List<AssemblySpeechLine>()).FirstOrDefault(l => string.Equals(Path.GetFileName(l.AudioPath), file, StringComparison.OrdinalIgnoreCase));
                        path = existing?.AudioPath ?? "";
                    }
                    if (text.Length == 0 && path.Length == 0) continue;
                    result.Add(new AssemblySpeechLine { Text = text, AudioPath = path });
                }
                return result;
            }

            string[] GeneratedPaths() {
                return (owner.template.SpeechLines ?? new List<AssemblySpeechLine>())
                    .Select(l => owner.template.Resolve(l.AudioPath)).Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToArray();
            }

            void ImportGenerated(int kind) {
                var paths = GeneratedPaths();
                if (paths.Length == 0) throw new Exception("Сначала создайте озвучки — в таблице должны появиться файлы.");
                owner.ImportPool(kind, paths);
                message.Text = "Добавлено файлов в аудио: " + paths.Length + ".";
            }

            void ImportGeneratedToScene() {
                var paths = GeneratedPaths();
                if (paths.Length == 0) throw new Exception("Сначала создайте озвучки — в таблице должны появиться файлы.");
                owner.ImportSpeechToSceneAudio(paths);
                message.Text = "Добавлено в аудио сцены: " + paths.Length + ".";
            }

            void Field(string caption, Control control) {
                if (caption != null) settings.Controls.Add(new Label { Text = caption, AutoSize = false, Height = caption.Length > 70 ? 42 : 26, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 6, 0, 0) });
                if (control != null) { control.Width = 560; control.Margin = new Padding(0, 0, 0, 6); settings.Controls.Add(control); }
            }

            NumericUpDown Slider(string caption, decimal min, decimal max, int decimals, double value, string unit) {
                var number = Value(min, max, decimals); number.Value = Math.Max(min, Math.Min(max, (decimal)value)); number.Visible = false; Controls.Add(number);
                Field(null, new AssemblySlider(caption, number, unit)); return number;
            }

            static NumericUpDown Value(decimal min, decimal max, int decimals) => new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .1m };

            static Button Button(string text, Action action = null, bool accent = false) {
                var b = new AssemblyStudioButton(text, action, accent);
                b.AutoSize = false; b.Height = 38; b.MinimumSize = new Size(120, 38);
                return b;
            }

            static FlowLayoutPanel Row() => new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };

            string Proxy => WindowsSupport.Unprotect(owner.assemblyPrefs.ProtectedWarmupHttpProxy);

            void SetBusy(bool value) {
                busy = value; settings.Enabled = !value; lines.Enabled = !value; save.Enabled = !value; generate.Enabled = !value;
            }

            ElevenLabsVoiceSettings VoiceSettings() => new ElevenLabsVoiceSettings {
                stability = (double)stability.Value, similarity_boost = (double)similarity.Value, style = (double)expression.Value,
                speed = (double)speed.Value, use_speaker_boost = speakerBoost.Checked
            };

            async Task LoadVoices() {
                SetBusy(true); message.Text = "Получаю список мужских голосов…";
                try {
                    string apiKey = owner.ElevenLabsApiKey();
                    if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("Ключ ElevenLabs не найден. Сохраните его в «Настройки» приложения.");
                    var selected = voices.SelectedItem as ElevenLabsVoice;
                    using (var client = new ElevenLabsClient(apiKey, Proxy)) {
                        var available = await client.ListMaleVoicesAsync(lifetime.Token);
                        voices.Items.Clear();
                        foreach (var voice in available) voices.Items.Add(voice);
                        voices.SelectedItem = available.FirstOrDefault(v => selected != null && v.voice_id == selected.voice_id);
                        if (voices.SelectedIndex < 0 && voices.Items.Count > 0) voices.SelectedIndex = 0;
                        message.Text = available.Count == 0
                            ? "Ключу не доступны голоса с мужской меткой."
                            : "Мужских голосов: " + available.Count + ".";
                    }
                } catch (Exception ex) { message.Text = ex.Message; }
                finally { SetBusy(false); }
            }

            async Task GenerateAll() {
                SetBusy(true);
                try {
                    string apiKey = owner.ElevenLabsApiKey();
                    if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("Ключ ElevenLabs не найден. Сохраните его в «Настройки».");
                    var voice = voices.SelectedItem as ElevenLabsVoice;
                    if (voice == null) throw new Exception("Сначала загрузите и выберите мужской голос.");
                    lines.EndEdit();
                    string folder = owner.SpeechFolderAbsolute();
                    int done = 0, skipped = 0;
                    using (var client = new ElevenLabsClient(apiKey, Proxy)) {
                        for (int i = 0; i < lines.Rows.Count; i++) {
                            var row = lines.Rows[i];
                            if (row.IsNewRow) continue;
                            string text = (Convert.ToString(row.Cells["Text"].Value) ?? "").Trim();
                            if (text.Length == 0) { skipped++; continue; }
                            message.Text = "Строка " + (i + 1) + " · ElevenLabs…";
                            var result = await client.GenerateAsync(text, voice.voice_id, VoiceSettings(), true, lifetime.Token);
                            string stem = "line-" + (i + 1).ToString("00") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                            string path = AssemblyFiles.SafeImportDestination(folder, Path.Combine(folder, stem + ".mp3"));
                            File.WriteAllBytes(path, result.Audio);
                            AssemblySpeechPlan.SaveTiming(path, text, result.SubtitleAlignment);
                            string name = Path.GetFileName(path);
                            string relative = Path.Combine(owner.SpeechFolderRelative(), name);
                            row.Cells["File"].Value = name;
                            row.Tag = relative;
                            done++;
                        }
                    }
                    PersistFromGrid();
                    message.Text = done == 0
                        ? "Нет строк с текстом. Введите фразы в таблицу."
                        : "Готово: " + done + " файлов в «" + folder + "»." + (skipped > 0 ? " Пустых строк: " + skipped + "." : "");
                } catch (Exception ex) { message.Text = "Озвучка не создана: " + ex.Message; }
                finally { SetBusy(false); }
            }

            void PersistFromGrid() {
                var stored = new List<AssemblySpeechLine>();
                for (int i = 0; i < lines.Rows.Count; i++) {
                    var row = lines.Rows[i];
                    if (row.IsNewRow) continue;
                    string text = (Convert.ToString(row.Cells["Text"].Value) ?? "").Trim();
                    string relative = Convert.ToString(row.Tag) ?? "";
                    if (string.IsNullOrWhiteSpace(relative) && !string.IsNullOrWhiteSpace(Convert.ToString(row.Cells["File"].Value)))
                        relative = Path.Combine(owner.SpeechFolderRelative(), Convert.ToString(row.Cells["File"].Value));
                    if (text.Length == 0 && relative.Length == 0) continue;
                    stored.Add(new AssemblySpeechLine { Text = text, AudioPath = relative });
                }
                var voice = voices.SelectedItem as ElevenLabsVoice;
                owner.template.Speech = new AssemblySpeechSettings {
                    VoiceId = voice == null ? "" : voice.voice_id, ModelId = "eleven_multilingual_v2",
                    Stability = (double)stability.Value, Similarity = (double)similarity.Value, Style = (double)expression.Value,
                    Speed = (double)speed.Value, SpeakerBoost = speakerBoost.Checked
                };
                owner.template.SpeechLines = stored;
                owner.Changed(false); owner.Save();
            }

            void OnClosing(object sender, FormClosingEventArgs e) {
                if (busy) { e.Cancel = true; message.Text = "Дождитесь ответа ElevenLabs."; return; }
                try { lines.EndEdit(); PersistFromGrid(); owner.RequestTimingHints(); owner.status.Text = "Озвучка сохранена · " + AssemblyProfile.ShortLabel(owner.studioProfile) + "."; }
                catch (Exception ex) { e.Cancel = true; message.Text = ex.Message; }
            }

            void ResizeFields() {
                foreach (Control control in settings.Controls) control.Width = Math.Max(280, settings.ClientSize.Width - 30);
            }
        }
    }
}
