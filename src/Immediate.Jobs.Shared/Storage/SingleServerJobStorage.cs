using System.Diagnostics.CodeAnalysis;
using Immediate.Jobs.Shared.Apis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Immediate.Jobs.Shared.Storage;

/// <summary>
///		A single-server storage topology that executes against an authoritative in-process store while
///		synchronously replicating changes to durable storage and restoring them when the process starts.
/// </summary>
/// <param name="durableStorage">
/// 	The durable write-through replica.
/// </param>
/// <param name="timeProvider">
/// 	The clock used by the in-process primary store.
/// </param>
/// <param name="logger">
/// 	The logger used to record single-server storage operations.
/// </param>
/// <remarks>
/// 	The wrapper takes ownership of <paramref name="durableStorage"/> and disposes it with the primary store.
/// </remarks>
internal sealed partial class SingleServerJobStorage(
	IJobStorage durableStorage,
	TimeProvider timeProvider,
	ILogger<SingleServerJobStorage>? logger
) :
	IJobStorage,
	IJobGraphStorage,
	IAsyncDisposable
{
	[SuppressMessage("Performance", "CA1823:Avoid unused private fields", Justification = "Used by generated logger methods")]
	[SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Used by generated logger methods")]
	private readonly ILogger _logger = logger ?? NullLogger<SingleServerJobStorage>.Instance;

	private bool _disposed;

	private InMemoryJobStorage PrimaryStorage { get; } = new(timeProvider);

	private IJobStorage DurableStorage { get; } =
		durableStorage switch
		{
			SingleServerJobStorage =>
				throw new ArgumentException("A single-server store cannot be used as its own durable replica.", nameof(durableStorage)),

			InMemoryJobStorage =>
				throw new ArgumentException("An in-memory store cannot be used as a durable replica.", nameof(durableStorage)),

			not IJobGraphStorage =>
				throw new ArgumentException("Single-server durable storage must support batches and continuations.", nameof(durableStorage)),

			_ => durableStorage,
		};

	private IJobGraphStorage JobGraphStorage => (IJobGraphStorage)DurableStorage;

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
	{
		SingleServerHeartbeatAsyncCalled(server.WorkerId, server.ActiveWorkers, server.MaxWorkers);
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		await PrimaryStorage.HeartbeatAsync(server, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		SingleServerIsHealthyAsyncCalled();
		await TaskScheduler.Yield();
		await EnsureInitializedAsync(cancellationToken);
		return await DurableStorage.IsHealthyAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		SingleServerDisposeAsyncCalled();
		await TaskScheduler.Yield();
		if (_disposed)
			return;

		_disposed = true;
		_initializationTask.TrySetException(new ObjectDisposedException(nameof(SingleServerJobStorage)));

		_recurringMaterialization.Dispose();

		await PrimaryStorage.DisposeAsync();
		await DurableStorage.DisposeAsync();
	}

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerHeartbeatAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerHeartbeatAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage HeartbeatAsync called (WorkerId={WorkerId}, ActiveWorkers={ActiveWorkers}, MaxWorkers={MaxWorkers})"
	)]
	private partial void SingleServerHeartbeatAsyncCalled(string workerId, int activeWorkers, int maxWorkers);

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerIsHealthyAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerIsHealthyAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage IsHealthyAsync called"
	)]
	private partial void SingleServerIsHealthyAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.SingleServerDisposeAsyncCalled,
		EventName = "Immediate.Jobs.Shared.SingleServerDisposeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "Single-server storage DisposeAsync called"
	)]
	private partial void SingleServerDisposeAsyncCalled();
}
