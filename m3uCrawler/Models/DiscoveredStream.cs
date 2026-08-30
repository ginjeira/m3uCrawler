namespace m3uCrawler.Models
{
    public enum SyncOutcome
    {
        NewChannel,
        ExistingUnchanged,
        ExistingReassigned,
        ExistingReordered,
        NewStream,
        Removed,
        Skipped,
        Ambiguous,
        Unchanged,
        Failed,
    }

    public sealed record DiscoveredStream(
        M3uStream Original,
        string Provider,
        string Source)
    {
        public string Title => Original.Title;
        public string Url => Original.Url;
        public string Group => Original.Group;
        public bool IsWorking => Original.IsWorking;
        public double ResponseTime => Original.ResponseTime;
    }

    public sealed record DispatcharrChannel(
        long Id,
        string Name,
        string? GroupName,
        double? ChannelNumber,
        string? TvgId,
        IReadOnlyList<long> StreamIds);

    public sealed record DispatcharrStream(
        long Id,
        string Name,
        string Url,
        string? TvgId,
        string? GroupName,
        string? M3uAccountName,
        bool IsCustom,
        bool IsWorking,
        double? ResponseTimeMs);

    public sealed record DispatcharrChannelGroup(long Id, string Name);

    public sealed record DispatcharrState(
        IReadOnlyList<DispatcharrChannel> Channels,
        IReadOnlyList<DispatcharrStream> Streams,
        IReadOnlyList<DispatcharrChannelGroup> Groups,
        string? Version);
}
