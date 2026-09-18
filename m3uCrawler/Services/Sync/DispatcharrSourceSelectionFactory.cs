using m3uCrawler.Models;
using m3uCrawler.Services.SourceSelection;

namespace m3uCrawler.Services.Sync
{
    /// <summary>
    /// PHASE 13 (Wave 13-6) — Constrói o artefacto
    /// <see cref="DispatcharrSourceSelection"/> a partir do resultado do
    /// <see cref="SourceSelectionStage"/>. Projeção pura: não recalcula a
    /// selecção nem toca no catálogo.
    /// </summary>
    public static class DispatcharrSourceSelectionFactory
    {
        private const string PolicyScopeOverride = "override";
        private const string PolicyScopeGlobal = "global";
        private const string PolicyScopeDefault = "default";

        public static DispatcharrSourceSelection FromStageResult(
            SourceSelectionStageResult result,
            SourceSelectionPolicySet? policies,
            DateTime? nowUtc = null)
        {
            ArgumentNullException.ThrowIfNull(result);

            var channels = result.Channels.Select(c => new ChannelSourceSelection
            {
                CanonicalChannelKey = c.CanonicalChannelKey ?? string.Empty,
                CanonicalChannelId = c.CanonicalChannelId,
                PolicyScope = policies is null
                    ? null
                    : policies.HasOverride(c.CanonicalChannelKey)
                        ? PolicyScopeOverride
                        : policies.HasExplicitGlobal
                            ? PolicyScopeGlobal
                            : PolicyScopeDefault,
                CandidateCount = c.CandidateCount,
                RejectedCount = c.Rejected.Count,
                Selected = c.Selected.Select(s => new SelectedStreamSelection
                {
                    StreamUrl = s.Candidate.StreamUrl,
                    Rank = s.Rank,
                    Provider = s.Candidate.Provider.Key,
                    Reason = s.Reason,
                    SourceId = s.Candidate.SourceId,
                }).ToList(),
            }).ToList();

            var candidates = 0;
            var selected = 0;
            var rejected = 0;
            foreach (var channel in result.Channels)
            {
                candidates += channel.CandidateCount;
                selected += channel.Selected.Count;
                rejected += channel.Rejected.Count;
            }

            var ambiguous = result.AmbiguousStreams.Count;

            return new DispatcharrSourceSelection
            {
                GeneratedAtUtc = (nowUtc ?? DateTime.UtcNow).ToString("o"),
                Channels = channels,
                Counts = new SelectionCounts
                {
                    Channels = result.Channels.Count,
                    Candidates = candidates,
                    Selected = selected,
                    Rejected = rejected,
                    Unmatched = Math.Max(0, result.Unmatched.Count - ambiguous),
                    Ambiguous = ambiguous,
                },
            };
        }
    }
}
