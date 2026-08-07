using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	OpenTelemetry-compatible instrumentation emitted by Immediate.Jobs.
/// </summary>
public static class JobTelemetry
{
	/// <summary>
	/// 	The activity source used for enqueue and execution traces.
	/// </summary>
	public static readonly ActivitySource ActivitySource = new("Immediate.Jobs");

	/// <summary>
	/// 	The meter used for scheduler metrics.
	/// </summary>
	public static readonly Meter Meter = new("Immediate.Jobs");

	private static readonly Counter<long> EnqueuedCounter = Meter.CreateCounter<long>("jobs.enqueued");
	private static readonly Counter<long> SucceededCounter = Meter.CreateCounter<long>("jobs.succeeded");
	private static readonly Counter<long> FailedCounter = Meter.CreateCounter<long>("jobs.failed");
	private static readonly Counter<long> RetriedCounter = Meter.CreateCounter<long>("jobs.retried");
	private static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>("job.duration", "s");
	private static long QueueDepth;
	private static long ActiveWorkers;

	static JobTelemetry()
	{
		_ = Meter.CreateObservableGauge("queue.depth", () => Interlocked.Read(ref QueueDepth));
		_ = Meter.CreateObservableGauge("workers.active", () => Interlocked.Read(ref ActiveWorkers));
	}

	internal static void Enqueued(string jobName, string queueName)
	{
		TagList tags = default;
		tags.Add("job.name", jobName);
		tags.Add("job.queue", queueName);
		EnqueuedCounter.Add(1, tags);
		_ = Interlocked.Increment(ref QueueDepth);
	}

	internal static void Acquired() => _ = Interlocked.Decrement(ref QueueDepth);
	internal static void ExecutionStarted() => _ = Interlocked.Increment(ref ActiveWorkers);
	internal static void ExecutionFinished() => _ = Interlocked.Decrement(ref ActiveWorkers);
	internal static void Succeeded(string jobName, string queueName, TimeSpan duration)
	{
		TagList tags = default;
		tags.Add("job.name", jobName);
		tags.Add("job.queue", queueName);
		SucceededCounter.Add(1, tags);
		tags.Add("outcome", "succeeded");
		DurationHistogram.Record(duration.TotalSeconds, tags);
	}

	internal static void Failed(string jobName, string queueName, TimeSpan duration)
	{
		TagList tags = default;
		tags.Add("job.name", jobName);
		tags.Add("job.queue", queueName);
		FailedCounter.Add(1, tags);
		tags.Add("outcome", "failed");
		DurationHistogram.Record(duration.TotalSeconds, tags);
	}

	internal static void Retried(string jobName, string queueName)
	{
		TagList tags = default;
		tags.Add("job.name", jobName);
		tags.Add("job.queue", queueName);
		RetriedCounter.Add(1, tags);
		_ = Interlocked.Increment(ref QueueDepth);
	}
}
