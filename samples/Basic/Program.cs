using Immediate.Jobs.Shared;
using Immediate.Jobs.Dashboard;
using Immediate.Handlers.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();
builder.Services.AddImmediateJobs(options =>
{
	_ = options.UseInMemory();
	options.MaxParallelJobs = 4;
}).AddHealthCheck();

var app = builder.Build();
app.MapPost("/welcome/{userId:guid}", async (
	Guid userId,
	SendWelcomeEmail.Scheduler scheduler,
	CancellationToken cancellationToken
) =>
{
	var jobId = await scheduler.EnqueueAsync(new(userId, "v2"), cancellationToken);
	return Results.Accepted($"/jobs/api/jobs?search=send-welcome-email", new { jobId = jobId.Id });
});
app.MapImmediateJobsDashboard("/jobs");
await app.RunAsync();

[Handler, Job("send-welcome-email", MaxAttempts = 5, Timeout = "00:02:00")]
public sealed partial class SendWelcomeEmail(IEmailSender sender)
{
	public sealed record Payload(Guid UserId, string Template);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		new(sender.SendAsync(payload.UserId, payload.Template, cancellationToken));
}

[Handler, Job(Cron = "0 */5 * * * *")]
public sealed partial class CleanupSessionsJob(ILogger<CleanupSessionsJob> logger)
{
	private ValueTask HandleAsync(NoPayload request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		logger.LogInformation("Cleaning expired sessions for job {JobId}", request.JobDetails?.JobId);
		return ValueTask.CompletedTask;
	}
}

public interface IEmailSender
{
	Task SendAsync(Guid userId, string template, CancellationToken cancellationToken);
}

public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
	public Task SendAsync(Guid userId, string template, CancellationToken cancellationToken)
	{
		logger.LogInformation("Sending template {Template} to user {UserId}", template, userId);
		return Task.CompletedTask;
	}
}
