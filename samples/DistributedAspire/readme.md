# Immediate.Jobs distributed Aspire sample

This sample splits Immediate.Jobs across separate processes that share one PostgreSQL database, composed with
.NET Aspire. Every process uses the EF Core provider in distributed mode (`UseDistributed()`), so PostgreSQL is
the coordination authority: jobs enqueued by one process are claimed and run by another, and nothing lives only
in memory.

| Resource | Project | Role |
| --- | --- | --- |
| `jobs-api` | `Api` | Hosts the Immediate.Jobs dashboard at `/jobs`, a health endpoint, and Scalar. Applies the EF Core migrations on startup. Workers are disabled. |
| `worker` | `Worker` | The only process that runs jobs. Polls PostgreSQL every five seconds with fair queues enabled. |
| `jobs-cli` | `Cli` | A one-shot console app that enqueues the demo workflow and exits. Workers are disabled. Starts only when you start it. |
| `postgres` | — | PostgreSQL with a persistent container and data volume, plus pgAdmin and pgweb for inspecting the database. |

All three projects register the same jobs from `DistributedAspire.Shared`, so any of them can schedule work, but
only the worker executes it.

## Run it

Prerequisites are the .NET 10 SDK and a Docker-compatible container runtime.

```console
dotnet run --project samples/DistributedAspire/AppHost/Immediate.Jobs.DistributedAspire.AppHost.csproj
```

The PostgreSQL password is a secret Aspire parameter. Enter it when the Aspire dashboard prompts for it, or set it
once in the AppHost's user secrets:

```console
dotnet user-secrets set Parameters:postgres-password <password> --project samples/DistributedAspire/AppHost
```

The containers bind fixed host ports: PostgreSQL on `5432`, pgAdmin on `5050`, and pgweb on `5051`. Stop any local
service using those ports first. The containers are persistent, so they keep running, and keep their data, after the
AppHost stops.

Open the Aspire dashboard URL printed in the console. Once `jobs-api` and `worker` are healthy, start `jobs-cli`
from its resource actions to enqueue the workflow, then open the `jobs-api` **Jobs** link to watch it in the
Immediate.Jobs dashboard.

## The workflow

`jobs-cli` enqueues a single `prepare-batches` job asking for 50 rows processed 10 at a time. From there the work
fans out across jobs that the worker picks up from PostgreSQL:

```text
prepare-batches     seeds 50 rows into the BatchableRows table (if it is empty)
└─ enqueue-batches  splits the rows into id ranges and commits one atomic batch:
   ├─ process-batch ─┐
   ├─ process-batch ─┤  each rewrites the rows in its range
   ├─ ...            │
   └─ process-batch ─┴─ cleanup-batches  continuation that deletes the rows once every range succeeded
```

- `prepare-batches`, `enqueue-batches`, and `cleanup-batches` allow one invocation at a time and queue overlapping
  runs (`OverlapPolicy.Queue`).
- `process-batch` runs up to 24 invocations concurrently, with up to five attempts each.
- The **Batches** tab of the Immediate.Jobs dashboard shows the batch graph, from the `process-batch` fan-out to the
  `cleanup-batches` fan-in.

Start `jobs-cli` again to run another round.

## Scaling the worker

Because every process coordinates through PostgreSQL, you can run several workers side by side. Add `WithReplicas`
to the worker in `AppHost/AppHost.cs`:

```csharp
builder.AddProject<Projects.Immediate_Jobs_DistributedAspire_Worker>("worker")
	.WithReplicas(3)
	// ...
```

The `process-batch` jobs then spread across the replicas, and the **Servers** page of the Immediate.Jobs dashboard
lists each worker.

## Database

The API applies the EF Core migrations in `DistributedAspire.Shared/Migrations` on startup, and the worker waits for
the API, so the Immediate.Jobs schema and the `BatchableRows` table exist before any job runs. To start from a
clean database, delete the `postgres` container and its data volume.
