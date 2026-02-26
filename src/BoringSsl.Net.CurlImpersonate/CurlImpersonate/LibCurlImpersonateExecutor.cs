namespace BoringSsl.Net.CurlImpersonate;

using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class LibCurlImpersonateExecutor(
    ILogger<LibCurlImpersonateExecutor>? logger = null,
    CurlImpersonateProfilePolicy profilePolicy = CurlImpersonateProfilePolicy.PreferLower,
    IReadOnlyList<string>? profileCandidates = null) : ICurlImpersonateExecutor, ICurlImpersonateStreamingExecutor, IDisposable
{
    private const long CurlGlobalDefault = 3;
    private const int MaxPendingBodyChunks = 128;
    private static readonly Lock GlobalInitLock = new();
    private static readonly Lock RuntimeCapabilityCacheLock = new();
    private static readonly Dictionary<string, RuntimeCapabilityCache> RuntimeCapabilityCaches = new(StringComparer.Ordinal);
    private static bool _globalInitialized;
    private static long _requestSequence;
    private static readonly nuint CurlReadFuncAbort = unchecked((nuint)(nint)(-1));
    private static readonly CurlMultiDispatcher MultiDispatcher = new();
    private static readonly CurlNative.CurlWriteCallback WriteBodyCallback = OnWriteBody;
    private static readonly CurlNative.CurlWriteCallback WriteHeaderCallback = OnWriteHeader;
    private static readonly CurlNative.CurlWriteCallback ReadBodyCallback = OnReadBody;
    private static readonly CurlNative.CurlXferInfoCallback XferInfoCallback = OnXferInfo;

    private readonly Lock _poolLock = new();
    private readonly Lock _impersonationResolutionLock = new();
    private readonly Stack<IntPtr> _easyHandlePool = [];
    private readonly Dictionary<string, string> _resolvedImpersonationTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly CurlImpersonateProfilePolicy _profilePolicy = profilePolicy;
    private readonly string[] _profileCandidates = NormalizeCandidates(profileCandidates);
    private readonly ILogger<LibCurlImpersonateExecutor> _logger = logger ?? NullLogger<LibCurlImpersonateExecutor>.Instance;
    private bool _disposed;

    internal int DebugPooledHandleCount
    {
        get
        {
            lock (_poolLock)
            {
                return _easyHandlePool.Count;
            }
        }
    }

    public async Task<CurlImpersonateResponse> ExecuteAsync(CurlImpersonateRequest request, CancellationToken cancellationToken)
    {
        var streamingResponse = await ExecuteStreamingAsync(request, cancellationToken).ConfigureAwait(false);
        using var body = streamingResponse.Body;
        var initialCapacity = TryGetContentLength(streamingResponse.Headers, out var contentLength)
            ? contentLength
            : 0;
        using var buffer = initialCapacity > 0 ? new MemoryStream(initialCapacity) : new MemoryStream();
        await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return new CurlImpersonateResponse(
            statusCode: streamingResponse.StatusCode,
            reasonPhrase: streamingResponse.ReasonPhrase,
            headers: streamingResponse.Headers,
            body: buffer.ToArray(),
            protocolVersion: streamingResponse.ProtocolVersion);
    }

    public async Task<CurlImpersonateStreamingResponse> ExecuteStreamingAsync(
        CurlImpersonateRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        EnsureGlobalInitialized();

        var logContext = CreateLogContext(request);
        var startedAt = Stopwatch.GetTimestamp();
        LogRequestStart(logContext);

        var pipe = new Pipe();
        var transferState = new CurlTransferState(cancellationToken);
        var transfer = await PrepareTransferAsync(request, transferState, cancellationToken).ConfigureAwait(false);

        var completion = RunTransferAsync(transfer, pipe.Writer, cancellationToken, logContext, startedAt);
        ObserveFaults(completion);

        try
        {
            var headers = await transferState.HeadersReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CurlImpersonateStreamingResponse(
                statusCode: headers.StatusCode,
                reasonPhrase: headers.ReasonPhrase,
                headers: headers.Headers,
                body: pipe.Reader.AsStream(),
                protocolVersion: headers.ProtocolVersion);
        }
        catch
        {
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_poolLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var pooledCount = _easyHandlePool.Count;
            while (_easyHandlePool.TryPop(out var easyHandle))
            {
                CurlNative.EasyCleanup(easyHandle);
            }

            _logger.LogDebug("Disposed curl executor and released {HandleCount} pooled easy handles.", pooledCount);
        }
    }

    private async Task<PreparedTransfer> PrepareTransferAsync(
        CurlImpersonateRequest request,
        CurlTransferState transferState,
        CancellationToken cancellationToken)
    {
        var easyHandle = RentEasyHandle();
        var transfer = new PreparedTransfer(easyHandle, transferState);
        try
        {
            transfer.StateHandle = GCHandle.Alloc(transferState, GCHandleType.Normal);
            var transferStatePtr = GCHandle.ToIntPtr(transfer.StateHandle);

            SetOpt(easyHandle, CurlOption.Url, request.Uri.AbsoluteUri);
            SetOpt(easyHandle, CurlOption.CustomRequest, request.Method);
            SetOpt(easyHandle, CurlOption.HttpVersion, (long)CurlHttpVersion.Http2_0);
            SetOpt(easyHandle, CurlOption.TimeoutMs, request.TimeoutMs);
            SetOpt(easyHandle, CurlOption.FollowLocation, 0L);
            SetOpt(easyHandle, CurlOption.NoSignal, 1L);
            SetOpt(easyHandle, CurlOption.AcceptEncoding, "identity");
            SetOpt(easyHandle, CurlOption.WriteFunction, WriteBodyCallback);
            SetOpt(easyHandle, CurlOption.WriteData, transferStatePtr);
            SetOpt(easyHandle, CurlOption.HeaderFunction, WriteHeaderCallback);
            SetOpt(easyHandle, CurlOption.HeaderData, transferStatePtr);
            SetOpt(easyHandle, CurlOption.NoProgress, 0L);
            SetOpt(easyHandle, CurlOption.XferInfoFunction, XferInfoCallback);
            SetOpt(easyHandle, CurlOption.XferInfoData, transferStatePtr);

            foreach (var header in request.Headers)
            {
                transfer.HeaderList = CurlNative.SListAppend(transfer.HeaderList, $"{header.Name}: {header.Value}");
                if (transfer.HeaderList == IntPtr.Zero)
                {
                    throw new InvalidOperationException("curl_slist_append returned null.");
                }
            }

            if (transfer.HeaderList != IntPtr.Zero)
            {
                SetOpt(easyHandle, CurlOption.HttpHeader, transfer.HeaderList);
            }

            if (request.HasBody)
            {
                // 禁止 curl 自动添加 Expect: 100-continue
                transfer.HeaderList = CurlNative.SListAppend(transfer.HeaderList, "Expect:");
                if (transfer.HeaderList != IntPtr.Zero)
                    SetOpt(easyHandle, CurlOption.HttpHeader, transfer.HeaderList);

                await ConfigureRequestBodyAsync(request, transferState, transferStatePtr, easyHandle, cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolvedTarget = ResolveImpersonationTarget(request.ImpersonateTarget);
            var impersonateResult = TryImpersonate(easyHandle, resolvedTarget);
            EnsureSuccess(impersonateResult, "curl_easy_impersonate");

            // curl_easy_impersonate 可能覆盖了 HTTP 版本设置，
            // 在 impersonate 之后重新强制 HTTP/2
            SetOpt(easyHandle, CurlOption.HttpVersion, (long)CurlHttpVersion.Http2_0);

            return transfer;
        }
        catch
        {
            CleanupTransfer(transfer);
            throw;
        }
    }

    private static async Task ConfigureRequestBodyAsync(
        CurlImpersonateRequest request,
        CurlTransferState transferState,
        IntPtr transferStatePtr,
        IntPtr easyHandle,
        CancellationToken cancellationToken)
    {
        var requestBodyStream = await request.OpenBodyStreamAsync(cancellationToken).ConfigureAwait(false);
        if (requestBodyStream is null)
            return;

        transferState.SetRequestBodyStream(requestBodyStream, request.BodyLength);
        SetOpt(easyHandle, CurlOption.ReadFunction, ReadBodyCallback);
        SetOpt(easyHandle, CurlOption.ReadData, transferStatePtr);

        // 对于 POST，使用 CURLOPT_POST 保持正确的 :method 伪头。
        // CURLOPT_UPLOAD 会把方法改成 PUT，在 HTTP/2 下即使设了 CUSTOMREQUEST 也可能不生效。
        // Expect: 100-continue 已通过空 Expect: header 禁止。
        if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            SetOpt(easyHandle, CurlOption.Post, 1L);
        else
            SetOpt(easyHandle, CurlOption.Upload, 1L);

        if (request.BodyLength.HasValue)
        {
            SetOpt(easyHandle, CurlOption.InFileSizeLarge, request.BodyLength.Value);
            SetOpt(easyHandle, CurlOption.PostFieldSizeLarge, request.BodyLength.Value);
        }
    }

    private async Task RunTransferAsync(
        PreparedTransfer transfer,
        PipeWriter bodyWriter,
        CancellationToken cancellationToken,
        RequestLogContext logContext,
        long startedAtTimestamp)
    {
        var bodyPumpTask = PumpBodyAsync(transfer.State, bodyWriter);
        Exception? completionError = null;
        try
        {
            var performResult = await PerformAsync(transfer.EasyHandle, cancellationToken).ConfigureAwait(false);
            completionError = CreateCompletionError(performResult, transfer.State, cancellationToken);
            if (completionError is null)
            {
                PublishHeadersIfNeeded(transfer.State, transfer.EasyHandle);
            }
        }
        catch (Exception ex)
        {
            completionError = ex;
        }
        finally
        {
            transfer.State.CompleteBodyQueue(completionError);
            var pumpError = await AwaitBodyPumpAsync(bodyPumpTask).ConfigureAwait(false);
            completionError ??= pumpError;

            if (completionError is not null)
            {
                transfer.State.HeadersReady.TrySetException(completionError);
            }
            else
            {
                PublishHeadersIfNeeded(transfer.State, transfer.EasyHandle);
            }

            LogRequestCompletion(logContext, transfer.State, completionError, startedAtTimestamp);
            await bodyWriter.CompleteAsync(completionError).ConfigureAwait(false);
            CleanupTransfer(transfer);
        }
    }

    private static async Task PumpBodyAsync(CurlTransferState state, PipeWriter bodyWriter)
    {
        await foreach (var chunk in state.BodyChunks.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                var memory = bodyWriter.GetMemory(chunk.Length);
                chunk.Buffer.AsSpan(0, chunk.Length).CopyTo(memory.Span);
                bodyWriter.Advance(chunk.Length);

                var flushResult = await bodyWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                if (flushResult.IsCompleted)
                {
                    state.SetCallbackException(new IOException("Downstream body reader completed before upstream transfer finished."));
                    break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
            }
        }
    }

    private static async Task<Exception?> AwaitBodyPumpAsync(Task bodyPumpTask)
    {
        try
        {
            await bodyPumpTask.ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Task<CurlCode> PerformAsync(IntPtr easyHandle, CancellationToken cancellationToken)
    {
        return MultiDispatcher.EnqueueAsync(easyHandle, cancellationToken);
    }

    private static Exception? CreateCompletionError(
        CurlCode performResult,
        CurlTransferState transferState,
        CancellationToken cancellationToken)
    {
        if (transferState.CallbackException is not null)
        {
            return transferState.CallbackException;
        }

        if (performResult == CurlCode.Ok)
        {
            return null;
        }

        if (performResult == CurlCode.AbortedByCallback && cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException(cancellationToken);
        }

        return new InvalidOperationException($"{nameof(CurlNative.curl_easy_perform)} failed: {CurlNative.ToErrorMessage(performResult)} ({(int)performResult}).");
    }

    private static RequestLogContext CreateLogContext(CurlImpersonateRequest request)
    {
        var requestId = Interlocked.Increment(ref _requestSequence);
        var target = request.Uri.GetLeftPart(UriPartial.Path);
        var requestBytes = request.BodyLength
            ?? (!request.Body.IsEmpty ? request.Body.Length : (request.HasBody ? -1 : 0));
        return new RequestLogContext(
            requestId,
            request.Method,
            target,
            request.TimeoutMs,
            request.Headers.Count,
            requestBytes);
    }

    private void LogRequestStart(RequestLogContext context)
    {
        _logger.LogDebug(
            "curl request {RequestId} start: {Method} {Target}, timeoutMs={TimeoutMs}, headers={HeaderCount}, requestBytes={RequestBytes}.",
            context.RequestId,
            context.Method,
            context.Target,
            context.TimeoutMs,
            context.HeaderCount,
            context.RequestBytes);
    }

    private void LogRequestCompletion(
        RequestLogContext context,
        CurlTransferState state,
        Exception? completionError,
        long startedAtTimestamp)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startedAtTimestamp).TotalMilliseconds;
        if (completionError is null)
        {
            var statusCode = state.HeadersReady.Task.IsCompletedSuccessfully
                ? state.HeadersReady.Task.Result.StatusCode
                : 0;

            _logger.LogDebug(
                "curl request {RequestId} completed: {Method} {Target}, status={StatusCode}, responseBytes={ResponseBytes}, elapsedMs={ElapsedMs:F2}.",
                context.RequestId,
                context.Method,
                context.Target,
                statusCode,
                state.ResponseBodyBytes,
                elapsedMs);
            return;
        }

        if (completionError is OperationCanceledException)
        {
            _logger.LogDebug(
                "curl request {RequestId} canceled: {Method} {Target}, elapsedMs={ElapsedMs:F2}.",
                context.RequestId,
                context.Method,
                context.Target,
                elapsedMs);
            return;
        }

        _logger.LogWarning(
            completionError,
            "curl request {RequestId} failed: {Method} {Target}, elapsedMs={ElapsedMs:F2}.",
            context.RequestId,
            context.Method,
            context.Target,
            elapsedMs);
    }

    private IntPtr RentEasyHandle()
    {
        lock (_poolLock)
        {
            EnsureNotDisposed();
            if (_easyHandlePool.TryPop(out var pooled))
            {
                return pooled;
            }
        }

        var easyHandle = CurlNative.EasyInit();
        if (easyHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("curl_easy_init returned null.");
        }

        return easyHandle;
    }

    private void ReturnEasyHandle(IntPtr easyHandle)
    {
        if (easyHandle == IntPtr.Zero)
        {
            return;
        }

        lock (_poolLock)
        {
            if (_disposed)
            {
                CurlNative.EasyCleanup(easyHandle);
                return;
            }

            CurlNative.EasyReset(easyHandle);
            _easyHandlePool.Push(easyHandle);
        }
    }

    private static void PublishHeadersIfNeeded(CurlTransferState transferState, IntPtr easyHandle)
    {
        if (transferState.HeadersReady.Task.IsCompleted)
        {
            return;
        }

        var snapshot = CreateHeaderSnapshot(transferState.RawHeaderLines, easyHandle);
        transferState.HeadersReady.TrySetResult(snapshot);
    }

    private static HeaderSnapshot CreateHeaderSnapshot(IReadOnlyList<string> rawHeaderLines, IntPtr easyHandle)
    {
        var statusCode = GetStatusCode(easyHandle);
        var protocolVersion = GetProtocolVersion(easyHandle);
        var (parsedStatusCode, reasonPhrase, parsedProtocolVersion, headers) = CurlResponseHeaderParser.Parse(rawHeaderLines);
        if (parsedStatusCode > 0)
        {
            statusCode = parsedStatusCode;
        }

        return new HeaderSnapshot(statusCode, reasonPhrase, parsedProtocolVersion ?? protocolVersion, headers);
    }

    private void CleanupTransfer(PreparedTransfer transfer)
    {
        if (transfer.StateHandle.IsAllocated)
        {
            transfer.StateHandle.Free();
        }

        transfer.State.DisposeRequestBodyResources();

        if (transfer.HeaderList != IntPtr.Zero)
        {
            CurlNative.SListFreeAll(transfer.HeaderList);
        }

        ReturnEasyHandle(transfer.EasyHandle);
    }

    private static void ObserveFaults(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LibCurlImpersonateExecutor));
        }
    }

    private void EnsureGlobalInitialized()
    {
        lock (GlobalInitLock)
        {
            if (_globalInitialized)
            {
                return;
            }

            CurlNativeLibraryLoader.EnsureLoaded();
            var result = CurlNative.GlobalInit(CurlGlobalDefault);
            EnsureSuccess(result, "curl_global_init");
            _globalInitialized = true;
            _logger.LogInformation("curl_global_init completed for libcurl-impersonate runtime.");
        }
    }

    private static int GetStatusCode(IntPtr easyHandle)
    {
        var getInfoResult = CurlNative.EasyGetInfoLong(easyHandle, CurlInfo.ResponseCode, out var statusCodeLong);
        EnsureSuccess(getInfoResult, "curl_easy_getinfo(CURLINFO_RESPONSE_CODE)");
        return checked((int)statusCodeLong);
    }

    private static Version GetProtocolVersion(IntPtr easyHandle)
    {
        var getInfoResult = CurlNative.EasyGetInfoLong(easyHandle, CurlInfo.HttpVersion, out var protocolVersionLong);
        if (getInfoResult != CurlCode.Ok)
            return HttpVersion.Version11;

        return TryMapProtocolVersion(protocolVersionLong, out var protocolVersion)
            ? protocolVersion
            : HttpVersion.Version11;
    }

    private static bool TryMapProtocolVersion(long protocolVersionLong, out Version protocolVersion)
    {
        protocolVersion = HttpVersion.Version11;
        switch ((CurlHttpVersion)protocolVersionLong)
        {
            case CurlHttpVersion.Http1_0:
                protocolVersion = HttpVersion.Version10;
                return true;
            case CurlHttpVersion.Http1_1:
                protocolVersion = HttpVersion.Version11;
                return true;
            case CurlHttpVersion.Http2_0:
            case CurlHttpVersion.Http2Tls:
            case CurlHttpVersion.Http2PriorKnowledge:
                protocolVersion = HttpVersion.Version20;
                return true;
            case CurlHttpVersion.Http3:
            case CurlHttpVersion.Http3Only:
                protocolVersion = HttpVersion.Version30;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetContentLength(IReadOnlyList<CurlImpersonateHeader> headers, out int contentLength)
    {
        foreach (var header in headers)
        {
            if (!string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!long.TryParse(header.Value, out var parsedLong) || parsedLong < 0 || parsedLong > int.MaxValue)
            {
                break;
            }

            contentLength = (int)parsedLong;
            return true;
        }

        contentLength = 0;
        return false;
    }

    private static CurlCode TryImpersonate(IntPtr easyHandle, string impersonateTarget)
    {
        try
        {
            return CurlNative.EasyImpersonate(easyHandle, impersonateTarget, defaultHeaders: 0);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new NotSupportedException(
                "curl_easy_impersonate symbol was not found. Ensure curl-impersonate shared library is loaded.",
                ex);
        }
    }

    private string ResolveImpersonationTarget(string requestedTarget)
    {
        var normalizedRequestedTarget = requestedTarget.Trim();
        lock (_impersonationResolutionLock)
        {
            if (_resolvedImpersonationTargets.TryGetValue(normalizedRequestedTarget, out var cachedTarget))
            {
                return cachedTarget;
            }
        }

        var runtimeCapabilities = EnsureRuntimeCapabilities([normalizedRequestedTarget, .. _profileCandidates]);
        var resolvedTarget = ResolveByPolicy(runtimeCapabilities, normalizedRequestedTarget);
        lock (_impersonationResolutionLock)
        {
            _resolvedImpersonationTargets[normalizedRequestedTarget] = resolvedTarget;
        }

        if (!string.Equals(resolvedTarget, normalizedRequestedTarget, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Requested curl impersonation target {RequestedTarget} resolved to {ResolvedTarget} (policy={Policy}).",
                normalizedRequestedTarget,
                resolvedTarget,
                _profilePolicy);
        }

        return resolvedTarget;
    }

    private string ResolveByPolicy(RuntimeCapabilityCache runtimeCapabilities, string requestedTarget)
    {
        return _profilePolicy switch
        {
            CurlImpersonateProfilePolicy.Strict => IsTargetSupported(runtimeCapabilities, requestedTarget)
                ? requestedTarget
                : throw new InvalidOperationException(
                    $"Requested curl impersonation target '{requestedTarget}' is unsupported by current runtime. " +
                    $"Supported targets: {string.Join(", ", GetSupportedTargets(runtimeCapabilities))}."),
            CurlImpersonateProfilePolicy.HighestAvailable => SelectHighestSupportedCandidate(runtimeCapabilities)
                ?? throw new InvalidOperationException(
                    "No supported curl impersonation target found in configured candidates."),
            _ => ResolvePreferLower(runtimeCapabilities, requestedTarget),
        };
    }

    private string ResolvePreferLower(RuntimeCapabilityCache runtimeCapabilities, string requestedTarget)
    {
        if (IsTargetSupported(runtimeCapabilities, requestedTarget))
        {
            return requestedTarget;
        }

        if (TryParseChromeMajor(requestedTarget, out var requestedMajor))
        {
            var fallback = _profileCandidates
                .Where(candidate => IsTargetSupported(runtimeCapabilities, candidate))
                .Select(candidate => new CandidateRank(candidate, TryParseChromeMajor(candidate, out var major) ? major : int.MinValue))
                .Where(candidate => candidate.Major <= requestedMajor)
                .OrderByDescending(candidate => candidate.Major)
                .ThenBy(candidate => Array.IndexOf(_profileCandidates, candidate.Name))
                .Select(candidate => candidate.Name)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }
        }

        return SelectHighestSupportedCandidate(runtimeCapabilities)
            ?? throw new InvalidOperationException(
                $"curl_easy_impersonate could not resolve a supported target for '{requestedTarget}'. " +
                "Upgrade libcurl-impersonate or choose a supported target.");
    }

    private string? SelectHighestSupportedCandidate(RuntimeCapabilityCache runtimeCapabilities)
    {
        return _profileCandidates
            .Where(candidate => IsTargetSupported(runtimeCapabilities, candidate))
            .Select(candidate => new CandidateRank(candidate, TryParseChromeMajor(candidate, out var major) ? major : int.MinValue))
            .OrderByDescending(candidate => candidate.Major)
            .ThenBy(candidate => Array.IndexOf(_profileCandidates, candidate.Name))
            .Select(candidate => candidate.Name)
            .FirstOrDefault();
    }

    private RuntimeCapabilityCache EnsureRuntimeCapabilities(IReadOnlyList<string> targets)
    {
        var runtimeKey = GetRuntimeCapabilityCacheKey();
        var runtimeCapabilities = GetOrCreateRuntimeCapabilityCache(runtimeKey);
        ProbeMissingCapabilities(runtimeCapabilities, targets);
        return runtimeCapabilities;
    }

    private static RuntimeCapabilityCache GetOrCreateRuntimeCapabilityCache(string runtimeKey)
    {
        lock (RuntimeCapabilityCacheLock)
        {
            if (RuntimeCapabilityCaches.TryGetValue(runtimeKey, out var cached))
            {
                return cached;
            }

            var created = new RuntimeCapabilityCache();
            RuntimeCapabilityCaches[runtimeKey] = created;
            return created;
        }
    }

    private static string GetRuntimeCapabilityCacheKey()
    {
        var runtimePath = Environment.GetEnvironmentVariable("BSSL_CURL_IMPERSONATE_LIB");
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            return "runtime:default";
        }

        return $"runtime:{Path.GetFullPath(runtimePath.Trim())}";
    }

    private void ProbeMissingCapabilities(RuntimeCapabilityCache runtimeCapabilities, IReadOnlyList<string> targets)
    {
        var missing = targets
            .Where(static target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(target => !runtimeCapabilities.IsKnown(target))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var probeHandle = CurlNative.EasyInit();
        if (probeHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("curl_easy_init returned null while probing impersonation capabilities.");
        }

        try
        {
            foreach (var target in missing)
            {
                var result = TryImpersonate(probeHandle, target);
                if (result == CurlCode.BadFunctionArgument)
                {
                    runtimeCapabilities.SetSupport(target, false);
                    CurlNative.EasyReset(probeHandle);
                    continue;
                }

                if (result != CurlCode.Ok)
                {
                    EnsureSuccess(result, "curl_easy_impersonate");
                }

                runtimeCapabilities.SetSupport(target, true);
                CurlNative.EasyReset(probeHandle);
            }
        }
        finally
        {
            CurlNative.EasyCleanup(probeHandle);
        }
    }

    private static bool IsTargetSupported(RuntimeCapabilityCache runtimeCapabilities, string target)
    {
        return runtimeCapabilities.TryGetSupport(target, out var supported) && supported;
    }

    private static IReadOnlyList<string> GetSupportedTargets(RuntimeCapabilityCache runtimeCapabilities)
    {
        return runtimeCapabilities.GetSupportedTargets();
    }

    private static string[] NormalizeCandidates(IReadOnlyList<string>? profileCandidates)
    {
        var source = profileCandidates is { Count: > 0 }
            ? profileCandidates
            : CurlImpersonateDefaults.ProfileCandidates;

        var normalized = source
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(static candidate => candidate.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0
            ? [.. CurlImpersonateDefaults.ProfileCandidates]
            : normalized;
    }

    private static bool TryParseChromeMajor(string target, out int major)
    {
        major = 0;
        var span = target.AsSpan().Trim();
        if (!span.StartsWith("chrome".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var start = "chrome".Length;
        var end = start;
        while (end < span.Length && char.IsDigit(span[end]))
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        return int.TryParse(span[start..end], out major);
    }

    private static void SetOpt(IntPtr easyHandle, CurlOption option, string value)
    {
        var result = CurlNative.EasySetOptString(easyHandle, option, value);
        EnsureSuccess(result, $"curl_easy_setopt({option})");
    }

    private static void SetOpt(IntPtr easyHandle, CurlOption option, long value)
    {
        var result = CurlNative.EasySetOptLong(easyHandle, option, value);
        EnsureSuccess(result, $"curl_easy_setopt({option})");
    }

    private static void SetOpt(IntPtr easyHandle, CurlOption option, int value)
    {
        SetOpt(easyHandle, option, (long)value);
    }

    private static void SetOpt(IntPtr easyHandle, CurlOption option, IntPtr value)
    {
        var result = CurlNative.EasySetOptPointer(easyHandle, option, value);
        EnsureSuccess(result, $"curl_easy_setopt({option})");
    }

    private static void SetOpt(IntPtr easyHandle, CurlOption option, CurlNative.CurlWriteCallback callback)
    {
        var result = CurlNative.EasySetOptWriteCallback(easyHandle, option, callback);
        EnsureSuccess(result, $"curl_easy_setopt({option})");
    }

    private static void SetOpt(IntPtr easyHandle, CurlOption option, CurlNative.CurlXferInfoCallback callback)
    {
        var result = CurlNative.EasySetOptXferInfoCallback(easyHandle, option, callback);
        EnsureSuccess(result, $"curl_easy_setopt({option})");
    }

    private static void EnsureSuccess(CurlCode result, string operation)
    {
        if (result == CurlCode.Ok)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} failed: {CurlNative.ToErrorMessage(result)} ({(int)result}).");
    }

    private static void EnsureMultiSuccess(CurlMultiCode result, string operation)
    {
        if (result == CurlMultiCode.Ok)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} failed: {CurlNative.ToMultiErrorMessage(result)} ({(int)result}).");
    }

    private static nuint OnWriteBody(IntPtr buffer, nuint size, nuint nItems, IntPtr userData)
    {
        if (!TryGetTransferState(buffer, size, nItems, userData, out var state, out var totalSize))
        {
            return 0;
        }

        try
        {
            state.WriteBody(buffer, totalSize);
            return (nuint)totalSize;
        }
        catch (Exception ex)
        {
            state.SetCallbackException(ex);
            return 0;
        }
    }

    private static nuint OnWriteHeader(IntPtr buffer, nuint size, nuint nItems, IntPtr userData)
    {
        return OnWrite(buffer, size, nItems, userData, static (state, bytes) =>
        {
            var line = Encoding.ASCII.GetString(bytes).TrimEnd('\r', '\n');
            state.RawHeaderLines.Add(line);
            if (line.Length != 0)
            {
                return;
            }

            var (statusCode, reasonPhrase, protocolVersion, headers) = CurlResponseHeaderParser.Parse(state.RawHeaderLines);
            if (statusCode >= 200)
            {
                state.HeadersReady.TrySetResult(new HeaderSnapshot(
                    statusCode,
                    reasonPhrase,
                    protocolVersion ?? HttpVersion.Version11,
                    headers));
            }
        });
    }

    private static nuint OnReadBody(IntPtr buffer, nuint size, nuint nItems, IntPtr userData)
    {
        if (!TryGetTransferState(buffer, size, nItems, userData, out var state, out var totalSize))
            return 0;

        try
        {
            var bytesRead = state.ReadRequestBody(buffer, totalSize);
            return bytesRead >= 0 ? (nuint)bytesRead : CurlReadFuncAbort;
        }
        catch (Exception ex)
        {
            state.SetCallbackException(ex);
            return CurlReadFuncAbort;
        }
    }

    private static int OnXferInfo(
        IntPtr userData,
        long downloadTotal,
        long downloadNow,
        long uploadTotal,
        long uploadNow)
    {
        _ = downloadTotal;
        _ = downloadNow;
        _ = uploadTotal;
        _ = uploadNow;

        if (userData == IntPtr.Zero)
        {
            return 0;
        }

        var handle = GCHandle.FromIntPtr(userData);
        if (handle.Target is not CurlTransferState state)
        {
            return 1;
        }

        return state.ShouldCancelTransfer ? 1 : 0;
    }

    private static bool TryGetTransferState(
        IntPtr buffer,
        nuint size,
        nuint nItems,
        IntPtr userData,
        out CurlTransferState state,
        out int totalSize)
    {
        state = default!;
        totalSize = 0;
        if (buffer == IntPtr.Zero || userData == IntPtr.Zero)
        {
            return false;
        }

        var totalSizeLong = checked((long)size * (long)nItems);
        if (totalSizeLong <= 0 || totalSizeLong > int.MaxValue)
        {
            return false;
        }

        var handle = GCHandle.FromIntPtr(userData);
        if (handle.Target is not CurlTransferState transferState)
        {
            return false;
        }

        state = transferState;
        totalSize = (int)totalSizeLong;
        return true;
    }

    private static nuint OnWrite(
        IntPtr buffer,
        nuint size,
        nuint nItems,
        IntPtr userData,
        Action<CurlTransferState, byte[]> writer)
    {
        if (!TryGetTransferState(buffer, size, nItems, userData, out var state, out var totalSize))
        {
            return 0;
        }

        var managed = GC.AllocateUninitializedArray<byte>(totalSize);
        Marshal.Copy(buffer, managed, 0, managed.Length);

        try
        {
            writer(state, managed);
            return (nuint)totalSize;
        }
        catch (Exception ex)
        {
            state.SetCallbackException(ex);
            return 0;
        }
    }

    private sealed class PreparedTransfer(IntPtr easyHandle, CurlTransferState state)
    {
        public IntPtr EasyHandle { get; } = easyHandle;

        public CurlTransferState State { get; } = state;

        public IntPtr HeaderList { get; set; }

        public GCHandle StateHandle { get; set; }
    }

    private sealed class CurlTransferState(CancellationToken cancellationToken)
    {
        private const int UploadBufferSize = 64 * 1024;

        private readonly CancellationToken _cancellationToken = cancellationToken;
        private Exception? _callbackException;
        private long _responseBodyBytes;
        private long _uploadedBodyBytes;
        private Stream? _requestBodyStream;
        private byte[]? _uploadBuffer;

        public List<string> RawHeaderLines { get; } = [];

        public Channel<PooledChunk> BodyChunks { get; } = Channel.CreateBounded<PooledChunk>(new BoundedChannelOptions(MaxPendingBodyChunks)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        public TaskCompletionSource<HeaderSnapshot> HeadersReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? CallbackException => _callbackException;

        public bool ShouldCancelTransfer => _cancellationToken.IsCancellationRequested || _callbackException is not null;

        public long ResponseBodyBytes => Interlocked.Read(ref _responseBodyBytes);

        public long UploadedBodyBytes => Interlocked.Read(ref _uploadedBodyBytes);

        public void SetCallbackException(Exception exception)
        {
            Interlocked.CompareExchange(ref _callbackException, exception, null);
        }

        public void SetRequestBodyStream(Stream stream, long? length)
        {
            _ = length;
            _requestBodyStream = stream;
            _uploadBuffer = ArrayPool<byte>.Shared.Rent(UploadBufferSize);
        }

        public int ReadRequestBody(IntPtr destination, int requestedBytes)
        {
            if (_requestBodyStream is null || _uploadBuffer is null)
                return 0;

            var readSize = Math.Min(requestedBytes, _uploadBuffer.Length);
            if (readSize <= 0)
                return 0;

            var bytesRead = _requestBodyStream.Read(_uploadBuffer, 0, readSize);
            if (bytesRead <= 0)
                return 0;

            Marshal.Copy(_uploadBuffer, 0, destination, bytesRead);
            Interlocked.Add(ref _uploadedBodyBytes, bytesRead);
            return bytesRead;
        }

        public void WriteBody(IntPtr source, int length)
        {
            var chunk = ArrayPool<byte>.Shared.Rent(length);
            Marshal.Copy(source, chunk, 0, length);
            if (!BodyChunks.Writer.TryWrite(new PooledChunk(chunk, length)))
            {
                ArrayPool<byte>.Shared.Return(chunk);
                throw new InvalidOperationException("Body queue rejected a response chunk.");
            }

            Interlocked.Add(ref _responseBodyBytes, length);
        }

        public void CompleteBodyQueue(Exception? error)
        {
            if (error is null)
            {
                BodyChunks.Writer.TryComplete();
                return;
            }

            BodyChunks.Writer.TryComplete(error);
        }

        public void DisposeRequestBodyResources()
        {
            _requestBodyStream?.Dispose();
            _requestBodyStream = null;

            if (_uploadBuffer is null)
                return;

            ArrayPool<byte>.Shared.Return(_uploadBuffer);
            _uploadBuffer = null;
        }
    }

    private readonly record struct PooledChunk(byte[] Buffer, int Length);

    private readonly record struct CandidateRank(string Name, int Major);

    private sealed class RuntimeCapabilityCache
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, bool> _supportByTarget = new(StringComparer.OrdinalIgnoreCase);

        public bool IsKnown(string target)
        {
            lock (_lock)
            {
                return _supportByTarget.ContainsKey(target);
            }
        }

        public void SetSupport(string target, bool supported)
        {
            lock (_lock)
            {
                _supportByTarget[target] = supported;
            }
        }

        public bool TryGetSupport(string target, out bool supported)
        {
            lock (_lock)
            {
                return _supportByTarget.TryGetValue(target, out supported);
            }
        }

        public IReadOnlyList<string> GetSupportedTargets()
        {
            lock (_lock)
            {
                return _supportByTarget
                    .Where(static entry => entry.Value)
                    .Select(static entry => entry.Key)
                    .OrderBy(static entry => entry, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    private sealed class CurlMultiDispatcher
    {
        private const int PollTimeoutMs = 100;
        private readonly Lock _startLock = new();
        private readonly Channel<QueuedTransfer> _queue = Channel.CreateUnbounded<QueuedTransfer>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        private readonly Dictionary<IntPtr, QueuedTransfer> _active = [];
        private Thread? _thread;
        private IntPtr _multiHandle;

        public Task<CurlCode> EnqueueAsync(IntPtr easyHandle, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completion = new TaskCompletionSource<CurlCode>(TaskCreationOptions.RunContinuationsAsynchronously);
            EnsureStarted();
            if (!_queue.Writer.TryWrite(new QueuedTransfer(easyHandle, completion)))
            {
                completion.TrySetException(new InvalidOperationException("Failed to enqueue curl transfer."));
                return completion.Task;
            }

            TryWakeup();
            return completion.Task;
        }

        private void EnsureStarted()
        {
            lock (_startLock)
            {
                if (_thread is not null)
                {
                    return;
                }

                _thread = new Thread(RunLoop)
                {
                    Name = "BoringSsl.CurlMultiDispatcher",
                    IsBackground = true,
                };
                _thread.Start();
            }
        }

        private void RunLoop()
        {
            try
            {
                _multiHandle = CurlNative.MultiInit();
                if (_multiHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("curl_multi_init returned null.");
                }

                while (true)
                {
                    DrainQueue();
                    PerformAndComplete();

                    if (_active.Count == 0)
                    {
                        var next = _queue.Reader.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                        AddTransfer(next);
                        continue;
                    }

                    var pollResult = CurlNative.MultiPoll(_multiHandle, PollTimeoutMs, out _);
                    EnsureMultiSuccess(pollResult, "curl_multi_poll");
                }
            }
            catch (Exception ex)
            {
                FailAllTransfers(ex);
            }
            finally
            {
                if (_multiHandle != IntPtr.Zero)
                {
                    var cleanupResult = CurlNative.MultiCleanup(_multiHandle);
                    _multiHandle = IntPtr.Zero;
                    if (cleanupResult != CurlMultiCode.Ok)
                    {
                        _ = cleanupResult;
                    }
                }

                lock (_startLock)
                {
                    _thread = null;
                }
            }
        }

        private void DrainQueue()
        {
            while (_queue.Reader.TryRead(out var queued))
            {
                AddTransfer(queued);
            }
        }

        private void AddTransfer(QueuedTransfer queued)
        {
            var addResult = CurlNative.MultiAddHandle(_multiHandle, queued.EasyHandle);
            if (addResult != CurlMultiCode.Ok)
            {
                queued.Completion.TrySetException(
                    new InvalidOperationException(
                        $"curl_multi_add_handle failed: {CurlNative.ToMultiErrorMessage(addResult)} ({(int)addResult})."));
                return;
            }

            _active[queued.EasyHandle] = queued;
        }

        private void PerformAndComplete()
        {
            if (_active.Count == 0)
            {
                return;
            }

            var performResult = CurlNative.MultiPerform(_multiHandle, out _);
            EnsureMultiSuccess(performResult, "curl_multi_perform");

            while (CurlNative.MultiInfoRead(_multiHandle, out _, out var message, out var easyHandle, out var transferResult))
            {
                if (message != CurlMultiMessage.Done || easyHandle == IntPtr.Zero)
                {
                    continue;
                }

                _active.Remove(easyHandle, out var queued);
                var removeResult = CurlNative.MultiRemoveHandle(_multiHandle, easyHandle);
                if (removeResult != CurlMultiCode.Ok)
                {
                    queued.Completion.TrySetException(
                        new InvalidOperationException(
                            $"curl_multi_remove_handle failed: {CurlNative.ToMultiErrorMessage(removeResult)} ({(int)removeResult})."));
                    continue;
                }

                queued.Completion.TrySetResult(transferResult);
            }
        }

        private void TryWakeup()
        {
            if (_multiHandle == IntPtr.Zero)
            {
                return;
            }

            _ = CurlNative.MultiWakeup(_multiHandle);
        }

        private void FailAllTransfers(Exception exception)
        {
            foreach (var (_, queued) in _active)
            {
                queued.Completion.TrySetException(exception);
            }

            _active.Clear();

            while (_queue.Reader.TryRead(out var queued))
            {
                queued.Completion.TrySetException(exception);
            }
        }
    }

    private readonly record struct QueuedTransfer(
        IntPtr EasyHandle,
        TaskCompletionSource<CurlCode> Completion);

    private readonly record struct HeaderSnapshot(
        int StatusCode,
        string ReasonPhrase,
        Version ProtocolVersion,
        IReadOnlyList<CurlImpersonateHeader> Headers);

    private readonly record struct RequestLogContext(
        long RequestId,
        string Method,
        string Target,
        int TimeoutMs,
        int HeaderCount,
        long RequestBytes);

    private enum CurlCode
    {
        Ok = 0,
        AbortedByCallback = 42,
        BadFunctionArgument = 43,
    }

    private enum CurlMultiCode
    {
        Ok = 0,
    }

    private enum CurlMultiMessage
    {
        Done = 1,
    }

    private enum CurlHttpVersion
    {
        Http1_0 = 1,
        Http1_1 = 2,
        Http2_0 = 3,
        Http2Tls = 4,
        Http2PriorKnowledge = 5,
        Http3 = 30,
        Http3Only = 31,
    }

    private enum CurlInfo
    {
        ResponseCode = 0x200002,
        HttpVersion = 0x20002e,
    }

    private enum CurlOption
    {
        Url = 10002,
        CustomRequest = 10036,
        HttpHeader = 10023,
        Upload = 46,
        Post = 47,
        HttpVersion = 84,
        ReadFunction = 20012,
        ReadData = 10009,
        WriteFunction = 20011,
        WriteData = 10001,
        HeaderFunction = 20079,
        HeaderData = 10029,
        PostFields = 10015,
        InFileSizeLarge = 30115,
        PostFieldSizeLarge = 30120,
        TimeoutMs = 155,
        FollowLocation = 52,
        AcceptEncoding = 10102,
        NoSignal = 99,
        NoProgress = 43,
        XferInfoData = 10057,
        XferInfoFunction = 20219,
        Verbose = 41,
    }

    private static class CurlNative
    {
        private const string NativeLibrary = "boringssl_net_curlimp_shim";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nuint CurlWriteCallback(IntPtr buffer, nuint size, nuint nItems, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int CurlXferInfoCallback(IntPtr userData, long downloadTotal, long downloadNow, long uploadTotal, long uploadNow);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_global_init")]
        public static extern CurlCode curl_global_init(long flags);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_init")]
        public static extern IntPtr curl_easy_init();

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_cleanup")]
        public static extern void curl_easy_cleanup(IntPtr easyHandle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_reset")]
        public static extern void curl_easy_reset(IntPtr easyHandle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_setopt_long")]
        public static extern CurlCode curl_easy_setopt_long(IntPtr easyHandle, CurlOption option, long value);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_setopt_ptr")]
        public static extern CurlCode curl_easy_setopt_ptr(IntPtr easyHandle, CurlOption option, IntPtr value);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_setopt_str")]
        public static extern CurlCode curl_easy_setopt_str(IntPtr easyHandle, CurlOption option, string value);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_setopt_write_callback")]
        public static extern CurlCode curl_easy_setopt_callback(IntPtr easyHandle, CurlOption option, CurlWriteCallback callback);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_setopt_xferinfo_callback")]
        public static extern CurlCode curl_easy_setopt_xferinfo(IntPtr easyHandle, CurlOption option, CurlXferInfoCallback callback);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_perform")]
        public static extern CurlCode curl_easy_perform(IntPtr easyHandle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_getinfo_long")]
        public static extern CurlCode curl_easy_getinfo(IntPtr easyHandle, CurlInfo info, out long value);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_strerror")]
        public static extern IntPtr curl_easy_strerror(CurlCode code);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_slist_append")]
        public static extern IntPtr curl_slist_append(IntPtr list, string header);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_slist_free_all")]
        public static extern void curl_slist_free_all(IntPtr list);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_easy_impersonate")]
        public static extern CurlCode curl_easy_impersonate(IntPtr easyHandle, string target, int defaultHeaders);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_multi_init")]
        public static extern IntPtr curl_multi_init();

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_multi_cleanup")]
        public static extern CurlMultiCode curl_multi_cleanup(IntPtr multiHandle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_multi_add_handle")]
        public static extern CurlMultiCode curl_multi_add_handle(IntPtr multiHandle, IntPtr easyHandle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_multi_remove_handle")]
        public static extern CurlMultiCode curl_multi_remove_handle(IntPtr multiHandle, IntPtr easyHandle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_multi_perform")]
        public static extern CurlMultiCode curl_multi_perform(IntPtr multiHandle, out int runningHandles);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_multi_poll")]
        public static extern CurlMultiCode curl_multi_poll(IntPtr multiHandle, int timeoutMs, out int numFds);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_multi_info_read")]
        public static extern int curl_multi_info_read(
            IntPtr multiHandle,
            out int messagesInQueue,
            out int message,
            out IntPtr easyHandle,
            out CurlCode result);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_multi_wakeup")]
        public static extern CurlMultiCode curl_multi_wakeup(IntPtr multiHandle);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "bsn_curl_multi_strerror")]
        public static extern IntPtr curl_multi_strerror(CurlMultiCode code);

        public static CurlCode GlobalInit(long flags) => curl_global_init(flags);

        public static IntPtr EasyInit() => curl_easy_init();

        public static void EasyCleanup(IntPtr easyHandle) => curl_easy_cleanup(easyHandle);

        public static void EasyReset(IntPtr easyHandle) => curl_easy_reset(easyHandle);

        public static CurlCode EasySetOptLong(IntPtr easyHandle, CurlOption option, long value) =>
            curl_easy_setopt_long(easyHandle, option, value);

        public static CurlCode EasySetOptPointer(IntPtr easyHandle, CurlOption option, IntPtr value) =>
            curl_easy_setopt_ptr(easyHandle, option, value);

        public static CurlCode EasySetOptString(IntPtr easyHandle, CurlOption option, string value) =>
            curl_easy_setopt_str(easyHandle, option, value);

        public static CurlCode EasySetOptWriteCallback(IntPtr easyHandle, CurlOption option, CurlWriteCallback callback) =>
            curl_easy_setopt_callback(easyHandle, option, callback);

        public static CurlCode EasySetOptXferInfoCallback(IntPtr easyHandle, CurlOption option, CurlXferInfoCallback callback) =>
            curl_easy_setopt_xferinfo(easyHandle, option, callback);

        public static CurlCode EasyPerform(IntPtr easyHandle) => curl_easy_perform(easyHandle);

        public static CurlCode EasyGetInfoLong(IntPtr easyHandle, CurlInfo info, out long value) =>
            curl_easy_getinfo(easyHandle, info, out value);

        public static CurlCode EasyImpersonate(IntPtr easyHandle, string target, int defaultHeaders) =>
            curl_easy_impersonate(easyHandle, target, defaultHeaders);

        public static IntPtr MultiInit() => curl_multi_init();

        public static CurlMultiCode MultiCleanup(IntPtr multiHandle) => curl_multi_cleanup(multiHandle);

        public static CurlMultiCode MultiAddHandle(IntPtr multiHandle, IntPtr easyHandle) =>
            curl_multi_add_handle(multiHandle, easyHandle);

        public static CurlMultiCode MultiRemoveHandle(IntPtr multiHandle, IntPtr easyHandle) =>
            curl_multi_remove_handle(multiHandle, easyHandle);

        public static CurlMultiCode MultiPerform(IntPtr multiHandle, out int runningHandles) =>
            curl_multi_perform(multiHandle, out runningHandles);

        public static CurlMultiCode MultiPoll(IntPtr multiHandle, int timeoutMs, out int numFds) =>
            curl_multi_poll(multiHandle, timeoutMs, out numFds);

        public static bool MultiInfoRead(
            IntPtr multiHandle,
            out int messagesInQueue,
            out CurlMultiMessage message,
            out IntPtr easyHandle,
            out CurlCode result)
        {
            var hasMessage = curl_multi_info_read(multiHandle, out messagesInQueue, out var messageValue, out easyHandle, out result);
            message = (CurlMultiMessage)messageValue;
            return hasMessage != 0;
        }

        public static CurlMultiCode MultiWakeup(IntPtr multiHandle) => curl_multi_wakeup(multiHandle);

        public static IntPtr SListAppend(IntPtr list, string header) => curl_slist_append(list, header);

        public static void SListFreeAll(IntPtr list) => curl_slist_free_all(list);

        public static string ToErrorMessage(CurlCode code)
        {
            var pointer = curl_easy_strerror(code);
            return pointer == IntPtr.Zero
                ? "Unknown libcurl error"
                : Marshal.PtrToStringAnsi(pointer) ?? "Unknown libcurl error";
        }

        public static string ToMultiErrorMessage(CurlMultiCode code)
        {
            var pointer = curl_multi_strerror(code);
            return pointer == IntPtr.Zero
                ? "Unknown libcurl multi error"
                : Marshal.PtrToStringAnsi(pointer) ?? "Unknown libcurl multi error";
        }
    }
}
