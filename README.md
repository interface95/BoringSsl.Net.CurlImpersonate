# BoringSsl.Net.CurlImpersonate Workspace

[![CI](https://img.shields.io/badge/CI-not%20configured-lightgrey)](https://github.com/interface95/BoringSsl.Net.CurlImpersonate/actions)
[![NuGet](https://img.shields.io/nuget/vpre/BoringSsl.Net.CurlImpersonate)](https://www.nuget.org/packages/BoringSsl.Net.CurlImpersonate)
[![NuGet Downloads](https://img.shields.io/nuget/dt/BoringSsl.Net.CurlImpersonate)](https://www.nuget.org/packages/BoringSsl.Net.CurlImpersonate)

Standalone package workspace for `BoringSsl.Net.CurlImpersonate`.

This repository focuses on one reusable package:

- `BoringSsl.Net.CurlImpersonate` (in-process `libcurl-impersonate` based forwarding transport for .NET).

## Layout

- `src/BoringSsl.Net.CurlImpersonate`: package project + NuGet README.
- `tests/BoringSsl.Net.CurlImpersonate.UnitTests`: unit tests.
- `tests/BoringSsl.Net.CurlImpersonate.IntegrationTests`: external runtime integration tests.
- `native/curl_impersonate_shim`: native C shim.
- `build/build-curl-impersonate-shim.sh`: shim build helper.

## Build and Test

```bash
dotnet restore BoringSsl.Net.CurlImpersonate.Package.slnx
dotnet build BoringSsl.Net.CurlImpersonate.Package.slnx -c Release
dotnet test BoringSsl.Net.CurlImpersonate.Package.slnx -c Release
```

## Pack

```bash
dotnet pack src/BoringSsl.Net.CurlImpersonate/BoringSsl.Net.CurlImpersonate.csproj -c Release -o artifacts/nuget
```

## Runtime Dependencies

This package requires:

- Native shim: `boringssl_net_curlimp_shim`.
- Runtime curl library: `libcurl-impersonate-chrome`.

Recommended environment variables:

- `BSSL_CURL_SHIM_LIB=/absolute/path/to/libboringssl_net_curlimp_shim.so|.dylib|.dll`
- `BSSL_CURL_IMPERSONATE_LIB=/absolute/path/to/libcurl-impersonate-chrome.*`

Build shim helper:

```bash
./build/build-curl-impersonate-shim.sh
```

## Integration Tests (External Runtime)

```bash
RUN_CURL_IMPERSONATE_INTEGRATION_TESTS=1 \
CURL_IMPERSONATE_TARGET=chrome142 \
BSSL_CURL_SHIM_LIB=/absolute/path/to/libboringssl_net_curlimp_shim.so \
BSSL_CURL_IMPERSONATE_LIB=/absolute/path/to/libcurl-impersonate-chrome.so \
dotnet test tests/BoringSsl.Net.CurlImpersonate.IntegrationTests/BoringSsl.Net.CurlImpersonate.IntegrationTests.csproj -f net10.0 -c Release
```

Optional fingerprint assertions:

- `CURL_IMPERSONATE_EXPECTED_JA3_HASH=<hash>`
- `CURL_IMPERSONATE_EXPECTED_JA3N_HASH=<hash>`

## Quick Integration in App

```csharp
using BoringSsl.Net.CurlImpersonate;

var executor = CurlImpersonateExecutorFactory.CreateDefault(
    profilePolicy: CurlImpersonateProfilePolicy.PreferLower,
    profileCandidates: ["chrome142", "chrome136", "chrome133a", "chrome116"]);

using var client = new HttpClient(
    new CurlImpersonateForwarderHandler(executor, impersonateTarget: "chrome142", timeoutMs: 30_000),
    disposeHandler: true);
```

## 5-Minute Smoke Test

1) Build shim:

```bash
./build/build-curl-impersonate-shim.sh
```

2) Run one external integration test:

```bash
RUN_CURL_IMPERSONATE_INTEGRATION_TESTS=1 \
CURL_IMPERSONATE_TARGET=chrome142 \
BSSL_CURL_SHIM_LIB=/absolute/path/to/libboringssl_net_curlimp_shim.so \
BSSL_CURL_IMPERSONATE_LIB=/absolute/path/to/libcurl-impersonate-chrome.so \
dotnet test tests/BoringSsl.Net.CurlImpersonate.IntegrationTests/BoringSsl.Net.CurlImpersonate.IntegrationTests.csproj \
  -f net10.0 -c Release \
  --filter "FullyQualifiedName~HttpsGet_HttpBin_Returns200"
```

Expected output (key lines):

```text
Passed!  - Failed: 0, Passed: 1, Skipped: 0
```

## Runtime/Profile Matrix

Recommended defaults for production-like behavior:

| Scenario | `CURL_IMPERSONATE_TARGET` | `--curl-impersonate-policy` | Candidate list |
|---|---|---|---|
| Linux x64/arm64 server | `chrome142` | `prefer-lower` | `chrome142,chrome136,chrome133a,chrome116` |
| macOS dev machine | `chrome142` | `prefer-lower` | `chrome142,chrome136,chrome133a,chrome116` |
| Windows dev/test | `chrome142` | `prefer-lower` | `chrome142,chrome136,chrome133a,chrome116` |
| Runtime is old/unknown | requested latest | `highest-available` | keep full default list |
| Strict fingerprint lock | exact target | `strict` | include only allowed profiles |

Policy behavior summary:

- `strict`: unsupported target fails immediately.
- `prefer-lower`: fallback to nearest lower supported profile, then highest available.
- `highest-available`: always choose best available from candidate list.

Full package API guide is in:

- `src/BoringSsl.Net.CurlImpersonate/README.md`

## 中文快速接入

```bash
dotnet restore BoringSsl.Net.CurlImpersonate.Package.slnx
dotnet build BoringSsl.Net.CurlImpersonate.Package.slnx -c Release
dotnet test BoringSsl.Net.CurlImpersonate.Package.slnx -c Release
```

如果你要跑真实指纹验证测试，请设置：

- `RUN_CURL_IMPERSONATE_INTEGRATION_TESTS=1`
- `BSSL_CURL_SHIM_LIB`
- `BSSL_CURL_IMPERSONATE_LIB`
