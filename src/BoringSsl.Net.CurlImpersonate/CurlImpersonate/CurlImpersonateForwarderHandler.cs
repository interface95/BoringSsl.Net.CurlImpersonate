namespace BoringSsl.Net.CurlImpersonate;

using System.Net;
using System.Net.Http.Headers;

public sealed class CurlImpersonateForwarderHandler(
    ICurlImpersonateExecutor executor,
    string impersonateTarget,
    int timeoutMs) : HttpMessageHandler
{
    private static readonly HashSet<string> HopByHopResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Proxy-Connection",
        "Keep-Alive",
        "Transfer-Encoding",
        "Upgrade",
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestUri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
        var bodyLength = request.Content?.Headers.ContentLength;
        Func<CancellationToken, ValueTask<Stream>>? bodyStreamFactory = null;
        if (request.Content is not null)
        {
            var content = request.Content;
            bodyStreamFactory = ct => ReadContentAsStreamAsync(content, ct);
        }

        var headers = BuildRequestHeaders(request);
        var curlRequest = new CurlImpersonateRequest(
            method: request.Method.Method,
            uri: requestUri,
            headers: headers,
            body: ReadOnlyMemory<byte>.Empty,
            impersonateTarget: impersonateTarget,
            timeoutMs: timeoutMs,
            bodyStreamFactory: bodyStreamFactory,
            bodyLength: bodyLength);

        if (executor is ICurlImpersonateStreamingExecutor streamingExecutor)
        {
            var streamingResponse = await streamingExecutor.ExecuteStreamingAsync(curlRequest, cancellationToken).ConfigureAwait(false);
            return BuildStreamingHttpResponseMessage(request, streamingResponse);
        }

        var curlResponse = await executor.ExecuteAsync(curlRequest, cancellationToken).ConfigureAwait(false);
        return BuildBufferedHttpResponseMessage(request, curlResponse);
    }

    private static IReadOnlyList<CurlImpersonateHeader> BuildRequestHeaders(HttpRequestMessage request)
    {
        var headers = new List<CurlImpersonateHeader>();

        foreach (var header in request.Headers)
        {
            // 将多值 header 合并为单个值(逗号+空格分隔)，
            // 避免 curl 把同名 header 拆成多个 h2 pseudo-entry。
            // 如果 .NET 按 RFC 拆分了 "a,b" 成两个值，我们需要合回去。
            var combinedValue = string.Join(", ", header.Value);
            headers.Add(new CurlImpersonateHeader(header.Key, combinedValue));
        }

        if (!string.IsNullOrWhiteSpace(request.Headers.Host))
        {
            headers.Add(new CurlImpersonateHeader("Host", request.Headers.Host));
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                foreach (var value in header.Value)
                {
                    headers.Add(new CurlImpersonateHeader(header.Key, value));
                }
            }
        }

        return headers;
    }

    private static HttpResponseMessage BuildBufferedHttpResponseMessage(HttpRequestMessage request, CurlImpersonateResponse curlResponse)
    {
        var response = new HttpResponseMessage((HttpStatusCode)curlResponse.StatusCode)
        {
            RequestMessage = request,
            ReasonPhrase = curlResponse.ReasonPhrase,
            Content = new ByteArrayContent(curlResponse.Body),
            Version = curlResponse.ProtocolVersion,
        };

        CopyResponseHeaders(response, curlResponse.Headers, skipContentLength: true);
        response.Content.Headers.ContentLength = curlResponse.Body.Length;
        if (response.Content.Headers.ContentType is null &&
            !response.Content.Headers.Contains("Content-Type") &&
            !response.Headers.Contains("Content-Type"))
        {
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        return response;
    }

    private static HttpResponseMessage BuildStreamingHttpResponseMessage(
        HttpRequestMessage request,
        CurlImpersonateStreamingResponse streamingResponse)
    {
        var response = new HttpResponseMessage((HttpStatusCode)streamingResponse.StatusCode)
        {
            RequestMessage = request,
            ReasonPhrase = streamingResponse.ReasonPhrase,
            Content = new StreamContent(streamingResponse.Body),
            Version = streamingResponse.ProtocolVersion,
        };

        // 流式路径下不透传 Content-Length，避免上游压缩/解码或分块差异导致 Kestrel 长度校验失败。
        CopyResponseHeaders(response, streamingResponse.Headers, skipContentLength: true);
        if (response.Content.Headers.ContentType is null &&
            !response.Content.Headers.Contains("Content-Type") &&
            !response.Headers.Contains("Content-Type"))
        {
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        return response;
    }

    private static void CopyResponseHeaders(
        HttpResponseMessage response,
        IReadOnlyList<CurlImpersonateHeader> headers,
        bool skipContentLength)
    {
        foreach (var header in headers)
        {
            if (ShouldSkipResponseHeader(header.Name, skipContentLength))
            {
                continue;
            }

            if (!response.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                response.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }
    }

    private static bool ShouldSkipResponseHeader(string name, bool skipContentLength)
    {
        if (HopByHopResponseHeaders.Contains(name))
        {
            return true;
        }

        if (skipContentLength && string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 读取 HttpContent body stream。
    /// YARP 的 StreamCopyHttpContent 不支持 ReadAsStreamAsync（抛 NotImplementedException），
    /// 此处 catch 后 fallback 到 CopyToAsync 推模式。
    /// </summary>
    private static async ValueTask<Stream> ReadContentAsStreamAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NotImplementedException)
        {
            // YARP StreamCopyHttpContent 只支持 CopyToAsync（推模式）。
            var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            ms.Position = 0;
            return ms;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && executor is IDisposable disposableExecutor)
        {
            disposableExecutor.Dispose();
        }

        base.Dispose(disposing);
    }
}
