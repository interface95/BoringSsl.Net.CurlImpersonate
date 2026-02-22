# BoringSsl.Net.CurlImpersonate

`BoringSsl.Net.CurlImpersonate` provides an in-process `HttpMessageHandler` backed by `libcurl-impersonate`.

## Requirements

- .NET `net9.0` or `net10.0`
- Native shim `boringssl_net_curlimp_shim` (this package P/Invokes the shim, not the variadic `curl_easy_setopt` directly)
- `curl-impersonate` shared library available for the shim to link/load
- A valid impersonation target (for example: `chrome142`, `chrome136`, `chrome133a`)

## Minimal Example

```csharp
using BoringSsl.Net.CurlImpersonate;

using var client = new HttpClient(
    new CurlImpersonateForwarderHandler(
        executor: CurlImpersonateExecutorFactory.CreateDefault(
            profilePolicy: CurlImpersonateProfilePolicy.PreferLower,
            profileCandidates: ["chrome142", "chrome136", "chrome133a", "chrome116"]),
        impersonateTarget: "chrome142",
        timeoutMs: 30_000),
    disposeHandler: true);

using var request = new HttpRequestMessage(HttpMethod.Get, "https://tls.browserleaks.com/json");
using var response = await client.SendAsync(request, CancellationToken.None);

var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
Console.WriteLine(body);
```

## Notes

- This package focuses on upstream forwarding transport and fingerprint impersonation.
- For full MITM orchestration, use `BoringSsl.Net.Mitm`.
- Execution uses background worker threads, so ASP.NET request threads are not blocked by `curl_easy_perform`.
- Response body is streamed (`StreamContent`) when the executor supports streaming.
- Profile policy supports `Strict`, `PreferLower`, and `HighestAvailable`.

## Integration Tests

External integration tests are in:

- `tests/BoringSsl.Net.CurlImpersonate.IntegrationTests`

Build shim (host platform):

```bash
./build/build-curl-impersonate-shim.sh
```

Run them with:

```bash
RUN_CURL_IMPERSONATE_INTEGRATION_TESTS=1 \
CURL_IMPERSONATE_TARGET=chrome142 \
# optional strict check (raw JA3 hash is affected by extension permutation)
# CURL_IMPERSONATE_EXPECTED_JA3_HASH=<expected_raw_hash> \
# recommended stable check:
# CURL_IMPERSONATE_EXPECTED_JA3N_HASH=<expected_normalized_hash> \
BSSL_CURL_SHIM_LIB=<abs_path_to_shim_library> \
BSSL_CURL_IMPERSONATE_LIB=<abs_path_to_libcurl-impersonate-library> \
dotnet test tests/BoringSsl.Net.CurlImpersonate.IntegrationTests/BoringSsl.Net.CurlImpersonate.IntegrationTests.csproj -f net10.0
```
