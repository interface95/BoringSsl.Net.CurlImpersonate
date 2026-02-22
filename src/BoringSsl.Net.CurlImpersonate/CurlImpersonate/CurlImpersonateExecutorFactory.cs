namespace BoringSsl.Net.CurlImpersonate;

using Microsoft.Extensions.Logging;

public static class CurlImpersonateExecutorFactory
{
    public static ICurlImpersonateExecutor CreateDefault(
        ILogger<LibCurlImpersonateExecutor>? logger = null,
        CurlImpersonateProfilePolicy profilePolicy = CurlImpersonateProfilePolicy.PreferLower,
        IReadOnlyList<string>? profileCandidates = null) =>
        new LibCurlImpersonateExecutor(logger, profilePolicy, profileCandidates);
}
