namespace FatimaTTS.Models;

public class InworldApiException : Exception
{
    public int HttpStatusCode { get; }
    public bool IsRetryable { get; }
    public bool IsAuthError { get; }

    public InworldApiException(string message, int httpStatusCode)
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
        IsRetryable    = httpStatusCode is 429 or 500 or 502 or 503 or 504;
        IsAuthError    = httpStatusCode is 401 or 403;
    }

    public static string FriendlyMessage(int status, string apiMessage) => status switch
    {
        401 => "Invalid API key. Please check your Inworld credentials in Settings.",
        403 => "Access denied. Your API key may not have TTS permissions.",
        429 => "Rate limit exceeded. The request will be retried automatically.",
        400 => $"Bad request: {apiMessage}",
        >= 500 => "Inworld service is temporarily unavailable. Retrying…",
        _ => $"API error ({status}): {apiMessage}"
    };
}
