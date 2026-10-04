using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Immediate.Jobs.LinqToDB;

/// <summary>An optimistic-concurrency LinqToDB implementation of <see cref="IJobStorage"/>.</summary>
internal sealed partial class LinqToDBJobStorage<T>(
	Owned<T> contextScope,
	IOptions<LinqToDBJobStorageOptions> options,
	TimeProvider timeProvider,
	ILogger<LinqToDBJobStorage<T>>? logger = null
) : IJobGraphStorage
	where T : DataConnection
{
	[SuppressMessage("Performance", "CA1823:Avoid unused private fields", Justification = "Used by generated logger methods")]
	[SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Used by generated logger methods")]
	private readonly ILogger _logger = logger ?? NullLogger<LinqToDBJobStorage<T>>.Instance;

	private readonly string? _schema = options.Value.Schema;

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		DisposeAsyncCalled();
		await TaskScheduler.Yield();
		await ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		InitializeAsyncCalled();
		await TaskScheduler.Yield();
		await ValueTask.CompletedTask;
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
		await using var scope = contextScope.GetScope(out var connection);

		await connection.BulkCopyAsync(
			new BulkCopyOptions { SchemaName = _schema },
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

		await connection.BulkCopyAsync(
			new BulkCopyOptions { SchemaName = _schema },
			jobs.Select(ToEntity)
		);

		await connection.BulkCopyAsync(
			new BulkCopyOptions { SchemaName = _schema },
			edges.Select(ToEntity)
		);

		await connection.BulkCopyAsync(
			new BulkCopyOptions { SchemaName = _schema },
			recurringSchedules.Select(ToEntity)
		);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		HeartbeatAsyncCalled(server.WorkerId);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		await using var scope = contextScope.GetScope(out var connection);

		_ = await Servers(connection)
			.Where(entity => entity.ExpiresAt < server.LastHeartbeat)
			.DeleteAsync(cancellationToken);
		var updated = await Servers(connection)
			.Where(entity => entity.WorkerId == server.WorkerId)
			.Set(entity => entity.LastHeartbeat, server.LastHeartbeat)
			.Set(entity => entity.ExpiresAt, server.LastHeartbeat + server.ServerTimeout)
			.Set(entity => entity.ActiveWorkers, server.ActiveWorkers)
			.Set(entity => entity.MaxWorkers, server.MaxWorkers)
			.Set(entity => entity.Details, JsonSerializer.Serialize(server, LinqToDBJsonSerializerContext.Default.JobServerSnapshot))
			.UpdateAsync(cancellationToken);
		if (updated != 0)
			return;
		try
		{
			_ = await InsertAsync(connection, new ImmediateJobServerEntity
			{
				WorkerId = server.WorkerId,
				LastHeartbeat = server.LastHeartbeat,
				ExpiresAt = server.LastHeartbeat + server.ServerTimeout,
				ActiveWorkers = server.ActiveWorkers,
				MaxWorkers = server.MaxWorkers,
				Details = JsonSerializer.Serialize(server, LinqToDBJsonSerializerContext.Default.JobServerSnapshot),
			}, cancellationToken);
		}
		catch (DbException)
		{
			_ = await Servers(connection)
				.Where(entity => entity.WorkerId == server.WorkerId)
				.Set(entity => entity.LastHeartbeat, server.LastHeartbeat)
				.Set(entity => entity.ExpiresAt, server.LastHeartbeat + server.ServerTimeout)
				.Set(entity => entity.ActiveWorkers, server.ActiveWorkers)
				.Set(entity => entity.MaxWorkers, server.MaxWorkers)
				.Set(entity => entity.Details, JsonSerializer.Serialize(server, LinqToDBJsonSerializerContext.Default.JobServerSnapshot))
				.UpdateAsync(cancellationToken);
		}
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		IsHealthyAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		try
		{
			await using var scope = contextScope.GetScope(out var connection);

			_ = await connection.ExecuteAsync("SELECT 1", cancellationToken);
			return true;
		}
		catch (Exception exception) when (exception is DbException or InvalidOperationException)
		{
			return false;
		}
	}

#pragma warning restore CA1032, CA1064

	[LoggerMessage(
		EventId = LibraryEventIds.DisposeAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.DisposeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DisposeAsync called"
	)]
	private partial void DisposeAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.InitializeAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.InitializeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "InitializeAsync called"
	)]
	private partial void InitializeAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.LoadPersistedJobStateCalled,
		EventName = "Immediate.Jobs.LinqToDB.LoadPersistedJobStateCalled",
		Level = LogLevel.Debug,
		Message = "LoadPersistedJobState called (Jobs={Jobs}, Edges={Edges})"
	)]
	private partial void LoadPersistedJobStateCalled(int jobs, int edges);

	[LoggerMessage(
		EventId = LibraryEventIds.HeartbeatAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.HeartbeatAsyncCalled",
		Level = LogLevel.Debug,
		Message = "HeartbeatAsync called (Server={Server})"
	)]
	private partial void HeartbeatAsyncCalled(string server);

	[LoggerMessage(
		EventId = LibraryEventIds.IsHealthyAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.IsHealthyAsyncCalled",
		Level = LogLevel.Debug,
		Message = "IsHealthyAsync called"
	)]
	private partial void IsHealthyAsyncCalled();
}
