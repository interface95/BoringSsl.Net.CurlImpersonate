namespace BoringSsl.Net.CurlImpersonate;

public interface ICurlImpersonateExecutor
{
    Task<CurlImpersonateResponse> ExecuteAsync(CurlImpersonateRequest request, CancellationToken cancellationToken);
}
