using System.Collections.Generic;
using System.Linq;

namespace VideoBatch {
    /// <summary>Channel-level thumbnail and per-item Thumbnail can diverge; upload jobs must see one resolved path.</summary>
    public static class YouTubeThumbnailSync {
        public static string ForItem(YouTubeChannel channel, YouTubeItem item, int itemIndexZeroBased) {
            if (item != null && !string.IsNullOrWhiteSpace(item.Thumbnail)) return item.Thumbnail.Trim();
            if (channel != null && !string.IsNullOrWhiteSpace(channel.Thumbnail)) return channel.Thumbnail.Trim();
            return "";
        }

        public static void Apply(YouTubeChannel channel) {
            if (channel == null) return;
            if (channel.Items == null) channel.Items = new List<YouTubeItem>();
            string thumb = channel.Items.Select(i => i?.Thumbnail).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim()
                ?? (channel.Thumbnail ?? "").Trim();
            if (string.IsNullOrWhiteSpace(thumb)) return;
            channel.Thumbnail = thumb;
            if (channel.Items.Count > 0 && channel.Items[0] != null && string.IsNullOrWhiteSpace(channel.Items[0].Thumbnail))
                channel.Items[0].Thumbnail = thumb;
        }
    }
}
