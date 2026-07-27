using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Context;

public sealed record OriginatingRequestContext(string ClientIpAddress, string UserAgent);

public sealed class CurrentRequestContext
{
	public OriginatingRequestContext? Value { get; set; }
}

public sealed class RequestContextExtractor(
	IHttpContextAccessor httpContextAccessor,
	CurrentRequestContext currentRequestContext
) : IJobContextExtractor<OriginatingRequestContext>
{
	public string Key => "http-request";

	public ValueTask<OriginatingRequestContext?> CaptureAsync(CancellationToken cancellationToken)
	{
		if (httpContextAccessor.HttpContext is not { } httpContext)
			return ValueTask.FromResult<OriginatingRequestContext?>(null);

		var context = new OriginatingRequestContext(
			httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
			httpContext.Request.Headers.UserAgent.ToString()
		);
		return ValueTask.FromResult<OriginatingRequestContext?>(context);
	}

	public ValueTask RestoreAsync(
		OriginatingRequestContext context,
		CancellationToken cancellationToken
	)
	{
		currentRequestContext.Value = context;
		return ValueTask.CompletedTask;
	}
}
