using Immediate.Jobs.Shared;
using Immediate.Handlers.Shared;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddScoped<CurrentGreetingContext>();
services.AddImmediateJobs(options => options.UseInMemory());

await using var provider = services.BuildServiceProvider();

await using (var scope = provider.CreateAsyncScope())
{
	const string ExpectedContext = "captured before enqueue";
	var currentContext = scope.ServiceProvider.GetRequiredService<CurrentGreetingContext>();
	currentContext.Value = ExpectedContext;

	var scheduler = scope.ServiceProvider.GetRequiredService<AotGreetingJob.Scheduler>();
	_ = await scheduler.Enqueue(new("Native AOT", ExpectedContext));
}

await provider.GetRequiredService<JobSchedulerService>().DrainAsync();

public sealed record GreetingContext(string Value);

public sealed class CurrentGreetingContext
{
	public string? Value { get; set; }
}

public sealed class GreetingContextExtractor(CurrentGreetingContext currentContext)
	: IJobContextExtractor<GreetingContext>
{
	public string Key => "greeting";

	public ValueTask<GreetingContext?> CaptureAsync(CancellationToken cancellationToken) =>
		ValueTask.FromResult(
			currentContext.Value is { } value ? new GreetingContext(value) : null
		);

	public ValueTask RestoreAsync(GreetingContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		cancellationToken.ThrowIfCancellationRequested();
		currentContext.Value = context.Value;
		return ValueTask.CompletedTask;
	}
}

[Handler, Job("aot-greeting"), UsesJobContext<GreetingContextExtractor>]
public sealed partial class AotGreetingJob(CurrentGreetingContext currentContext)
{
	public sealed record Payload(string Name, string ExpectedContext);

	private Task WriteAsync(Payload payload, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (currentContext.Value != payload.ExpectedContext)
		{
			throw new InvalidOperationException("The enqueue-time context was not restored.");
		}

		Console.WriteLine($"Hello, {payload.Name}! Restored context: {currentContext.Value}");
		return Task.CompletedTask;
	}

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		new(WriteAsync(payload, cancellationToken));
}
