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

The app listens on `http://localhost:50790` and opens the Immediate.Jobs dashboard at `/jobs`. The dashboard is only
available in the `Development` environment, which the launch profile sets.

- `POST /greetings/{name}` enqueues a job that logs a greeting.
- `POST /greetings/{name}/delayed` schedules the same job to run in one minute. Stop the app before it runs and start
  it again to see the job recovered from SQLite.
- `POST /retry-demo` enqueues a job that fails its first two attempts, retries ten seconds later each time, and
  succeeds on attempt three.
- A recurring `heartbeat` job runs at second zero of every minute.
- `GET /health` reports the Immediate.Jobs health check.

```console
curl -X POST http://localhost:50790/greetings/Ada
```

## Database

The app creates `immediate-jobs.db` in the project directory on startup with `EnsureCreatedAsync`. `EnsureCreatedAsync`
does not update an existing database, so delete `immediate-jobs.db` (and its `-shm` and `-wal` files) after pulling a
change to the Immediate.Jobs schema. The files are ignored by git.
