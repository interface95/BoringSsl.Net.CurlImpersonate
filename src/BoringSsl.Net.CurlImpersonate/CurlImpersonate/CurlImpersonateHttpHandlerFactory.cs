namespace BoringSsl.Net.CurlImpersonate;

using Microsoft.Extensions.Logging;

public static class CurlImpersonateHttpHandlerFactory
{
    public static HttpMessageHandler CreateOrFallback(
        Func<HttpMessageHandler> fallbackHandlerFactory,
        string impersonateTarget = "chrome142",
        int timeoutMs = 30_000,
        CurlImpersonateProfilePolicy profilePolicy = CurlImpersonateProfilePolicy.PreferLower,
        IReadOnlyList<string>? profileCandidates = null,
        bool strictRuntime = false,
        ILogger<LibCurlImpersonateExecutor>? logger = null,
        Func<(bool IsAvailable, string Reason)>? runtimeProbe = null)
    {
        ArgumentNullException.ThrowIfNull(fallbackHandlerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(impersonateTarget);
        if (timeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, "timeoutMs must be greater than 0.");
        }

        runtimeProbe ??= static () =>
        {
            var ok = CurlImpersonateRuntime.TryValidate(out var reason);
            return (ok, reason);
        };

        var runtimeStatus = runtimeProbe();
        if (runtimeStatus.IsAvailable)
        {
            return new CurlImpersonateForwarderHandler(
                executor: CurlImpersonateExecutorFactory.CreateDefault(
                    logger: logger,
                    profilePolicy: profilePolicy,
                    profileCandidates: profileCandidates),
                impersonateTarget: impersonateTarget,
                timeoutMs: timeoutMs);
        }

        if (strictRuntime)
        {
            throw new InvalidOperationException(
                $"curl-impersonate runtime unavailable and strictRuntime=true. Details: {runtimeStatus.Reason}");
        }

        var fallback = fallbackHandlerFactory();
        if (fallback is null)
        {
            throw new InvalidOperationException("fallbackHandlerFactory returned null.");
        }

        return fallback;
    }
}
