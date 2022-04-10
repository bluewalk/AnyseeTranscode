using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Net.Bluewalk.AnyseeTranscode
{
    public class M3UPlaylist : List<M3UPlaylistEntry>
    {
        public IList<M3UPlaylistEntry> Ordered => this.OrderBy(e => e.Group)
            .ThenBy(e => e.Title)
            .ToList();

        public M3UPlaylist() { }

        public M3UPlaylist(IEnumerable<M3UPlaylistEntry> entries)
        {
            AddRange(entries);
        }

        public override string ToString()
        {
            var playlist = new StringBuilder();
            playlist.AppendLine("#EXTM3U");
            playlist.AppendLine("#EXT-X-VERSION:3");

            ForEach(e =>
            {
                var attributes = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(e.Group))
                    attributes.Add("group-title", e.Group);
                if (e.TvgLogo != default)
                    attributes.Add("tvg-logo", Uri.UnescapeDataString(e.TvgLogo.ToString()));
                if (!string.IsNullOrEmpty(e.TvgName))
                    attributes.Add("tvg-name", e.TvgName);

                playlist.AppendLine(
                    $"#EXTINF:0 {string.Join(" ", attributes.Select(a => $"{a.Key}=\"{a.Value}\""))},{e.Title}");
                playlist.AppendLine(e.Url.ToString());
            });

            return playlist.ToString();
        }
    }

    public class M3UPlaylistEntry
    {
        public string Group { get; set; }
        public Uri TvgLogo { get; set; }
        public string TvgName { get; set; }
        public string Title { get; set; }
        public Uri Url { get; set; }
    }
}