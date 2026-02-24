namespace BoringSsl.Net.CurlImpersonate;

using System.Net;

public static class CurlResponseHeaderParser
{
    public static (int StatusCode, string ReasonPhrase, Version? ProtocolVersion, IReadOnlyList<CurlImpersonateHeader> Headers) Parse(IReadOnlyList<string> rawHeaderLines)
    {
        var statusCode = 200;
        var reasonPhrase = "OK";
        Version? protocolVersion = null;
        var headers = new List<CurlImpersonateHeader>(rawHeaderLines.Count);

        foreach (var line in rawHeaderLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                headers.Clear();
                if (TryParseStatusLine(line, out var parsedProtocolVersion, out var parsedStatusCode, out var parsedReasonPhrase))
                {
                    protocolVersion = parsedProtocolVersion;
                    statusCode = parsedStatusCode;
                    reasonPhrase = parsedReasonPhrase;
                }

                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            var name = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            headers.Add(new CurlImpersonateHeader(name, value));
        }

        return (statusCode, reasonPhrase, protocolVersion, headers);
    }

    private static bool TryParseStatusLine(
        string line,
        out Version? protocolVersion,
        out int statusCode,
        out string reasonPhrase)
    {
        protocolVersion = null;
        statusCode = 0;
        reasonPhrase = string.Empty;

        var span = line.AsSpan().Trim();
        var firstSpaceIndex = span.IndexOf(' ');
        if (firstSpaceIndex <= 0 || firstSpaceIndex >= span.Length - 1)
        {
            return false;
        }

        protocolVersion = ParseProtocolVersion(span[..firstSpaceIndex]);
        span = span[(firstSpaceIndex + 1)..].TrimStart();
        var secondSpaceIndex = span.IndexOf(' ');
        var statusCodeSpan = secondSpaceIndex >= 0 ? span[..secondSpaceIndex] : span;
        if (!int.TryParse(statusCodeSpan, out statusCode))
        {
            return false;
        }

        if (secondSpaceIndex < 0)
        {
            return true;
        }

        reasonPhrase = span[(secondSpaceIndex + 1)..].TrimStart().ToString();
        return true;
    }

    private static Version? ParseProtocolVersion(ReadOnlySpan<char> token)
    {
        const string prefix = "HTTP/";
        if (!token.StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return null;

        var suffix = token[prefix.Length..];
        if (suffix.Length == 1 && suffix[0] == '2')
            return HttpVersion.Version20;

        if (suffix.Length == 1 && suffix[0] == '3')
            return HttpVersion.Version30;

        return Version.TryParse(suffix.ToString(), out var parsed) ? parsed : null;
    }
}
