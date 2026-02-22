namespace BoringSsl.Net.CurlImpersonate.UnitTests.CurlImpersonate;

using BoringSsl.Net.CurlImpersonate;
using System.Net.Http;

public sealed class CurlImpersonateRuntimeAndFactoryTests
{
    [Fact]
    public void CreateOrFallback_ReturnsCurlHandler_WhenRuntimeAvailable()
    {
        var fallbackCalled = false;

        using var handler = CurlImpersonateHttpHandlerFactory.CreateOrFallback(
            fallbackHandlerFactory: () =>
            {
                fallbackCalled = true;
                return new SocketsHttpHandler();
            },
            impersonateTarget: "chrome142",
            timeoutMs: 30_000,
            runtimeProbe: static () => (true, string.Empty));

        Assert.IsType<CurlImpersonateForwarderHandler>(handler);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public void CreateOrFallback_ReturnsFallback_WhenRuntimeUnavailable_AndStrictDisabled()
    {
        var fallback = new SocketsHttpHandler();

        using var handler = CurlImpersonateHttpHandlerFactory.CreateOrFallback(
            fallbackHandlerFactory: () => fallback,
            strictRuntime: false,
            runtimeProbe: static () => (false, "missing runtime"));

        Assert.Same(fallback, handler);
    }

    [Fact]
    public void CreateOrFallback_Throws_WhenRuntimeUnavailable_AndStrictEnabled()
    {
        var action = () => CurlImpersonateHttpHandlerFactory.CreateOrFallback(
            fallbackHandlerFactory: static () => new SocketsHttpHandler(),
            strictRuntime: true,
            runtimeProbe: static () => (false, "missing runtime"));

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains("strictRuntime=true", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateOrFallback_Throws_WhenTimeoutIsInvalid()
    {
        var action = () => CurlImpersonateHttpHandlerFactory.CreateOrFallback(
            fallbackHandlerFactory: static () => new SocketsHttpHandler(),
            timeoutMs: 0,
            runtimeProbe: static () => (true, string.Empty));

        _ = Assert.Throws<ArgumentOutOfRangeException>(action);
    }

    [Fact]
    public void GetStatus_MasksPaths_WhenIncludePathsIsFalse()
    {
        const string shimPath = "/tmp/shim/libboringssl_net_curlimp_shim.so";
        const string curlPath = "/tmp/curl/libcurl-impersonate-chrome.so";
        var oldShim = Environment.GetEnvironmentVariable(CurlImpersonateRuntime.ShimLibraryPathEnv);
        var oldCurl = Environment.GetEnvironmentVariable(CurlImpersonateRuntime.CurlLibraryPathEnv);

        try
        {
            Environment.SetEnvironmentVariable(CurlImpersonateRuntime.ShimLibraryPathEnv, shimPath);
            Environment.SetEnvironmentVariable(CurlImpersonateRuntime.CurlLibraryPathEnv, curlPath);

            var status = CurlImpersonateRuntime.GetStatus(includePaths: false);

            Assert.True(status.HasShimLibraryPath);
            Assert.True(status.HasCurlLibraryPath);
            Assert.Equal("<set>", status.ShimLibraryPath);
            Assert.Equal("<set>", status.CurlLibraryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CurlImpersonateRuntime.ShimLibraryPathEnv, oldShim);
            Environment.SetEnvironmentVariable(CurlImpersonateRuntime.CurlLibraryPathEnv, oldCurl);
        }
    }

    [Fact]
    public void GetStatus_ExposesPaths_WhenIncludePathsIsTrue()
    {
        const string shimPath = "/tmp/shim/libboringssl_net_curlimp_shim.so";
        const string curlPath = "/tmp/curl/libcurl-impersonate-chrome.so";
        var oldShim = Environment.GetEnvironmentVariable(CurlImpersonateRuntime.ShimLibraryPathEnv);
        var oldCurl = Environment.GetEnvironmentVariable(CurlImpersonateRuntime.CurlLibraryPathEnv);

        try
        {
            Environment.SetEnvironmentVariable(CurlImpersonateRuntime.ShimLibraryPathEnv, shimPath);
            Environment.SetEnvironmentVariable(CurlImpersonateRuntime.CurlLibraryPathEnv, curlPath);

            var status = CurlImpersonateRuntime.GetStatus(includePaths: true);

            Assert.True(status.HasShimLibraryPath);
            Assert.True(status.HasCurlLibraryPath);
            Assert.Equal(shimPath, status.ShimLibraryPath);
            Assert.Equal(curlPath, status.CurlLibraryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CurlImpersonateRuntime.ShimLibraryPathEnv, oldShim);
            Environment.SetEnvironmentVariable(CurlImpersonateRuntime.CurlLibraryPathEnv, oldCurl);
        }
    }
}
