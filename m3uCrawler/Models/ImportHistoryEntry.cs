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

        // Campos aditivos de descoberta (não quebram consumidores existentes).
        public int MessagesAnalyzed { get; set; }
        public int CandidatesFound { get; set; }
        public int PlaylistsDownloaded { get; set; }
        public int CountryMatches { get; set; }
        public int PlaylistsRejected { get; set; }
        public int StreamsExtracted { get; set; }
        public int StreamsTested { get; set; }
        public int StreamsWorking { get; set; }
        public int StreamsFailed { get; set; }
    }
}
