using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class YouTubeWorkspacePanel {
        Button liveButton;
        System.Windows.Forms.Timer livePulse;
        YouTubeLiveManager live;
        float pulseStep;
        bool stoppingLive;
        void InitializeLive() {
            try {
                live = new YouTubeLiveManager(new LiveStore());
                live.LogLine += LiveLog; live.Changed += LiveChanged;
                AccountRemoval.RemovingYouTube += ValidateLiveAccountRemoval;
            } catch (Exception ex) { liveButton.Enabled = false; AppendLog("ЭФИР · " + ex.Message); }
            livePulse = new System.Windows.Forms.Timer { Interval = 100 };
            livePulse.Tick += (s, e) => { pulseStep += 0.25f; if (live != null && live.HasBusy) grid.InvalidateColumn(grid.Columns["account"].Index); };
            livePulse.Start(); UpdateLiveRows();
        }
        void PaintLiveIndicator(YouTubeChannel channel, Graphics graphics, ref Rectangle rect) {
            var state = live?.View(channel.ChannelId);
            if (state == null || (state.Phase != LivePhase.Live && state.Phase != LivePhase.Reconnecting)) return;
            int alpha = state.Phase == LivePhase.Live ? 140 + (int)(90 * (0.5 + 0.5 * Math.Sin(pulseStep))) : 220;
            using (var brush = new SolidBrush(Color.FromArgb(alpha, state.Phase == LivePhase.Live ? Color.Red : Color.Orange)))
                graphics.FillEllipse(brush, rect.X + 2, rect.Y + 8, 10, 10);
            rect.X += 18; rect.Width -= 18;
        }
        void ValidateLiveAccountRemoval(IEnumerable<YouTubeChannel> channels) {
            foreach (var channel in channels) if (channel != null && live?.View(channel.ChannelId)?.Busy == true)
                throw new InvalidOperationException("Сначала завершите эфир канала «" + channel.Name + "».");
        }
        void AddLiveContextMenu(ContextMenuStrip menu, YouTubeChannel channel) {
            menu.Items.Add("Завершить эфир этого канала", null, async (s, e) => await StopLive(channel.ChannelId)).Enabled = live?.View(channel.ChannelId)?.Busy == true;
            menu.Items.Add("Завершить все эфиры", null, async (s, e) => await StopLive(null)).Enabled = live?.HasBusy == true;
            menu.Items.Add("Настроить эфир / подключить канал", null, (s, e) => ConfigureLive(channel));
            var state = live?.View(channel.ChannelId);
            if (!string.IsNullOrEmpty(state?.Url)) menu.Items.Add("Открыть страницу эфира", null, (s, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(state.Url) { UseShellExecute = true }));
            menu.Items.Add(new ToolStripSeparator());
        }
        public bool HasLiveWork => live?.HasBusy == true;
        public async Task<bool> FinishLiveBeforeClose() {
            if (live == null || !live.HasBusy) return true;
            try { await live.StopAll(); return true; }
            catch (Exception e) {
                LiveLog(e.Message);
                return MessageBox.Show(this, "Передача видео остановлена, но YouTube не подтвердил завершение всех эфиров.\n" + e.Message + "\n\nЗакрыть приложение? Данные для повторного завершения сохранены.", "Прямые эфиры", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            }
        }
        async Task LiveButtonClick() { if (live?.HasBusy == true) await StopLive(null); else StartLive(); }
        void StartLive() {
            if (live == null || stoppingLive) return;
            var channels = GetCheckedChannels();
            if (channels.Count == 0) { MessageBox.Show(this, "Отметьте галочками каналы для эфира.", "YouTube"); return; }
            try {
                EnsureChannelIds();
                using (var dialog = new YouTubeLiveDialog(live, channels)) if (dialog.ShowDialog(FindForm()) == DialogResult.OK) live.Start(channels, dialog.Options);
            } catch (Exception e) { MessageBox.Show(this, e.Message, "Прямые эфиры", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        void ConfigureLive(YouTubeChannel channel = null) {
            if (live == null || stoppingLive) return;
            var channels = channel == null ? GetCheckedChannels() : new List<YouTubeChannel> { channel };
            if (channels.Count == 0) { MessageBox.Show(this, "Отметьте каналы, которые нужно подключить к API.", "YouTube"); return; }
            try { using (var dialog = new YouTubeLiveDialog(live, channels)) {
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK) live.Start(channels, dialog.Options);
            } } catch (Exception e) { MessageBox.Show(this, e.Message, "Прямые эфиры", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        async Task StopLive(string id) {
            if (live == null || stoppingLive) return;
            stoppingLive = true; liveButton.Enabled = false;
            try { if (id == null) await live.StopAll(); else await live.Stop(id); }
            catch (Exception e) { LiveLog(e.Message); MessageBox.Show(this, e.Message, "Завершение эфира", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { stoppingLive = false; if (!IsDisposed) { liveButton.Enabled = true; UpdateLiveRows(); } }
        }
        void LiveChanged() {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(new Action(LiveChanged)); return; }
            UpdateLiveRows();
        }
        void LiveLog(string line) {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(LiveLog), line); return; }
            AppendLog(line);
            try { Directory.CreateDirectory(Store.Root); File.AppendAllText(Path.Combine(Store.Root, "youtube-live.log"), DateTime.Now.ToString("o") + " " + line + Environment.NewLine); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { AppendLog("Не удалось записать лог эфира: " + e.Message); }
        }
        void UpdateLiveRows() {
            if (grid == null || liveButton == null) return;
            liveButton.Text = live?.HasBusy == true ? "Завершить все эфиры" : "Запустить эфир";
            foreach (DataGridViewRow row in grid.Rows) if (row.Tag is YouTubeChannel ch) {
                var state = live?.View(ch.ChannelId);
                row.Cells["live"].Value = state?.Label ?? "—";
                row.Cells["live"].ToolTipText = state == null ? "" : state.Detail + "\n" + state.Url;
                row.Cells["live"].Style.ForeColor = state?.Phase == LivePhase.Live ? Color.Red : state?.Phase == LivePhase.Error || state?.Phase == LivePhase.NeedsCleanup ? Theme.Warning : Theme.TextSecondary;
            }
            grid.Invalidate();
        }
        protected override void Dispose(bool disposing) {
            if (disposing) { livePulse?.Stop(); livePulse?.Dispose(); AccountRemoval.RemovingYouTube -= ValidateLiveAccountRemoval; if (live != null) { live.Changed -= LiveChanged; live.LogLine -= LiveLog; } backend.LogLine -= AppendLog; }
            base.Dispose(disposing);
        }
    }
}
