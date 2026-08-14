using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

namespace Immediate.Jobs.Dashboard;

[Handler]
[MapPost("recurring/{name}/trigger")]
[MapGroup<DashboardApi>]
internal static partial class TriggerDashboardRecurringJob
{
	[Validate]
	internal sealed partial record Command : IValidationTarget<Command>
	{
		[FromRoute]
		[NotEmpty]
		public required string Name { get; init; }
	}

	internal static Results<StatusCodeHttpResult, NotFound, ProblemHttpResult> TransformResult(
		DashboardMutationResult result
	) => DashboardApiEndpointOperations.TransformTriggerResult(result);

	private static async ValueTask<DashboardMutationResult> HandleAsync(
		Command command,
		IServiceProvider serviceProvider,
		IJobStorage storage,
		IEnumerable<JobDefinition> definitions,
		CancellationToken cancellationToken
	)
	{
		var snapshot = await storage.GetMonitoringSnapshotAsync(cancellationToken);
		var schedule = snapshot.Recurring.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, command.Name, StringComparison.Ordinal));
		if (schedule is null)
			return DashboardMutationResult.NotFound;

		var definition = definitions.FirstOrDefault(candidate =>
			string.Equals(candidate.Name, schedule.JobName, StringComparison.Ordinal));
		if (definition is null)
		{
			return DashboardMutationResult.Conflict(
				$"No generated job definition exists for '{schedule.JobName}'."
			);
		}

		var idGenerator = serviceProvider.GetRequiredService<IIdGenerator>();
		var now = serviceProvider.GetService<TimeProvider>()?.GetUtcNow() ?? TimeProvider.System.GetUtcNow();
		var job = new JobRecord
		{
			Id = idGenerator.CreateId(IdKind.Job),
			JobName = schedule.JobName,
			QueueName = definition.Queue.Name,
			Payload = "{}",
			State = JobState.Pending,
			DueAt = now,
			CreatedAt = now,
		};
		await storage.EnqueueAsync(job, cancellationToken);
		return DashboardMutationResult.Accepted;
	}
}
