using System.Globalization;
using StackExchange.Redis;

namespace Immediate.Jobs.Redis;

internal sealed partial class RedisJobStorage
{
	private RedisKey AllJobsKey => _root + "jobs";

	private RedisKey LeasesKey => _root + "leases";

	private RedisKey RecurringNamesKey => _root + "recurring:names";

	private RedisKey RecurringDueKey => _root + "recurring:due";

	private RedisKey RecurringDedupeKey => _root + "recurring:dedupe";

	private RedisKey DefinitionMetadataKey => _root + "definition-metadata";

	private RedisKey DefinitionCatalogVersionKey => _root + "definition-catalog-version";

	private RedisKey ServersKey => _root + "servers";

	private RedisKey JobKey(JobHandle id) => _root + "job:" + id.Value;

	private RedisKey ExecutionIndexKey(JobHandle id) => _root + "executions:index:" + id.Value;

	private RedisKey ExecutionDataKey(JobHandle id) => _root + "executions:data:" + id.Value;

	private RedisKey DueKey(string jobName) => _root + "due:" + jobName.ToUpperInvariant();

	private RedisKey StateKey(JobState state) => string.Create(CultureInfo.InvariantCulture, $"{_root}state:{(int)state}");

	private RedisKey CompletedKey(JobState state) => string.Create(CultureInfo.InvariantCulture, $"{_root}completed:{(int)state}");

	private RedisKey RecurringKey(string name) => _root + "recurring:" + name;

	private RedisKey ServerKey(string workerId) => _root + "server:" + workerId;

	private static long Score(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

	private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

	private static string DueMember(DateTimeOffset dueAt, DateTimeOffset createdAt, JobHandle handle) => $"{Timestamp(dueAt)}|{Timestamp(createdAt)}|{handle.Value}";

	private static string NullableTimestamp(DateTimeOffset? value) => value is { } actual ? Timestamp(actual) : "";
}
