# Immediate.Jobs SQLite sample

This sample runs entirely on your machine: one ASP.NET Core app, with Immediate.Jobs storing its state in a local SQLite
file through the EF Core provider. It needs no containers, no Aspire, and no external database. The app runs in
memory-primary single-server mode: active queued and working state lives in process, and SQLite is the write-through
durable copy used for restart recovery.

## Run it

Prerequisites are the .NET 10 SDK.

```console
dotnet run --project samples/Sqlite/SqliteSample.csproj -f net10.0
```

The app listens on `http://localhost:50790` and opens a [Scalar](https://scalar.com) API reference at `/scalar`, where
you can trigger every demo below. The Immediate.Jobs dashboard is at `/jobs`. Scalar and the dashboard are only
available in the `Development` environment, which the launch profile sets.

- `POST /greetings/{name}` enqueues a job that logs a greeting.
- `POST /greetings/{name}/delayed` schedules the same job to run in one minute. Stop the app before it runs and start
  it again to see the job recovered from SQLite.
- `POST /retry-demo` enqueues a job that fails its first two attempts, retries ten seconds later each time, and
  succeeds on attempt three.
- `POST /fair-queue-demo` enqueues 100 slow jobs in one group and one job in a second group that becomes due five
  seconds later. With fair queues enabled, the quiet job runs as soon as it is due instead of waiting behind the
  backlog.
- `POST /continuation-branch-demo/{failRoot}` creates a root job with a success and a failure continuation. Pass
  `false` to run the success branch or `true` to run the failure branch; the other branch becomes `Skipped`.
- `POST /order-fulfillment-batches` creates an atomic ten-job order workflow with parallel inventory, fraud, and
  payment steps, two fan-in joins, and a `Complete` audit continuation. The fraud check adds an eleventh
  `order-record-fraud-assessment` job while it runs.
- `POST /game-release-batches/{title}` creates an atomic 19-job release workflow with repeated fan-out and fan-in
  stages.
- A recurring `heartbeat` job runs at second zero of every minute.
- `GET /health` reports the Immediate.Jobs health check.

```console
curl -X POST http://localhost:50790/greetings/Ada
curl -X POST http://localhost:50790/order-fulfillment-batches
curl -X POST http://localhost:50790/game-release-batches/Starfall
```

The workflow endpoints return the batch handle, and the response's `Location` header points at the batch in the
dashboard, where you can watch the dependency graph progress.

## Database

The app creates `immediate-jobs.db` in the project directory on startup with `EnsureCreatedAsync`. `EnsureCreatedAsync`
does not update an existing database, so delete `immediate-jobs.db` (and its `-shm` and `-wal` files) after pulling a
change to the Immediate.Jobs schema. The files are ignored by git.
