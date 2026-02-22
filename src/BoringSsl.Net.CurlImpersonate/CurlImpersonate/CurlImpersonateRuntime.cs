namespace BoringSsl.Net.CurlImpersonate;

public static class CurlImpersonateRuntime
{
    public const string ShimLibraryPathEnv = "BSSL_CURL_SHIM_LIB";
    public const string CurlLibraryPathEnv = "BSSL_CURL_IMPERSONATE_LIB";

    public static bool TryValidate(out string reason)
    {
        return CurlNativeLibraryLoader.TryValidate(out reason);
    }

    public static void EnsureAvailableOrThrow()
    {
        if (TryValidate(out var reason))
        {
            return;
        }

        throw new InvalidOperationException(
            $"curl-impersonate runtime unavailable. Set {ShimLibraryPathEnv} and {CurlLibraryPathEnv}. Details: {reason}");
    }

    public static CurlImpersonateRuntimeStatus GetStatus(bool includePaths = false)
    {
        var isAvailable = TryValidate(out var reason);
        var shimPath = Environment.GetEnvironmentVariable(ShimLibraryPathEnv);
        var curlPath = Environment.GetEnvironmentVariable(CurlLibraryPathEnv);

        return new CurlImpersonateRuntimeStatus(
            IsAvailable: isAvailable,
            Reason: reason,
            ShimLibraryPath: NormalizePath(shimPath, includePaths),
            CurlLibraryPath: NormalizePath(curlPath, includePaths));
    }

    private static string? NormalizePath(string? value, bool includePaths)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return includePaths ? value : "<set>";
    }
}
