namespace BoringSsl.Net.CurlImpersonate.IntegrationTests.CurlImpersonate;

using BoringSsl.Net.CurlImpersonate;

public sealed class CurlIntegrationFactAttribute : FactAttribute
{
    public CurlIntegrationFactAttribute()
    {
        if (!CurlIntegrationSettings.IsEnabled())
        {
            Skip = $"Set {CurlIntegrationSettings.RunIntegrationEnv} to 1 to run external curl-impersonate integration tests.";
            return;
        }

        if (!CurlIntegrationSettings.TryValidateRuntime(out var reason))
        {
            Skip = reason;
        }
    }
}

internal static class CurlIntegrationSettings
{
    public const string RunIntegrationEnv = "RUN_CURL_IMPERSONATE_INTEGRATION_TESTS";
    public const string ImpersonateTargetEnv = "CURL_IMPERSONATE_TARGET";
    public const string ExpectedJa3HashEnv = "CURL_IMPERSONATE_EXPECTED_JA3_HASH";
    public const string ExpectedNormalizedJa3HashEnv = "CURL_IMPERSONATE_EXPECTED_JA3N_HASH";
    public const string ShimLibraryEnv = "BSSL_CURL_SHIM_LIB";
    public const string CurlLibraryEnv = "BSSL_CURL_IMPERSONATE_LIB";

    public static bool IsEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(RunIntegrationEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetImpersonateTarget()
    {
        return Environment.GetEnvironmentVariable(ImpersonateTargetEnv) is { Length: > 0 } target
            ? target
            : "chrome142";
    }

    public static bool TryValidateRuntime(out string reason)
    {
        if (!CurlImpersonateRuntime.TryValidate(out reason))
        {
            reason =
                $"Failed to load curl-impersonate runtime via shim. " +
                $"Set {ShimLibraryEnv} and optionally {CurlLibraryEnv} when running integration tests. " +
                $"Details: {reason}";
            return false;
        }

        return true;
    }
}
