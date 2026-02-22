namespace BoringSsl.Net.CurlImpersonate;

public static class CurlImpersonateRuntime
{
    public static bool TryValidate(out string reason)
    {
        return CurlNativeLibraryLoader.TryValidate(out reason);
    }
}
