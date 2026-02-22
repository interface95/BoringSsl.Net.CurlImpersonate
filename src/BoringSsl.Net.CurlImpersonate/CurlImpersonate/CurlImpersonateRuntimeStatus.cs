namespace BoringSsl.Net.CurlImpersonate;

public readonly record struct CurlImpersonateRuntimeStatus(
    bool IsAvailable,
    string Reason,
    string? ShimLibraryPath,
    string? CurlLibraryPath)
{
    public bool HasShimLibraryPath => !string.IsNullOrWhiteSpace(ShimLibraryPath);

    public bool HasCurlLibraryPath => !string.IsNullOrWhiteSpace(CurlLibraryPath);
}
