using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using m3uCrawler.Models;

namespace m3uCrawler.Services.Dispatcharr
{
    public interface IDispatcharrM3UClient
    {
        Task<IReadOnlyList<DispatcharrChannelGroup>> ListGroupsAsync(CancellationToken ct);
        Task<long> CreateGroupAsync(string name, CancellationToken ct);
        Task<string?> GetVersionAsync(CancellationToken ct);
    }

    public sealed class DispatcharrM3UClient : IDispatcharrM3UClient
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public DispatcharrM3UClient(HttpClient http) { _http = http; }

        public async Task<IReadOnlyList<DispatcharrChannelGroup>> ListGroupsAsync(CancellationToken ct)
        {
            using var resp = await _http.GetAsync("/api/channels/groups/?page_size=1000", ct);
            if (!resp.IsSuccessStatusCode)
                throw new DispatcharrException("/api/channels/groups/", "list-failed", (int)resp.StatusCode);
            var page = await resp.Content.ReadFromJsonAsync<PagedResponse<GroupDto>>(Json, ct)
                       ?? throw new DispatcharrException("/api/channels/groups/", "empty-list-response", (int)resp.StatusCode);
            return page.Results.Select(g => new DispatcharrChannelGroup(g.Id, g.Name)).ToList();
        }

        public async Task<long> CreateGroupAsync(string name, CancellationToken ct)
        {
            using var resp = await _http.PostAsJsonAsync("/api/channels/groups/", new { name }, Json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new DispatcharrException("/api/channels/groups/", CredentialSanitizer.SanitizeUrl(body), (int)resp.StatusCode);
            }
            var dto = await resp.Content.ReadFromJsonAsync<GroupDto>(Json, ct)
                      ?? throw new DispatcharrException("/api/channels/groups/", "empty-create-response", (int)resp.StatusCode);
            return dto.Id;
        }

        public async Task<string?> GetVersionAsync(CancellationToken ct)
        {
            try
            {
                using var resp = await _http.GetAsync("/api/core/version/", ct);
                if (!resp.IsSuccessStatusCode) return null;
                var doc = await resp.Content.ReadFromJsonAsync<VersionDto>(Json, ct);
                return doc?.Version;
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed class GroupDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    internal sealed class VersionDto
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
    }
}
