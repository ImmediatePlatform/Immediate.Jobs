using System.Runtime.InteropServices;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	/// <inheritdoc />
	public async ValueTask UpsertRecurringAsync(RecurringJobSchedule schedule, CancellationToken cancellationToken = default)
	{
		UpsertRecurringAsyncCalled(schedule.Name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (_jobDefinitions.TryGetValue(schedule.JobName, out var definition))
			{
				schedule = schedule with { JobName = definition.Name };
			}

			ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(_recurring, schedule.Name, out _);

			current = current switch
			{
				{ IsCodeDefined: true } when !schedule.IsCodeDefined =>
					throw new ImmediateJobException("Code-defined recurring schedules cannot be replaced by dynamic schedules."),

				{ } =>
					schedule with
					{
						Name = current.Name,
						IsPaused = current.IsPaused || (schedule.CancellationReason ?? current.CancellationReason) is not null,
						LastRunAt = current.LastRunAt,
						CancellationReason = schedule.CancellationReason ?? current.CancellationReason,
					},

				_ => schedule,
			};
		}
	}

	/// <inheritdoc />
	public async ValueTask RemoveRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		RemoveRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_recurring.TryGetValue(name, out var schedule))
				throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");
			if (schedule.IsCodeDefined)
				throw new ImmediateJobException("Code-defined recurring schedules cannot be deleted.");

			_recurring.Remove(name);
		}
	}

	/// <inheritdoc />
	public async ValueTask PauseRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		PauseRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await SetRecurringPausedAsync(name, isPaused: true, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask ResumeRecurringAsync(string name, CancellationToken cancellationToken = default)
	{
		ResumeRecurringAsyncCalled(name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await SetRecurringPausedAsync(name, isPaused: false, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RecurringJobSchedule>> GetDueRecurringAsync(
		DateTimeOffset now,
		int batchSize,
		IReadOnlyList<string> jobNames,
		CancellationToken cancellationToken = default
	)
	{
		GetDueRecurringAsyncCalled(batchSize);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			return
			[
				.. _recurring.Values.Where(x => !x.IsPaused && x.NextRunAt <= now && jobNames.Contains(x.JobName, StringComparer.OrdinalIgnoreCase)).OrderBy(x => x.NextRunAt).Take(batchSize),
			];
		}
	}

	/// <inheritdoc />
	public async ValueTask<bool> MaterializeRecurringAsync(
		RecurringJobSchedule schedule,
		JobRecord job,
		DateTimeOffset nextRunAt,
		IReadOnlyList<JobContinuationEdge>? dependencies = null,
		CancellationToken cancellationToken = default
	)
	{
		MaterializeRecurringAsyncCalled(job.JobHandle, schedule.Name);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			if (!_recurring.TryGetValue(schedule.Name, out var current) || current.IsPaused || current.NextRunAt != schedule.NextRunAt)
				return false;

			var inserted = job.RecurringKey is null || _recurringKeys.Add(job.RecurringKey);
			if (inserted)
			{
				SetJob(job with { QueueName = null });

				if (dependencies is { })
				{
					_edges.AddRange(dependencies);
					EvaluateAlreadyTerminalParents(dependencies);
				}
			}

			_recurring[schedule.Name] = current with { LastRunAt = schedule.NextRunAt, NextRunAt = nextRunAt };
			return inserted;
		}
	}

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock; this method acquires it internally.
	/// </remarks>
	private ValueTask SetRecurringPausedAsync(string name, bool isPaused, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_gate)
		{
			if (!_recurring.TryGetValue(name, out var schedule))
				throw new KeyNotFoundException($"Recurring schedule '{name}' was not found.");

			if (!isPaused && schedule.CancellationReason is not null)
				throw new ImmediateJobException("Canceled recurring schedules cannot be resumed.");

			_recurring[name] = schedule with { IsPaused = isPaused };
		}

		return ValueTask.CompletedTask;
	}

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryUpsertRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.UpsertRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "UpsertRecurringAsync called (Schedule={Schedule})"
	)]
	private partial void UpsertRecurringAsyncCalled(string schedule);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryRemoveRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.RemoveRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "RemoveRecurringAsync called (Name={Name})"
	)]
	private partial void RemoveRecurringAsyncCalled(string name);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryPauseRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.PauseRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "PauseRecurringAsync called (Name={Name})"
	)]
	private partial void PauseRecurringAsyncCalled(string name);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryResumeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.ResumeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "ResumeRecurringAsync called (Name={Name})"
	)]
	private partial void ResumeRecurringAsyncCalled(string name);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryGetDueRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.GetDueRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetDueRecurringAsync called (BatchSize={BatchSize})"
	)]
	private partial void GetDueRecurringAsyncCalled(int batchSize);

	/// <remarks>
	/// 	The caller does not need to hold the <see cref="_gate"/> lock.
	/// </remarks>
	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryMaterializeRecurringAsyncCalled,
		EventName = "Immediate.Jobs.Shared.MaterializeRecurringAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MaterializeRecurringAsync called (JobHandle={JobHandle}, Schedule={Schedule})"
	)]
	private partial void MaterializeRecurringAsyncCalled(JobHandle jobHandle, string schedule);
}
