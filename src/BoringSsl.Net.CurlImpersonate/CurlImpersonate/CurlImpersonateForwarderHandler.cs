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
        var body = request.Content is null
            ? ReadOnlyMemory<byte>.Empty
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var headers = BuildRequestHeaders(request);
        var curlRequest = new CurlImpersonateRequest(
            method: request.Method.Method,
            uri: requestUri,
            headers: headers,
            body: body,
            impersonateTarget: impersonateTarget,
            timeoutMs: timeoutMs);

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
            foreach (var value in header.Value)
            {
                headers.Add(new CurlImpersonateHeader(header.Key, value));
            }
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
        };

        CopyResponseHeaders(response, streamingResponse.Headers, skipContentLength: false);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && executor is IDisposable disposableExecutor)
        {
            disposableExecutor.Dispose();
        }

        base.Dispose(disposing);
    }
}
