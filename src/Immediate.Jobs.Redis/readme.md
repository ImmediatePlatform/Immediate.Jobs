# Immediate.Jobs.Redis

[![NuGet](https://img.shields.io/nuget/v/Immediate.Jobs.Redis.svg?style=plastic)](https://www.nuget.org/packages/Immediate.Jobs.Redis/)
[![Documentation](https://img.shields.io/badge/docs-online-brightgreen)](https://immediateplatform.dev/docs/Immediate.Jobs/configuring-storage-providers#redis)
[![License](https://img.shields.io/github/license/ImmediatePlatform/Immediate.Jobs.svg)](https://github.com/ImmediatePlatform/Immediate.Jobs/blob/main/license.txt)

Distributed Redis queue and recurring storage for
[Immediate.Jobs](https://www.nuget.org/packages/Immediate.Jobs/), built on StackExchange.Redis.

## Installation

```console
dotnet add package Immediate.Jobs --prerelease
dotnet add package Immediate.Jobs.Redis --prerelease
```

## Configuration

Register an application-owned `IConnectionMultiplexer`, then select Redis storage and configure its key placement:

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
	ConnectionMultiplexer.Connect("localhost:6379"));

builder.Services.AddMyAppHandlers();
builder.Services.AddMyAppJobs()
	.ConfigureStorage(storage => storage
		.UseRedis()
		.ConfigureRedis(redis =>
		{
			redis.Database = 0;
			redis.KeyPrefix = "billing-jobs";
		}));
```

`AddMyAppJobs` is the generated registration method for an assembly named `MyApp`.

The provider resolves `IConnectionMultiplexer` from dependency injection. The application controls how that connection
is created and shared. Registering it with the factory overload of `AddSingleton` lets the service provider dispose it
at shutdown.

The prefix isolates applications and keeps all of the application's keys in one Redis Cluster slot, which is required
for operations that update several keys together. `Database` defaults to `-1` (the server default), and `KeyPrefix`
defaults to `immediate-jobs`. Prefixes cannot be empty or contain braces; invalid options fail validation when the host
starts.

Redis coordinates job claims between every application replica automatically. No `UseSingleServer()` or
`UseDistributed()` call is needed. If a process stops, work that it claimed becomes available to another replica after
the lease expires.

## Capabilities and limitations

Redis supports ordinary and recurring jobs, cancellation, retries, execution history, and retention.

Redis does not support fair queues, batches, or continuations; fair scheduling and dependency graphs require the
Entity Framework Core or LinqToDB provider.

## More information

- [Immediate.Jobs core package](https://www.nuget.org/packages/Immediate.Jobs/)
- [Storage-provider configuration](https://immediateplatform.dev/docs/Immediate.Jobs/configuring-storage-providers#redis)
- [GitHub repository](https://github.com/ImmediatePlatform/Immediate.Jobs)
