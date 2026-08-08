using System.Reflection;
using System.Runtime.ExceptionServices;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.FunctionalTests;

#pragma warning disable CS1591
public interface IControllableJobStorage : IRecurringJobStorage, IJobGraphStorage, IJobStorageReplica;

public class ControllableJobStorageProxy : DispatchProxy
{
	public object Inner { get; set; } = null!;
	public bool BlockBatchEnqueue { get; set; }
	public bool FailTelemetry { get; set; }
	public bool CaptureFailures { get; set; }
	public CancellationTokenSource? CancelTelemetry { get; set; }
	public string? CapturedFailedJobId { get; private set; }
	public string? CapturedFailure { get; private set; }
	public TaskCompletionSource BatchEnqueueEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource BatchEnqueueRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	public IReadOnlyList<JobRecord>? CapturedBatchJobs { get; private set; }
	public IReadOnlyList<JobContinuationEdge>? CapturedBatchEdges { get; private set; }

	public static IControllableJobStorage Create(IJobStorage inner)
	{
		var proxy = DispatchProxy.Create<IControllableJobStorage, ControllableJobStorageProxy>();
		((ControllableJobStorageProxy)(object)proxy).Inner = inner;
		return proxy;
	}

	protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
	{
		ArgumentNullException.ThrowIfNull(targetMethod);
		ArgumentNullException.ThrowIfNull(args);
		if (string.Equals(targetMethod.Name, nameof(IJobGraphStorage.EnqueueBatchAsync), StringComparison.Ordinal) &&
			BlockBatchEnqueue)
		{
			CapturedBatchJobs = (IReadOnlyList<JobRecord>)args[1]!;
			CapturedBatchEdges = (IReadOnlyList<JobContinuationEdge>)args[2]!;
			_ = BatchEnqueueEntered.TrySetResult();
			return new ValueTask(BatchEnqueueRelease.Task);
		}

		if (string.Equals(targetMethod.Name, nameof(IJobStorage.SetExecutionTelemetryAsync), StringComparison.Ordinal) &&
			FailTelemetry)
		{
#pragma warning disable CA2012 // Reflection proxies must box the ValueTask returned by the intercepted interface method.
			return ValueTask.FromException(new InvalidOperationException("Expected telemetry persistence failure."));
#pragma warning restore CA2012
		}

		if (string.Equals(targetMethod.Name, nameof(IJobStorage.SetExecutionTelemetryAsync), StringComparison.Ordinal) &&
			CancelTelemetry is { } cancellation)
		{
			cancellation.Cancel();
#pragma warning disable CA2012 // Reflection proxies must box the ValueTask returned by the intercepted interface method.
			return ValueTask.FromCanceled((CancellationToken)args[^1]!);
#pragma warning restore CA2012
		}

		if (string.Equals(targetMethod.Name, nameof(IJobStorage.FailAsync), StringComparison.Ordinal) && CaptureFailures)
		{
			CapturedFailedJobId = (string)args[0]!;
			CapturedFailure = (string)args[3]!;
			return ValueTask.CompletedTask;
		}

		try
		{
			return targetMethod.Invoke(Inner, args);
		}
		catch (TargetInvocationException exception) when (exception.InnerException is { } innerException)
		{
			ExceptionDispatchInfo.Capture(innerException).Throw();
			throw;
		}
	}
}
#pragma warning restore CS1591
