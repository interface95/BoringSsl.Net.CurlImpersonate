# Production Readiness Checklist

Use this checklist before enabling traffic in production.

## Release Gates

- `CI` workflow is green on `ubuntu-latest`, `windows-latest`, and `macos-latest`.
- Package tests pass for `net9.0` and `net10.0`.
- Preview package artifact and `SHA256SUMS.txt` are generated and archived.
- Package version is explicit (`x.y.z-preview.n` for preview or `x.y.z` for GA).

## Runtime Gates

- `BSSL_CURL_SHIM_LIB` is set to the expected shim path in target environment.
- `BSSL_CURL_IMPERSONATE_LIB` is set to the expected runtime library path.
- Startup probe `CurlImpersonateRuntime.GetStatus()` is recorded in logs.
- If runtime is unavailable, behavior is intentional:
- `strictRuntime=true` for fail-fast rollout, or
- `strictRuntime=false` with explicit fallback handler.

## Traffic Rollout Gates

- Canary rollout starts with low percentage traffic.
- Error budget thresholds are defined (request failure rate, timeout rate).
- Rollback path is tested:
- switch to fallback handler, or
- disable impersonate transport.
- SLO dashboards include status-code distribution and latency percentiles.

## Fingerprint Verification Gates

- Run integration test against `https://tls.peet.ws/api/all`.
- Verify expected HTTP protocol (`h2`) in target environment.
- If strict fingerprint lock is required, enforce `Strict` policy and fixed candidate list.

## Incident Response

- Keep one known-good runtime profile list for emergency downgrade.
- Keep one known-good package version for emergency rollback.
- Document owner on-call and rollback command path.
