namespace m3uCrawler.Services.Dispatcharr
{
    public sealed class DispatcharrException : Exception
    {
        public int? StatusCode { get; }
        public string Endpoint { get; }
        public string SanitizedMessage { get; }

        public DispatcharrException(string endpoint, string sanitizedMessage, int? statusCode = null, Exception? inner = null)
            : base($"Dispatcharr call to {endpoint} failed: {sanitizedMessage}", inner)
        {
            Endpoint = endpoint;
            StatusCode = statusCode;
            SanitizedMessage = sanitizedMessage;
        }
    }
}
