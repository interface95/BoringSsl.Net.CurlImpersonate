namespace BoringSsl.Net.CurlImpersonate;

using System.Reflection;
using System.Runtime.InteropServices;

internal static class CurlNativeLibraryLoader
{
    private const string ShimLibraryName = "boringssl_net_curlimp_shim";
    private const string ShimLibraryPathEnv = "BSSL_CURL_SHIM_LIB";
    private const string CurlLibraryPathEnv = "BSSL_CURL_IMPERSONATE_LIB";
    private static readonly Lock SyncLock = new();
    private static IntPtr _shimHandle;
    private static bool _shimLoaded;
    private static bool _resolverRegistered;

    public static bool TryValidate(out string reason)
    {
        try
        {
            EnsureLoaded();
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static void EnsureLoaded()
    {
        lock (SyncLock)
        {
            if (!_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(typeof(CurlNativeLibraryLoader).Assembly, Resolve);
                _resolverRegistered = true;
            }

            if (_shimLoaded)
            {
                return;
            }

            TryPreloadCurlLibrary();
            _shimHandle = LoadShimOrThrow();
            _shimLoaded = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        _ = assembly;
        _ = searchPath;
        if (!string.Equals(libraryName, ShimLibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        EnsureLoaded();
        return _shimHandle;
    }

    private static void TryPreloadCurlLibrary()
    {
        var curlPath = Environment.GetEnvironmentVariable(CurlLibraryPathEnv);
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            return;
        }

        if (!File.Exists(curlPath))
        {
            throw new DllNotFoundException(
                $"Environment variable {CurlLibraryPathEnv} points to a missing file: {curlPath}");
        }

        _ = NativeLibrary.Load(curlPath);
    }

    private static IntPtr LoadShimOrThrow()
    {
        var fromEnv = Environment.GetEnvironmentVariable(ShimLibraryPathEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (!File.Exists(fromEnv))
            {
                throw new DllNotFoundException(
                    $"Environment variable {ShimLibraryPathEnv} points to a missing file: {fromEnv}");
            }

            return NativeLibrary.Load(fromEnv);
        }

        foreach (var candidate in GetCandidatePaths())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            return NativeLibrary.Load(candidate);
        }

        if (NativeLibrary.TryLoad(ShimLibraryName, out var handle))
        {
            return handle;
        }

        throw new DllNotFoundException(
            $"Failed to load native shim '{ShimLibraryName}'. " +
            $"Set {ShimLibraryPathEnv} to an absolute shim path if auto-discovery fails.");
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(baseDir, "boringssl_net_curlimp_shim.dll");
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.Combine(baseDir, "libboringssl_net_curlimp_shim.dylib");
            yield break;
        }

        yield return Path.Combine(baseDir, "libboringssl_net_curlimp_shim.so");
    }
}
