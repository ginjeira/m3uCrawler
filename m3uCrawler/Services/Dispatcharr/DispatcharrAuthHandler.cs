using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace m3uCrawler.Services.Dispatcharr
{
    public sealed class DispatcharrAuthHandler : DelegatingHandler
    {
        private readonly DispatcharrAuthState _state;
        private readonly DispatcharrLoginApi _login;
        private readonly bool _useApiKey;

        public DispatcharrAuthHandler(DispatcharrAuthState state, DispatcharrLoginApi login)
            : this(state, login, useApiKey: false)
        {
        }

        public DispatcharrAuthHandler(DispatcharrAuthState state, DispatcharrLoginApi login, bool useApiKey)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _login = login ?? throw new ArgumentNullException(nameof(login));
            _useApiKey = useApiKey;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AttachToken(request);

            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return response;

            if (_useApiKey)
            {
                response.Dispose();
                return MakeUnauthorizedResponse(request);
            }

            if (request.Headers.Contains("X-Dispatcharr-Auth-Retry"))
                return response;

            response.Dispose();

            bool refreshed = await _state.RefreshAsync(_login, cancellationToken);
            if (!refreshed)
            {
                bool logged = await _state.LoginAsync(_login, cancellationToken);
                if (!logged)
                    return MakeUnauthorizedResponse(request);
            }

            var retry = await CloneAsync(request, cancellationToken);
            retry.Headers.Add("X-Dispatcharr-Auth-Retry", "1");
            AttachToken(retry);
            return await base.SendAsync(retry, cancellationToken);
        }

        private void AttachToken(HttpRequestMessage request)
        {
            var token = _state.AccessToken;
            if (string.IsNullOrWhiteSpace(token))
                return;

            if (_useApiKey)
            {
                request.Headers.Remove("X-API-Key");
                request.Headers.Add("X-API-Key", token);
                request.Headers.Authorization = null;
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source, CancellationToken ct)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri);
            if (source.Content != null)
            {
                var ms = new MemoryStream();
                await source.Content.CopyToAsync(ms, ct);
                ms.Position = 0;
                clone.Content = new StreamContent(ms);
                foreach (var h in source.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            foreach (var h in source.Headers)
                clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return clone;
        }

        private static HttpResponseMessage MakeUnauthorizedResponse(HttpRequestMessage req)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                RequestMessage = req,
            };
            return resp;
        }
    }

    public sealed class DispatcharrAuthState
    {
        private readonly object _gate = new();
        public string? AccessToken { get; private set; }
        public string? RefreshToken { get; private set; }

        public void Set(string? access, string? refresh)
        {
            lock (_gate)
            {
                AccessToken = access;
                RefreshToken = refresh;
            }
        }

        public Task<bool> RefreshAsync(DispatcharrLoginApi login, CancellationToken ct)
        {
            var rt = RefreshToken;
            if (string.IsNullOrWhiteSpace(rt)) return Task.FromResult(false);
            return Task.Run(async () =>
            {
                try
                {
                    var resp = await login.RefreshAsync(rt, ct);
                    Set(resp.Access, resp.Refresh ?? rt);
                    return !string.IsNullOrWhiteSpace(resp.Access);
                }
                catch
                {
                    return false;
                }
            }, ct);
        }

        public async Task<bool> LoginAsync(DispatcharrLoginApi login, CancellationToken ct)
        {
            var resp = await login.LoginAsync(ct);
            if (resp == null || string.IsNullOrWhiteSpace(resp.Access)) return false;
            Set(resp.Access, resp.Refresh);
            return true;
        }
    }

    public class DispatcharrLoginApi
    {
        protected readonly HttpClient _client;

        public DispatcharrLoginApi(HttpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public virtual async Task<LoginResponse?> LoginAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
                return null;
            var url = "/api/accounts/auth/login/";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { username = Username, password = Password })
            };
            using var resp = await _client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct);
        }

        public virtual async Task<LoginResponse> RefreshAsync(string refresh, CancellationToken ct)
        {
            var url = "/api/accounts/token/refresh/";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { refresh })
            };
            using var resp = await _client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return (await resp.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct))
                   ?? throw new DispatcharrException(url, "empty-refresh-response", (int)resp.StatusCode);
        }

        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? ApiKey { get; set; }

        public void ApplyApiKey(HttpRequestMessage req)
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        }
    }

    public sealed class LoginResponse
    {
        [JsonPropertyName("access")] public string Access { get; set; } = string.Empty;
        [JsonPropertyName("refresh")] public string? Refresh { get; set; }
    }
}
