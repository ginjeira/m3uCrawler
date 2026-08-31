namespace m3uCrawler.Services.Dispatcharr
{
    public sealed class DispatcharrException : Exception
    {
        public int? StatusCode { get; }
        public string Endpoint { get; }
        public string SanitizedMessage { get; }
        public string? Method { get; }

        public DispatcharrException(string endpoint, string sanitizedMessage, int? statusCode = null,
                                   string? method = null, Exception? inner = null)
            : base(BuildMessage(endpoint, sanitizedMessage, statusCode, method), inner)
        {
            Endpoint = endpoint;
            StatusCode = statusCode;
            SanitizedMessage = sanitizedMessage;
            Method = method;
        }

        private static string BuildMessage(string endpoint, string sanitizedMessage, int? statusCode, string? method)
        {
            var verb = string.IsNullOrWhiteSpace(method) ? string.Empty : $"{method!.ToUpperInvariant()} ";
            var status = statusCode.HasValue ? $"HTTP {statusCode.Value}" : "HTTP ?";
            var body = string.IsNullOrEmpty(sanitizedMessage) ? "(empty body)" : sanitizedMessage;
            return $"Dispatcharr call to {verb}{endpoint} failed: {status} — {body}";
        }
    }
}
