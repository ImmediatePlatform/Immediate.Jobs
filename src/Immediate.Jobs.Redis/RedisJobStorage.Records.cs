using System.Globalization;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	private static JobRecord ToJobRecord(RedisValue[] values)
	{
		var job = JsonSerializer.Deserialize(
			(byte[])values[0]!, RedisJsonSerializerContext.Default.JobRecord
		) ?? throw new ImmediateJobException("Job record contains invalid data.");
		return job with
		{
			State = (JobState)ParseInt32(values[1]),
			CancellationReason = values[11].IsNull ? job.CancellationReason : NullIfEmpty(values[11]),
			DueAt = FromTimestamp(values[2]),
			Attempt = ParseInt32(values[3]),
			WorkerId = NullIfEmpty(values[4]),
			LeaseExpiresAt = FromNullableTimestamp(values[5]),
			LastError = NullIfEmpty(values[6]),
			CompletedAt = FromNullableTimestamp(values[7]),
			ExecutionTraceId = NullIfEmpty(values[8]),
			ExecutionSpanId = NullIfEmpty(values[9]),
			ExecutionStartedAt = FromNullableTimestamp(values[10]),
			QueueName = NullIfEmpty(values[12]),
		};
	}

	private async ValueTask<JobRecord?> ReadJobAsync(JobHandle id, CancellationToken cancellationToken)
	{
		var values = await ReadJobValuesAsync(id, cancellationToken);
		return values is null ? null : ToJobRecord(values);
	}

	private async ValueTask<RedisValue[]?> ReadJobValuesAsync(JobHandle id, CancellationToken cancellationToken)
	{
		var result = (RedisResult[])(await Database.ScriptEvaluateAsync(
			RedisScripts.ReadJobs, [DefinitionMetadataKey, JobKey(id)], [_root]
		).WaitAsync(cancellationToken))!;
		return result.Length == 0 ? null : ReadValues(result[0]);
	}

	private static RedisValue[] ReadValues(RedisResult result) =>
		[.. ((RedisResult[])result!).Select(static value => (RedisValue)(byte[]?)value)];

	private static IReadOnlyList<JobRecord> ReadJobRecords(RedisResult result) =>
		[.. ((RedisResult[])result!).Select(static values => ToJobRecord(ReadValues(values)))];

	private static DateTimeOffset FromTimestamp(RedisValue value) =>
		DateTimeOffset.Parse((string)value!, CultureInfo.InvariantCulture);

	private static DateTimeOffset? FromNullableTimestamp(RedisValue value) =>
		value.IsNullOrEmpty ? null : FromTimestamp(value);

	private static int ParseInt32(RedisValue value) =>
		int.Parse((string)value!, NumberStyles.Integer, CultureInfo.InvariantCulture);

	private static string? NullIfEmpty(RedisValue value) => value.IsNullOrEmpty ? null : (string)value!;
}

internal sealed record RedisJobHeader
{
	public required JobHandle JobHandle { get; init; }
	public required string JobName { get; init; }
	public required DateTimeOffset CreatedAt { get; init; }
	public string? CancellationReason { get; init; }
}
