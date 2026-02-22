namespace BoringSsl.Net.CurlImpersonate;

public interface ICurlImpersonateStreamingExecutor : ICurlImpersonateExecutor
{
    Task<CurlImpersonateStreamingResponse> ExecuteStreamingAsync(
        CurlImpersonateRequest request,
        CancellationToken cancellationToken);
}
