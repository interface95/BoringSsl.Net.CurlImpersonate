namespace BoringSsl.Net.CurlImpersonate;

using System.Runtime.InteropServices;

public sealed class CurlImpersonateRequest(
    string method,
    Uri uri,
    IReadOnlyList<CurlImpersonateHeader> headers,
    ReadOnlyMemory<byte> body,
    string impersonateTarget,
    int timeoutMs,
    Func<CancellationToken, ValueTask<Stream>>? bodyStreamFactory = null,
    long? bodyLength = null)
{
    public string Method { get; } = method;

    public Uri Uri { get; } = uri;

    public IReadOnlyList<CurlImpersonateHeader> Headers { get; } = headers;

    public ReadOnlyMemory<byte> Body { get; } = body;

    public Func<CancellationToken, ValueTask<Stream>>? BodyStreamFactory { get; } = bodyStreamFactory;

    public long? BodyLength { get; } = bodyLength ?? (body.IsEmpty ? null : body.Length);

    public bool HasBody => BodyStreamFactory is not null || !Body.IsEmpty || BodyLength is > 0;

    public string ImpersonateTarget { get; } = impersonateTarget;

    public int TimeoutMs { get; } = timeoutMs;

    public async ValueTask<Stream?> OpenBodyStreamAsync(CancellationToken cancellationToken)
    {
        if (BodyStreamFactory is not null)
            return await BodyStreamFactory(cancellationToken).ConfigureAwait(false);

        if (Body.IsEmpty)
            return null;

        if (MemoryMarshal.TryGetArray(Body, out var segment) && segment.Array is not null)
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);

        return new MemoryStream(Body.ToArray(), writable: false);
    }
}
