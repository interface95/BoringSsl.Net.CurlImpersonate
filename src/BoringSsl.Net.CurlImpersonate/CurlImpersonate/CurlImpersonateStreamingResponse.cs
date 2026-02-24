namespace BoringSsl.Net.CurlImpersonate;

public sealed class CurlImpersonateStreamingResponse(
    int statusCode,
    string reasonPhrase,
    IReadOnlyList<CurlImpersonateHeader> headers,
    Stream body,
    Version protocolVersion)
{
    public int StatusCode { get; } = statusCode;

    public string ReasonPhrase { get; } = reasonPhrase;

    public IReadOnlyList<CurlImpersonateHeader> Headers { get; } = headers;

    public Stream Body { get; } = body;

    public Version ProtocolVersion { get; } = protocolVersion;
}
