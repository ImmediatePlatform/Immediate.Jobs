using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
///		A best-effort, non-durable, single-node provider intended for development and tests.
/// </summary>
/// <param name="timeProvider">
/// 	The clock used for scheduling, leases, and timestamps.
/// </param>
/// <param name="logger">The logger used to record storage operations.</param>
/// <remarks>
///		Public use should only be done via <see cref="IImmediateJobsStorageBuilder.UseInMemory"/>.
/// </remarks>
[SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Not publicly usable; arguments are validated by internal consumers.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed partial class InMemoryJobStorage(
	TimeProvider timeProvider,
	ILogger<InMemoryJobStorage>? logger = null
) :
	IJobStorage,
	IJobGraphStorage
{
	[SuppressMessage("Performance", "CA1823:Avoid unused private fields", Justification = "Used by generated logger methods")]
	[SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Used by generated logger methods")]
	private readonly ILogger _logger = logger ?? NullLogger<InMemoryJobStorage>.Instance;

	private readonly Lock _gate = new();

	private readonly Dictionary<JobHandle, JobRecord> _jobs = [];

	private readonly Dictionary<JobHandle, SortedDictionary<int, JobExecutionRecord>> _executions = [];

	private readonly Dictionary<BatchHandle, BatchRecord> _batches = [];

	private readonly List<JobContinuationEdge> _edges = [];

	private readonly HashSet<JobContinuationEdge> _settledEdges = [];

	private readonly Dictionary<string, RecurringJobSchedule> _recurring = [with(StringComparer.Ordinal)];

	private readonly Dictionary<string, JobServerSnapshot> _servers = [with(StringComparer.Ordinal)];

	private readonly HashSet<string> _recurringKeys = [with(StringComparer.Ordinal)];

	private readonly Dictionary<(string QueueName, string GroupId), long> _fairQueueLastServed = [];

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		DisposeAsyncCalled();
	}

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		InitializeAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
	}

	/// <summary>
	///	    Used for testing to pre-load various values to the storage before the test starts.
	/// </summary>
	/// <param name="jobs">
	///	    The jobs that should be loaded in the database.
	/// </param>
	/// <param name="batches">
	///	    The batches that should be loaded in the database.
	/// </param>
	/// <param name="edges">
	///	    The continuation edges that should be loaded in the database.
	/// </param>
	/// <param name="recurringSchedules">
	///	    The recurring schedules that should be loaded in the database.
	/// </param>
	/// <remarks>
	///	    This method should run before any other methods run to initialize test state. Use in regular app code is not
	///	    supported.
	/// </remarks>
	public void LoadPersistedJobState(
		IReadOnlyList<JobRecord> jobs,
		IReadOnlyList<BatchRecord> batches,
		IReadOnlyList<JobContinuationEdge> edges,
		IReadOnlyList<RecurringJobSchedule> recurringSchedules
	)
	{
		lock (_gate)
		{
			foreach (var b in batches)
				_batches[b.BatchHandle] = b;

			foreach (var j in jobs)
				_jobs[j.JobHandle] = j;

			_edges.AddRange(edges);

			foreach (var schedule in recurringSchedules)
				_recurring[schedule.Name] = schedule;
		}
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		HeartbeatAsyncCalled(server.WorkerId);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		lock (_gate)
		{
			foreach (var workerId in _servers
				.Where(item => item.Value.LastHeartbeat + item.Value.ServerTimeout < server.LastHeartbeat)
				.Select(static item => item.Key)
				.ToList())
			{
				_ = _servers.Remove(workerId);
			}

			_servers[server.WorkerId] = server;
		}
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		IsHealthyAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		return true;
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryDisposeAsyncCalled,
		EventName = "Immediate.Jobs.Shared.DisposeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DisposeAsync called"
	)]
	private partial void DisposeAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryInitializeAsyncCalled,
		EventName = "Immediate.Jobs.Shared.InitializeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "InitializeAsync called"
	)]
	private partial void InitializeAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryHeartbeatAsyncCalled,
		EventName = "Immediate.Jobs.Shared.HeartbeatAsyncCalled",
		Level = LogLevel.Debug,
		Message = "HeartbeatAsync called (Server={Server})"
	)]
	private partial void HeartbeatAsyncCalled(string server);

	[LoggerMessage(
		EventId = LibraryEventIds.InMemoryIsHealthyAsyncCalled,
		EventName = "Immediate.Jobs.Shared.IsHealthyAsyncCalled",
		Level = LogLevel.Debug,
		Message = "IsHealthyAsync called"
	)]
	private partial void IsHealthyAsyncCalled();
}
