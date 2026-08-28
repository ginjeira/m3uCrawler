using System;

namespace m3uCrawler.Models
{
    public class ImportHistoryEntry
    {
        public DateTime Timestamp { get; set; }
        public string Mode { get; set; } = "";
        public string SearchTerm { get; set; } = "";
        public int HistoryHours { get; set; }
        public int MaxStreams { get; set; }
        public int NewFunctionalCount { get; set; }
        public int ExistingRetestedCount { get; set; }
        public int ExistingStillWorkingCount { get; set; }
        public int FinalPlaylistCount { get; set; }
        public string TempPlaylistFileName { get; set; } = "playlist_temp.m3u";
        public string MainPlaylistFileName { get; set; } = "playlist.m3u";
    }
}
