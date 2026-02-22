# BoringSsl.Net.CurlImpersonate

`BoringSsl.Net.CurlImpersonate` provides an in-process `HttpMessageHandler` backed by `libcurl-impersonate`.

Use this package when you need Chrome-like TLS/client fingerprint behavior for upstream HTTP forwarding.

## Target Frameworks

- `net9.0`
- `net10.0`

## Install

```bash
dotnet add package BoringSsl.Net.CurlImpersonate
```

## Native Runtime Prerequisites

This package P/Invokes a native shim (`boringssl_net_curlimp_shim`) that calls into `libcurl-impersonate`.

Recommended runtime variables:

- `BSSL_CURL_SHIM_LIB=/absolute/path/to/libboringssl_net_curlimp_shim.so|.dylib|.dll`
- `BSSL_CURL_IMPERSONATE_LIB=/absolute/path/to/libcurl-impersonate-chrome.*`

If auto-discovery fails, set both variables explicitly.

You can validate runtime loading before first request:

```csharp
using BoringSsl.Net.CurlImpersonate;

if (!CurlImpersonateRuntime.TryValidate(out var reason))
{
    throw new InvalidOperationException($"curl-impersonate runtime unavailable: {reason}");
}
```

## Quick Start

```csharp
using BoringSsl.Net.CurlImpersonate;

var executor = CurlImpersonateExecutorFactory.CreateDefault(
    profilePolicy: CurlImpersonateProfilePolicy.PreferLower,
    profileCandidates: ["chrome142", "chrome136", "chrome133a", "chrome116"]);

using var client = new HttpClient(
    new CurlImpersonateForwarderHandler(
        executor: executor,
        impersonateTarget: "chrome142",
        timeoutMs: 30_000),
    disposeHandler: true);

using var response = await client.GetAsync("https://tls.peet.ws/api/all", CancellationToken.None);
response.EnsureSuccessStatusCode();

var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
Console.WriteLine(body);
```

## Profile Selection Policy

`CurlImpersonateProfilePolicy` controls behavior when requested target is not supported by current runtime:

- `Strict`: fail fast.
- `PreferLower`: fallback to nearest lower supported candidate, then highest available.
- `HighestAvailable`: always choose highest supported candidate.

Default candidates (`CurlImpersonateDefaults.ProfileCandidates`):

- `chrome142`, `chrome136`, `chrome133a`, `chrome116`, `chrome110`, `chrome107`, `chrome104`, `chrome101`, `chrome100`, `chrome99`

## Streaming Responses (SSE/Long Streams)

`CurlImpersonateForwarderHandler` supports streaming mode. Use `ResponseHeadersRead`:

```csharp
using var request = new HttpRequestMessage(HttpMethod.Get, "https://stream.wikimedia.org/v2/stream/recentchange");
request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
response.EnsureSuccessStatusCode();

await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
```

## Runtime/Profile Recommendations

Suggested baseline:

- target: `chrome142`
- policy: `PreferLower`
- candidates: `chrome142,chrome136,chrome133a,chrome116`

When runtime may lag behind latest profile:

- switch policy to `HighestAvailable`, or
- keep `PreferLower` and provide a descending candidate list.

When exact profile lock is required:

- use `Strict`.

## Integration Tests

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
dotnet test tests/BoringSsl.Net.CurlImpersonate.IntegrationTests/BoringSsl.Net.CurlImpersonate.IntegrationTests.csproj -f net10.0 -c Release
```

Single smoke test:

```bash
RUN_CURL_IMPERSONATE_INTEGRATION_TESTS=1 \
CURL_IMPERSONATE_TARGET=chrome142 \
BSSL_CURL_SHIM_LIB=<abs_path_to_shim_library> \
BSSL_CURL_IMPERSONATE_LIB=<abs_path_to_libcurl-impersonate-library> \
dotnet test tests/BoringSsl.Net.CurlImpersonate.IntegrationTests/BoringSsl.Net.CurlImpersonate.IntegrationTests.csproj \
  -f net10.0 -c Release \
  --filter "FullyQualifiedName~HttpsGet_HttpBin_Returns200"
```

## Troubleshooting

- `DllNotFoundException` for shim: set `BSSL_CURL_SHIM_LIB` to absolute shim path.
- `curl_easy_impersonate symbol was not found`: loaded curl is not `curl-impersonate`.
- `curl_easy_impersonate ... unsupported target`: requested profile is unavailable in current runtime.
- Request canceled unexpectedly: check `timeoutMs` and caller `CancellationToken`.
