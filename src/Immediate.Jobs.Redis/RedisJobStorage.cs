using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

/// <summary>
/// Distributed Redis storage for ordinary queue jobs and recurring schedules.
/// Batches and continuations require a graph-capable SQL provider.
/// </summary>
internal sealed partial class RedisJobStorage(
	IConnectionMultiplexer connection,
	IOptions<RedisJobStorageOptions> options,
	TimeProvider timeProvider,
	ILogger<RedisJobStorage>? logger = null
) : IJobStorage
{
	[SuppressMessage("Performance", "CA1823:Avoid unused private fields", Justification = "Used by generated logger methods")]
	[SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Used by generated logger methods")]
	private readonly ILogger _logger = logger ?? NullLogger<RedisJobStorage>.Instance;

	[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Owned by DI")]
	private readonly IConnectionMultiplexer _connection = connection;

	private readonly RedisJobStorageOptions _storageOptions = options.Value;

	private readonly TimeProvider _timeProvider = timeProvider;

	private readonly string _root = $"{{{options.Value.KeyPrefix}}}:";

	private IDatabase Database => _connection.GetDatabase(_storageOptions.Database);

	/// <inheritdoc />
	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
	{
		InitializeAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await Database.PingAsync().WaitAsync(cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask HeartbeatAsync(
		JobServerSnapshot server,
		CancellationToken cancellationToken = default
	)
	{
		HeartbeatAsyncCalled(server.WorkerId);
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		_ = await EvaluateInt64Async(
			RedisScripts.Heartbeat,
			[ServerKey(server.WorkerId), ServersKey],
			[
				Ticks(server.LastHeartbeat),
				server.ActiveWorkers,
				server.MaxWorkers,
				Score(server.LastHeartbeat + server.ServerTimeout),
				server.WorkerId,
				(long)server.ServerTimeout.TotalMilliseconds,
				Ticks(server.LastHeartbeat + server.ServerTimeout),
				JsonSerializer.Serialize(server, RedisJsonSerializerContext.Default.JobServerSnapshot),
			],
			cancellationToken
		);
	}

	/// <inheritdoc />
	public async ValueTask<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
	{
		IsHealthyAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();

		try
		{
			await Database.PingAsync().WaitAsync(cancellationToken);
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (RedisException)
		{
			return false;
		}
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		DisposeAsyncCalled();
		await TaskScheduler.Yield();
	}

	[LoggerMessage(
		EventId = LibraryEventIds.InitializeAsyncCalled,
		EventName = "Immediate.Jobs.Redis.InitializeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "InitializeAsync called"
	)]
	private partial void InitializeAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.HeartbeatAsyncCalled,
		EventName = "Immediate.Jobs.Redis.HeartbeatAsyncCalled",
		Level = LogLevel.Debug,
		Message = "HeartbeatAsync called (Server={Server})"
	)]
	private partial void HeartbeatAsyncCalled(string server);

	[LoggerMessage(
		EventId = LibraryEventIds.IsHealthyAsyncCalled,
		EventName = "Immediate.Jobs.Redis.IsHealthyAsyncCalled",
		Level = LogLevel.Debug,
		Message = "IsHealthyAsync called"
	)]
	private partial void IsHealthyAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.DisposeAsyncCalled,
		EventName = "Immediate.Jobs.Redis.DisposeAsyncCalled",
		Level = LogLevel.Debug,
		Message = "DisposeAsync called"
	)]
	private partial void DisposeAsyncCalled();

	private async ValueTask<long> EvaluateInt64Async(
		string script,
		RedisKey[] keys,
		RedisValue[] values,
		CancellationToken cancellationToken
	)
	{
		var result = await Database.ScriptEvaluateAsync(script, keys, values)
			.WaitAsync(cancellationToken);
		return (long)result;
	}
}
