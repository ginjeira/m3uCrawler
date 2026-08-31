using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using m3uCrawler.Models;

namespace m3uCrawler.Services.Dispatcharr
{
    public interface IDispatcharrStreamClient
    {
        Task<IReadOnlyList<DispatcharrStream>> ListAsync(CancellationToken ct);
        Task<long> CreateAsync(NewStreamRequest request, CancellationToken ct);
        Task DeleteAsync(long streamId, CancellationToken ct);
    }

    public sealed class DispatcharrStreamClient : IDispatcharrStreamClient
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public DispatcharrStreamClient(HttpClient http) { _http = http; }

        public async Task<IReadOnlyList<DispatcharrStream>> ListAsync(CancellationToken ct)
        {
            using var resp = await _http.GetAsync("/api/channels/streams/?page_size=1000", ct);
            if (!resp.IsSuccessStatusCode)
                throw await DispatcharrErrorHelper.ToExceptionAsync(resp, "/api/channels/streams/", HttpMethod.Get, "list-failed", ct);
            var page = await resp.Content.ReadFromJsonAsync<PagedResponse<StreamListDto>>(Json, ct)
                       ?? throw await DispatcharrErrorHelper.ToExceptionAsync(resp, "/api/channels/streams/", HttpMethod.Get, "empty-list-response", ct);
            return page.Results.Select(StreamListDto.ToDomain).ToList();
        }

        public async Task<long> CreateAsync(NewStreamRequest request, CancellationToken ct)
        {
            using var resp = await _http.PostAsJsonAsync("/api/channels/streams/", request.ToPayload(), Json, ct);
            if (!resp.IsSuccessStatusCode)
                throw await DispatcharrErrorHelper.ToExceptionAsync(resp, "/api/channels/streams/", HttpMethod.Post, "create-failed", ct);
            var dto = await resp.Content.ReadFromJsonAsync<StreamListDto>(Json, ct)
                      ?? throw await DispatcharrErrorHelper.ToExceptionAsync(resp, "/api/channels/streams/", HttpMethod.Post, "empty-create-response", ct);
            return dto.Id;
        }

        public async Task DeleteAsync(long streamId, CancellationToken ct)
        {
            using var resp = await _http.DeleteAsync($"/api/channels/streams/{streamId}/", ct);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw await DispatcharrErrorHelper.ToExceptionAsync(resp, $"/api/channels/streams/{streamId}/", HttpMethod.Delete, "delete-failed", ct);
        }
    }

    public sealed class NewStreamRequest
    {
        public string Name { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public long? ChannelGroupId { get; init; }
        public string? TvgId { get; init; }
        public string? LogoUrl { get; init; }
        public bool IsCustom { get; init; } = true;

        public object ToPayload()
        {
            var payload = new Dictionary<string, object?>
            {
                ["name"] = Name,
                ["url"] = Url,
                ["is_custom"] = IsCustom,
            };
            if (ChannelGroupId.HasValue) payload["channel_group"] = ChannelGroupId.Value;
            if (!string.IsNullOrWhiteSpace(TvgId)) payload["tvg_id"] = TvgId;
            if (!string.IsNullOrWhiteSpace(LogoUrl)) payload["logo_url"] = LogoUrl;
            return payload;
        }
    }

    internal sealed class StreamListDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("tvg_id")] public string? TvgId { get; set; }
        [JsonPropertyName("channel_group")] public long? ChannelGroupId { get; set; }
        [JsonPropertyName("m3u_account")] public long? M3uAccountId { get; set; }
        [JsonPropertyName("m3u_account_name")] public string? M3uAccountName { get; set; }
        [JsonPropertyName("is_custom")] public bool IsCustom { get; set; }

        public static DispatcharrStream ToDomain(StreamListDto dto) => new(
            Id: dto.Id,
            Name: dto.Name,
            Url: dto.Url ?? string.Empty,
            TvgId: dto.TvgId,
            GroupName: dto.ChannelGroupId?.ToString(),
            M3uAccountName: dto.M3uAccountName,
            IsCustom: dto.IsCustom,
            IsWorking: true,
            ResponseTimeMs: null);
    }
}
