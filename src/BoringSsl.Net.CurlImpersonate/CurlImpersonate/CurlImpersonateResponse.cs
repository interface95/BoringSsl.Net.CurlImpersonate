namespace BoringSsl.Net.CurlImpersonate;

public sealed class CurlImpersonateResponse(
    int statusCode,
    string reasonPhrase,
    IReadOnlyList<CurlImpersonateHeader> headers,
    byte[] body,
    Version protocolVersion)
{
    public int StatusCode { get; } = statusCode;

    public string ReasonPhrase { get; } = reasonPhrase;

    public IReadOnlyList<CurlImpersonateHeader> Headers { get; } = headers;

    public byte[] Body { get; } = body;

    public Version ProtocolVersion { get; } = protocolVersion;
}
