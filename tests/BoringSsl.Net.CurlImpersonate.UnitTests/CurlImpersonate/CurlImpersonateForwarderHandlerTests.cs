namespace BoringSsl.Net.CurlImpersonate.UnitTests.CurlImpersonate;

using BoringSsl.Net.CurlImpersonate;
using System.Net;
using System.Text;

public sealed class CurlImpersonateForwarderHandlerTests
{
    [Fact]
    public async Task SendAsync_MapsRequestToCurlExecutor_AndBuildsResponse()
    {
        var fakeExecutor = new FakeCurlImpersonateExecutor(new CurlImpersonateResponse(
            statusCode: 201,
            reasonPhrase: "Created",
            headers:
            [
                new CurlImpersonateHeader("Content-Type", "application/json"),
                new CurlImpersonateHeader("X-Upstream", "ok"),
                new CurlImpersonateHeader("Connection", "close"),
                new CurlImpersonateHeader("Content-Length", "999"),
            ],
            body: Encoding.UTF8.GetBytes("{\"ok\":true}")));

        using var handler = new CurlImpersonateForwarderHandler(fakeExecutor, impersonateTarget: "chrome136", timeoutMs: 45_000);
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/v1/test?x=1")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello")),
        };
        request.Headers.TryAddWithoutValidation("X-Trace-Id", "trace-1");
        request.Headers.Host = "example.com";
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        using var response = await invoker.SendAsync(request, CancellationToken.None);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Created", response.ReasonPhrase);
        Assert.Equal("{\"ok\":true}", responseBody);
        Assert.True(response.Headers.Contains("X-Upstream"));
        Assert.False(response.Headers.Contains("Connection"));
        Assert.Equal(responseBody.Length, response.Content.Headers.ContentLength);

        Assert.NotNull(fakeExecutor.LastRequest);
        Assert.Equal("POST", fakeExecutor.LastRequest!.Method);
        Assert.Equal(new Uri("https://example.com/v1/test?x=1"), fakeExecutor.LastRequest.Uri);
        Assert.Equal("chrome136", fakeExecutor.LastRequest.ImpersonateTarget);
        Assert.Equal(45_000, fakeExecutor.LastRequest.TimeoutMs);
        Assert.Equal("hello", Encoding.UTF8.GetString(fakeExecutor.LastRequest.Body.Span));
        Assert.Contains(fakeExecutor.LastRequest.Headers, static header => header.Name == "X-Trace-Id" && header.Value == "trace-1");
        Assert.Contains(fakeExecutor.LastRequest.Headers, static header => header.Name == "Host" && header.Value == "example.com");
        Assert.Contains(fakeExecutor.LastRequest.Headers, static header => header.Name == "Content-Type" && header.Value == "application/json");
    }

    [Fact]
    public async Task SendAsync_UsesStreamingExecutor_WhenAvailable()
    {
        var fakeExecutor = new FakeStreamingCurlImpersonateExecutor(new CurlImpersonateStreamingResponse(
            statusCode: 200,
            reasonPhrase: "OK",
            headers:
            [
                new CurlImpersonateHeader("Content-Type", "text/event-stream"),
                new CurlImpersonateHeader("X-Stream", "yes"),
            ],
            body: new MemoryStream(Encoding.UTF8.GetBytes("data: hello\n\n"), writable: false)));

        using var handler = new CurlImpersonateForwarderHandler(fakeExecutor, impersonateTarget: "chrome136", timeoutMs: 30_000);
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/stream");

        using var response = await invoker.SendAsync(request, CancellationToken.None);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.True(fakeExecutor.StreamingPathUsed);
        Assert.Equal("data: hello\n\n", responseBody);
        Assert.True(response.Headers.Contains("X-Stream"));
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SendAsync_Throws_WhenRequestUriMissing()
    {
        using var handler = new CurlImpersonateForwarderHandler(
            new FakeCurlImpersonateExecutor(new CurlImpersonateResponse(200, "OK", [], [])),
            impersonateTarget: "chrome116",
            timeoutMs: 30_000);

        var send = async () =>
        {
            using var invoker = new HttpMessageInvoker(handler);
            using var request = new HttpRequestMessage();
            _ = await invoker.SendAsync(request, CancellationToken.None);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(send);
    }

    private sealed class FakeCurlImpersonateExecutor(CurlImpersonateResponse response) : ICurlImpersonateExecutor
    {
        public CurlImpersonateRequest? LastRequest { get; private set; }

        public Task<CurlImpersonateResponse> ExecuteAsync(CurlImpersonateRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class FakeStreamingCurlImpersonateExecutor(CurlImpersonateStreamingResponse response)
        : ICurlImpersonateStreamingExecutor
    {
        public bool StreamingPathUsed { get; private set; }

        public Task<CurlImpersonateResponse> ExecuteAsync(CurlImpersonateRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Buffered path should not be used in this test.");
        }

        public Task<CurlImpersonateStreamingResponse> ExecuteStreamingAsync(CurlImpersonateRequest request, CancellationToken cancellationToken)
        {
            StreamingPathUsed = true;
            return Task.FromResult(response);
        }
    }
}
