namespace BoringSsl.Net.CurlImpersonate.IntegrationTests.CurlImpersonate;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoringSsl.Net.CurlImpersonate;

public sealed class LibCurlImpersonateIntegrationTests
{
    [CurlIntegrationFact]
    public async Task HttpsGet_HttpBin_Returns200()
    {
        using var executor = new LibCurlImpersonateExecutor();
        using var client = CreateClient(executor);

        using var response = await client.GetAsync("https://httpbin.org/get");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [CurlIntegrationFact]
    public async Task PostJson_HttpBin_EchoBodyMatches()
    {
        using var executor = new LibCurlImpersonateExecutor();
        using var client = CreateClient(executor);
        const string body = """{"message":"hello","n":1}""";

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://httpbin.org/post")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);

        Assert.Equal(body, json.RootElement.GetProperty("data").GetString());
    }

    [CurlIntegrationFact]
    public async Task TlsFingerprint_TlsPeetWs_MatchesExpectedJa3Hash()
    {
        using var executor = new LibCurlImpersonateExecutor();
        using var client = CreateClient(executor);

        using var response = await client.GetAsync("https://tls.peet.ws/api/all");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        var ja3 = json.RootElement.GetProperty("tls").GetProperty("ja3").GetString();
        var actualJa3Hash = json.RootElement.GetProperty("tls").GetProperty("ja3_hash").GetString();
        var normalizedJa3Hash = ComputeNormalizedJa3Hash(ja3);
        Assert.False(string.IsNullOrWhiteSpace(actualJa3Hash), "tls.ja3_hash was empty.");
        Assert.False(string.IsNullOrWhiteSpace(normalizedJa3Hash), "normalized JA3 hash was empty.");

        var expectedRawHash = Environment.GetEnvironmentVariable(CurlIntegrationSettings.ExpectedJa3HashEnv);
        if (!string.IsNullOrWhiteSpace(expectedRawHash))
        {
            Assert.True(string.Equals(expectedRawHash, actualJa3Hash, StringComparison.OrdinalIgnoreCase),
                $"JA3 hash mismatch. expected={expectedRawHash}, actual={actualJa3Hash}");
        }

        var expectedNormalizedHash = Environment.GetEnvironmentVariable(CurlIntegrationSettings.ExpectedNormalizedJa3HashEnv);
        if (!string.IsNullOrWhiteSpace(expectedNormalizedHash))
        {
            Assert.True(string.Equals(expectedNormalizedHash, normalizedJa3Hash, StringComparison.OrdinalIgnoreCase),
                $"Normalized JA3 hash mismatch. expected={expectedNormalizedHash}, actual={normalizedJa3Hash}, rawJa3={ja3}");
        }
    }

    [CurlIntegrationFact]
    public async Task Http2Validation_TlsPeetWs_ReportsH2()
    {
        using var executor = new LibCurlImpersonateExecutor();
        using var client = CreateClient(executor);

        using var response = await client.GetAsync("https://tls.peet.ws/api/all");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        var httpVersion = json.RootElement.GetProperty("http_version").GetString();

        Assert.Equal("h2", httpVersion);
    }

    [CurlIntegrationFact]
    public async Task Http2Validation_HttpResponseVersion_Is20()
    {
        using var executor = new LibCurlImpersonateExecutor();
        using var client = CreateClient(executor);

        using var response = await client.GetAsync("https://tls.peet.ws/api/all");
        response.EnsureSuccessStatusCode();

        Assert.Equal(HttpVersion.Version20, response.Version);
    }

    [CurlIntegrationFact]
    public async Task StreamingResponse_SseEndpoint_CanReadChunks()
    {
        using var executor = new LibCurlImpersonateExecutor();
        using var client = CreateClient(executor);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://stream.wikimedia.org/v2/stream/recentchange");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[512];
        var total = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(buffer, timeout.Token);
            if (bytesRead == 0)
            {
                break;
            }

            total.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            if (total.ToString().Contains("\ndata: ", StringComparison.Ordinal))
            {
                break;
            }
        }

        Assert.Contains("\ndata: ", total.ToString(), StringComparison.Ordinal);
    }

    [CurlIntegrationFact]
    public async Task Cancellation_DelayRequest_ThrowsOperationCanceledException()
    {
        using var executor = new LibCurlImpersonateExecutor();
        var request = new CurlImpersonateRequest(
            method: "GET",
            uri: new Uri("https://httpbin.org/delay/10"),
            headers: [],
            body: ReadOnlyMemory<byte>.Empty,
            impersonateTarget: CurlIntegrationSettings.GetImpersonateTarget(),
            timeoutMs: 60_000);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(request, cancellation.Token));
    }

    [CurlIntegrationFact]
    public async Task ConnectionPool_TenRequests_ReusesHandle()
    {
        using var executor = new LibCurlImpersonateExecutor();
        using var client = CreateClient(executor);

        for (var i = 0; i < 10; i++)
        {
            using var response = await client.GetAsync("https://httpbin.org/get");
            response.EnsureSuccessStatusCode();
        }

        var pooledHandleCount = GetPooledHandleCount(executor);
        Assert.InRange(pooledHandleCount, 1, 4);
    }

    private static HttpClient CreateClient(LibCurlImpersonateExecutor executor)
    {
        return new HttpClient(
            new CurlImpersonateForwarderHandler(
                executor,
                impersonateTarget: CurlIntegrationSettings.GetImpersonateTarget(),
                timeoutMs: 30_000),
            disposeHandler: true);
    }

    private static int GetPooledHandleCount(LibCurlImpersonateExecutor executor)
    {
        return executor.DebugPooledHandleCount;
    }

    private static string ComputeNormalizedJa3Hash(string? ja3)
    {
        if (string.IsNullOrWhiteSpace(ja3))
        {
            return string.Empty;
        }

        var segments = ja3.Split(',', StringSplitOptions.None);
        if (segments.Length != 5)
        {
            return string.Empty;
        }

        var normalizedExtensions = segments[2].Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => int.TryParse(value, out var parsed) ? parsed : int.MaxValue)
            .OrderBy(static value => value)
            .Select(static value => value.ToString())
            .ToArray();
        var normalizedJa3 = string.Join(",", [segments[0], segments[1], string.Join("-", normalizedExtensions), segments[3], segments[4]]);

        return Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(normalizedJa3)));
    }
}
