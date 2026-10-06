using System.Net;

namespace CloudInlet.Core.B2;

/// <summary>An API failure with safe diagnostic fields. Server messages and tokens are never retained.</summary>
public sealed class B2RequestException : IOException
{
    public HttpStatusCode StatusCode { get; }
    public string Code { get; }

    internal B2RequestException(HttpStatusCode statusCode, string code)
        : base($"Backblaze B2 rejected the request ({(int)statusCode}, {code}).")
    {
        StatusCode = statusCode;
        Code = code;
    }
}
