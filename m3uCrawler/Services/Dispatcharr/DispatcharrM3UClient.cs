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
                throw await DispatcharrErrorHelper.ToExceptionAsync(resp, "/api/channels/groups/", HttpMethod.Get, "list-failed", ct);

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            IReadOnlyList<GroupDto> items;
            if (LooksLikeJsonArray(bytes))
            {
                items = JsonSerializer.Deserialize<List<GroupDto>>(bytes, Json)
                        ?? throw await DispatcharrErrorHelper.ToExceptionAsync(resp, "/api/channels/groups/", HttpMethod.Get, "empty-list-response", ct);
            }
            else
            {
                var page = JsonSerializer.Deserialize<PagedResponse<GroupDto>>(bytes, Json)
                           ?? throw await DispatcharrErrorHelper.ToExceptionAsync(resp, "/api/channels/groups/", HttpMethod.Get, "empty-list-response", ct);
                items = page.Results;
            }

            return items.Select(g => new DispatcharrChannelGroup(g.Id, g.Name)).ToList();
        }

        private static bool LooksLikeJsonArray(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n') continue;
                return b == (byte)'[';
            }
            return false;
        }

        public async Task<long> CreateGroupAsync(string name, CancellationToken ct)
        {
            using var resp = await _http.PostAsJsonAsync("/api/channels/groups/", new { name }, Json, ct);
            if (!resp.IsSuccessStatusCode)
                throw await DispatcharrErrorHelper.ToExceptionAsync(resp, "/api/channels/groups/", HttpMethod.Post, "create-failed", ct);
            var dto = await resp.Content.ReadFromJsonAsync<GroupDto>(Json, ct)
                      ?? throw await DispatcharrErrorHelper.ToExceptionAsync(resp, "/api/channels/groups/", HttpMethod.Post, "empty-create-response", ct);
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

    internal static class DispatcharrErrorHelper
    {
        public static async Task<DispatcharrException> ToExceptionAsync(
            HttpResponseMessage resp, string endpoint, HttpMethod method, string fallbackReason, CancellationToken ct)
        {
            string body = string.Empty;
            try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* keep body empty */ }
            var sanitized = string.IsNullOrEmpty(body) ? fallbackReason : CredentialSanitizer.SanitizeUrl(body);
            return new DispatcharrException(endpoint, sanitized, (int)resp.StatusCode, method.Method, inner: null);
        }
    }
}
