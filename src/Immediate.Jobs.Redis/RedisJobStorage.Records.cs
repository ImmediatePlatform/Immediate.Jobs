using System.Globalization;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	private static readonly RedisValue[] JobMutableFields =
	[
		"record",
		"state",
		"due",
		"attempt",
		"worker",
		"lease",
		"error",
		"completed",
		"executionTraceId",
		"executionSpanId",
		"executionStartedAt",
	];

	private async ValueTask<JobRecord?> ReadJobAsync(JobHandle id, CancellationToken cancellationToken)
	{
		var values = await Database.HashGetAsync(JobKey(id), JobMutableFields)
			.WaitAsync(cancellationToken);
		if (values[0].IsNull)
			return null;
		var job = JsonSerializer.Deserialize(
			(string)values[0]!,
			RedisJsonSerializerContext.Default.JobRecord
		) ?? throw new ImmediateJobException($"Job '{id}' contains invalid data.");
		return job with
		{
			State = (JobState)ParseInt32(values[1]),
			DueAt = FromTicks(values[2]),
			Attempt = ParseInt32(values[3]),
			WorkerId = NullIfEmpty(values[4]),
			LeaseExpiresAt = FromNullableTicks(values[5]),
			LastError = NullIfEmpty(values[6]),
			CompletedAt = FromNullableTicks(values[7]),
			ExecutionTraceId = NullIfEmpty(values[8]),
			ExecutionSpanId = NullIfEmpty(values[9]),
			ExecutionStartedAt = FromNullableTicks(values[10]),
		};
	}

	private static DateTimeOffset FromTicks(RedisValue value) =>
		new(long.Parse((string)value!, NumberStyles.None, CultureInfo.InvariantCulture), TimeSpan.Zero);

	private static DateTimeOffset? FromNullableTicks(RedisValue value) =>
		value.IsNullOrEmpty ? null : FromTicks(value);

	private static int ParseInt32(RedisValue value) =>
		int.Parse((string)value!, NumberStyles.Integer, CultureInfo.InvariantCulture);

	private static string? NullIfEmpty(RedisValue value) => value.IsNullOrEmpty ? null : (string)value!;
}
