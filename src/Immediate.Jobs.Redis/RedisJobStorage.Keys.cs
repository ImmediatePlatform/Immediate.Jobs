using System.Globalization;
using Immediate.Jobs.Shared.Apis;
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

	private RedisKey DueKey(string queue) => _root + "due:" + queue;

	private RedisKey StateKey(JobState state) => string.Create(CultureInfo.InvariantCulture, $"{_root}state:{(int)state}");

	private RedisKey CompletedKey(JobState state) => string.Create(CultureInfo.InvariantCulture, $"{_root}completed:{(int)state}");

	private RedisKey RecurringKey(string name) => _root + "recurring:" + name;

	private RedisKey ServerKey(string workerId) => _root + "server:" + workerId;

	private static long Score(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

	private static string Ticks(DateTimeOffset value) => value.UtcTicks.ToString("D19", CultureInfo.InvariantCulture);

	private static string DueMember(JobRecord job) => $"{Ticks(job.DueAt)}|{Ticks(job.CreatedAt)}|{job.JobHandle.Value}";

	private static string NullableTicks(DateTimeOffset? value) => value is { } actual ? Ticks(actual) : "";
}
