using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.StorageTests;

internal static class PostgreSqlJobSearchAssertions
{
	internal static async Task AssertUnicodeSearchAsync(IServiceProvider services, CancellationToken cancellationToken)
	{
		await using var scope = services.CreateAsyncScope();
		var storage = scope.ServiceProvider.GetRequiredService<IJobStorage>();
		await storage.InitializeAsync(cancellationToken);
		var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
		var expected = JobHandle.FromString("unicode-search");
		await storage.EnqueueAsync(new()
		{
			JobHandle = expected,
			JobName = "RésuméJob",
			QueueName = "default",
			Payload = "{}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
		}, cancellationToken);

		foreach (var search in new[] { "résumé", "RÉSUMÉ", "SuMé" })
		{
			var matches = await storage.QueryJobsAsync(new() { Search = search }, cancellationToken);
			Assert.Equal(expected, Assert.Single(matches).JobHandle);
		}

		var unaccented = await storage.QueryJobsAsync(new() { Search = "resume" }, cancellationToken);
		Assert.Empty(unaccented);
	}
}
