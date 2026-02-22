namespace BoringSsl.Net.CurlImpersonate;

public sealed class CurlImpersonateRequest(
    string method,
    Uri uri,
    IReadOnlyList<CurlImpersonateHeader> headers,
    ReadOnlyMemory<byte> body,
    string impersonateTarget,
    int timeoutMs)
{
    public string Method { get; } = method;

    public Uri Uri { get; } = uri;

    public IReadOnlyList<CurlImpersonateHeader> Headers { get; } = headers;

    public ReadOnlyMemory<byte> Body { get; } = body;

    public string ImpersonateTarget { get; } = impersonateTarget;

    public int TimeoutMs { get; } = timeoutMs;
}
