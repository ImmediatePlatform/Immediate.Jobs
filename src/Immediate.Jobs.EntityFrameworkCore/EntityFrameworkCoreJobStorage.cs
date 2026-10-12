using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Immediate.Jobs.EntityFrameworkCore;

/// <summary>An optimistic-concurrency EF Core implementation of <see cref="IJobStorage"/>.</summary>
/// <typeparam name="TContext">The application context containing the Immediate.Jobs model.</typeparam>
/// <param name="contextFactory">The factory used to create application database contexts.</param>
/// <param name="timeProvider">The clock used for storage timestamps, or <see langword="null"/> to use the system clock.</param>
/// <param name="logger">The logger used to record storage operations.</param>
internal sealed partial class EntityFrameworkCoreJobStorage<TContext>(
	IDbContextFactory<TContext> contextFactory,
	TimeProvider? timeProvider = null,
	ILogger<EntityFrameworkCoreJobStorage<TContext>>? logger = null
) : IJobStorage, IJobGraphStorage
	where TContext : DbContext
{
	[SuppressMessage("Performance", "CA1823:Avoid unused private fields", Justification = "Used by generated logger methods")]
	[SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Used by generated logger methods")]
	private readonly ILogger _logger = logger ?? NullLogger<EntityFrameworkCoreJobStorage<TContext>>.Instance;

	private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		DisposeAsyncCalled();
		await TaskScheduler.Yield();
	}

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		InitializeAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		_ = context.Model.FindEntityType(typeof(ImmediateJobEntity))
			?? throw new ImmediateJobException("Immediate.Jobs entities are not configured. Call modelBuilder.AddImmediateJobs() from OnModelCreating.");
	}

	/// <summary>
	///		Used for testing to pre-load various values to the storage before the test starts.
	/// </summary>
	/// <param name="jobs">
	///		The jobs that should be loaded in the database.
	/// </param>
	/// <param name="batches">
	///		The batches that should be loaded in the database.
	/// </param>
	/// <param name="edges">
	///		The continuation edges that should be loaded in the database.
	/// </param>
	/// <param name="recurringSchedules">
	///		The recurring schedules that should be loaded in the database.
	/// </param>
	/// <remarks>
	///	    This method should run before any other methods run to initialize test state. Use in regular app code is not
	///	    supported.
	/// </remarks>
	public async ValueTask LoadPersistedJobState(
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<BatchRecord> batches,
		IReadOnlyList<JobContinuationEdge> edges,
		IReadOnlyList<RecurringJobSchedule> recurringSchedules
	)
	{
		LoadPersistedJobStateCalled(jobs.Count, edges.Count);
		await using var context = await contextFactory.CreateDbContextAsync();

		context.AddRange(
			batches.Select(batch => new ImmediateJobBatchEntity
			{
				Id = batch.BatchHandle.Value,
				CreatedAt = batch.CreatedAt,
				TotalJobs = batch.TotalJobs,
				PendingCount = batch.PendingCount,
				SucceededCount = batch.SucceededCount,
				FailedCount = batch.FailedCount,
				CancelledCount = batch.CancelledCount,
				SkippedCount = batch.SkippedCount,
				StartedAt = batch.StartedAt,
				CompletedAt = batch.CompletedAt,
				State = batch.State,
				ConcurrencyStamp = Guid.NewGuid(),
			})
		);

		context.AddRange(jobs.Select(ToEntity));
		context.AddRange(edges.Select(ToEntity));
		context.AddRange(recurringSchedules.Select(ToEntity));

		await context.SaveChangesAsync();
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		HeartbeatAsyncCalled(server.WorkerId);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
		_ = await context.Set<ImmediateJobServerEntity>()
			.Where(item => item.ExpiresAt < server.LastHeartbeat)
			.ExecuteDeleteAsync(cancellationToken);
		var entity = await context.Set<ImmediateJobServerEntity>().FindAsync([server.WorkerId], cancellationToken);
		if (entity is null)
		{
			_ = context.Add(new ImmediateJobServerEntity
			{
				WorkerId = server.WorkerId,
				LastHeartbeat = server.LastHeartbeat,
				ExpiresAt = server.LastHeartbeat + server.ServerTimeout,
				ActiveWorkers = server.ActiveWorkers,
				MaxWorkers = server.MaxWorkers,
				Details = JsonSerializer.Serialize(server, EntityFrameworkCoreJsonSerializerContext.Default.JobServerSnapshot),
			});
		}
		else
		{
			entity.LastHeartbeat = server.LastHeartbeat;
			entity.ExpiresAt = server.LastHeartbeat + server.ServerTimeout;
			entity.ActiveWorkers = server.ActiveWorkers;
			entity.MaxWorkers = server.MaxWorkers;
			entity.Details = JsonSerializer.Serialize(server, EntityFrameworkCoreJsonSerializerContext.Default.JobServerSnapshot);
		}

		_ = await context.SaveChangesAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		IsHealthyAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		try
		{
			await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
			return await context.Database.CanConnectAsync(cancellationToken);
		}
		catch (Exception exception) when (exception is DbException or InvalidOperationException)
		{
			return false;
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.DisposeAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.DisposeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DisposeAsync called"
	)]
	private partial void DisposeAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.InitializeAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.InitializeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "InitializeAsync called"
	)]
	private partial void InitializeAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.LoadPersistedJobStateCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.LoadPersistedJobStateCalled",
		Level = LogLevel.Debug,
		Message = "LoadPersistedJobState called (Jobs={Jobs}, Edges={Edges})"
	)]
	private partial void LoadPersistedJobStateCalled(int jobs, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.HeartbeatAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.HeartbeatAsyncCalled",
		Level = LogLevel.Debug,
		Message = "HeartbeatAsync called (Server={Server})"
	)]
	private partial void HeartbeatAsyncCalled(string server);

	[LoggerMessage(
		EventId = LibraryEventIds.IsHealthyAsyncCalled,
		EventName = "Immediate.Jobs.EntityFrameworkCore.IsHealthyAsyncCalled",
		Level = LogLevel.Debug,
		Message = "IsHealthyAsync called"
	)]
	private partial void IsHealthyAsyncCalled();
}
