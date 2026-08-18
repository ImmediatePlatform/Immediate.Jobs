# Immediate.Jobs.Redis

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.Redis.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.Redis/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

Distributed Redis queue and recurring storage for
[Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/), built on StackExchange.Redis.

## Installation

```console
dotnet add package Immediate.Jobs
dotnet add package Immediate.Jobs.Redis
```

## Configuration

Pass either a StackExchange.Redis configuration string or an application-owned `IConnectionMultiplexer`. `UseRedis`
selects distributed mode automatically:

```csharp
builder.Services.AddMyAppJobs(options =>
	options.UseRedis("localhost:6379", redis =>
	{
		redis.Database = 1;
		redis.KeyPrefix = "billing-jobs";
	}));
```

`AddMyAppJobs` is the generated registration method for an assembly named `MyApp`. Also register the matching generated
Immediate.Handlers method, `AddMyAppHandlers()`.

The prefix isolates applications and is also used as the Redis Cluster hash tag, keeping every atomic Lua transition in
one slot. The provider owns connections it creates from a configuration string; it does not dispose an
`IConnectionMultiplexer` supplied by the application.

Terminal job history is tracked in completion-time sorted indexes and removed by the normal `SucceededRetention` and
`FailedRetention` purge loop.

## Capabilities and limitations

Redis provides distributed queue acquisition, leases, recurring schedules, cancellation, retries, execution history,
and retention. It always selects distributed mode because the memory-primary durable single-server topology requires
all storage capabilities.

Redis persists fair-queue group IDs but rejects fair acquisition in the current release. It also does not implement
batches or continuations; dependency graphs require the Entity Framework Core or LinqToDB provider.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Documentation](https://immediateplatform.dev/docs/Immediate.Jobs/introduction)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
