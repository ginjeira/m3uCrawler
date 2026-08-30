using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using m3uCrawler.Models;

namespace m3uCrawler.Services.Dispatcharr
{
    public interface IDispatcharrChannelClient
    {
        Task<IReadOnlyList<DispatcharrChannel>> ListAsync(CancellationToken ct);
        Task<long> CreateAsync(NewChannelRequest request, CancellationToken ct);
        Task UpdateStreamsAsync(long channelId, IReadOnlyList<long> orderedStreamIds, CancellationToken ct);
        Task<IReadOnlyList<long>> ListStreamIdsAsync(long channelId, CancellationToken ct);
    }

    public sealed class DispatcharrChannelClient : IDispatcharrChannelClient
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public DispatcharrChannelClient(HttpClient http) { _http = http; }

        public async Task<IReadOnlyList<DispatcharrChannel>> ListAsync(CancellationToken ct)
        {
            using var resp = await _http.GetAsync("/api/channels/channels/?page_size=1000", ct);
            if (!resp.IsSuccessStatusCode)
                throw new DispatcharrException("/api/channels/channels/", "list-failed", (int)resp.StatusCode);
            var page = await resp.Content.ReadFromJsonAsync<PagedResponse<ChannelDto>>(Json, ct)
                       ?? throw new DispatcharrException("/api/channels/channels/", "empty-list-response", (int)resp.StatusCode);
            return page.Results.Select(ChannelDto.ToDomain).ToList();
        }

        public async Task<long> CreateAsync(NewChannelRequest request, CancellationToken ct)
        {
            using var resp = await _http.PostAsJsonAsync("/api/channels/channels/", request.ToPayload(), Json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new DispatcharrException("/api/channels/channels/", CredentialSanitizer.SanitizeUrl(body), (int)resp.StatusCode);
            }
            var dto = await resp.Content.ReadFromJsonAsync<ChannelDto>(Json, ct)
                      ?? throw new DispatcharrException("/api/channels/channels/", "empty-create-response", (int)resp.StatusCode);
            return dto.Id;
        }

        public async Task UpdateStreamsAsync(long channelId, IReadOnlyList<long> orderedStreamIds, CancellationToken ct)
        {
            using var resp = await _http.PatchAsync($"/api/channels/channels/{channelId}/",
                JsonContent.Create(new { streams = orderedStreamIds.ToArray() }), ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new DispatcharrException($"/api/channels/channels/{channelId}/", CredentialSanitizer.SanitizeUrl(body), (int)resp.StatusCode);
            }
        }

        public async Task<IReadOnlyList<long>> ListStreamIdsAsync(long channelId, CancellationToken ct)
        {
            using var resp = await _http.GetAsync($"/api/channels/channels/{channelId}/streams/", ct);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<long>();
            var streams = await resp.Content.ReadFromJsonAsync<List<StreamDto>>(Json, ct);
            if (streams == null) return Array.Empty<long>();
            return streams.Select(s => s.Id).ToList();
        }
    }

    public sealed class NewChannelRequest
    {
        public string Name { get; init; } = string.Empty;
        public long? ChannelGroupId { get; init; }
        public double? ChannelNumber { get; init; }
        public string? TvgId { get; init; }
        public IReadOnlyList<long> Streams { get; init; } = Array.Empty<long>();

        public object ToPayload()
        {
            var payload = new Dictionary<string, object?>
            {
                ["name"] = Name,
                ["streams"] = Streams.ToArray(),
            };
            if (ChannelGroupId.HasValue) payload["channel_group_id"] = ChannelGroupId.Value;
            if (ChannelNumber.HasValue) payload["channel_number"] = ChannelNumber.Value;
            if (!string.IsNullOrWhiteSpace(TvgId)) payload["tvg_id"] = TvgId;
            return payload;
        }
    }

    internal sealed class ChannelDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("channel_group_id")] public long? ChannelGroupId { get; set; }
        [JsonPropertyName("channel_number")] public double? ChannelNumber { get; set; }
        [JsonPropertyName("tvg_id")] public string? TvgId { get; set; }
        [JsonPropertyName("streams")] public List<long> Streams { get; set; } = new();

        public static DispatcharrChannel ToDomain(ChannelDto dto) => new(
            Id: dto.Id,
            Name: dto.Name,
            GroupName: null,
            ChannelNumber: dto.ChannelNumber,
            TvgId: dto.TvgId,
            StreamIds: dto.Streams);
    }

    internal sealed class StreamDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    internal sealed class PagedResponse<T>
    {
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("next")] public string? Next { get; set; }
        [JsonPropertyName("previous")] public string? Previous { get; set; }
        [JsonPropertyName("results")] public List<T> Results { get; set; } = new();
    }
}
