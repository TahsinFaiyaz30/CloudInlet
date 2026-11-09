using CloudInlet.Core.B2;

namespace CloudInlet.Tests;

/// <summary>Bounded diagnostics without provider messages, request URLs, headers or credentials.</summary>
internal sealed record CloudPrefixAcceptanceFailure(int Status, string Code)
{
    internal static CloudPrefixAcceptanceFailure? From(Exception error)
    {
        for (var depth = 0; depth < 8 && error is not null; depth++, error = error.InnerException!)
            if (error is B2RequestException provider)
                // B2RequestException.Code comes from ReadErrorAsync's fixed safe-code allowlist.
                return new((int)provider.StatusCode, provider.Code);
        return null;
    }
}
