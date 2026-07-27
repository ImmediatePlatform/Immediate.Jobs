# Immediate.Jobs Aspire sample

This sample composes an ASP.NET Core API and PostgreSQL with .NET Aspire. Immediate.Jobs uses its EF Core provider in memory-primary single-server mode: active queued and working state lives in process, while PostgreSQL is the write-through durable copy used for restart recovery.

The AppHost gives PostgreSQL a named data volume and persists its generated password through its user-secrets identity, so database contents and credentials remain aligned across AppHost restarts.

The Aspire dashboard shows resource health, structured logs, traces, and the `Immediate.Jobs` metrics. The API's `/jobs` dashboard complements it with job history, recurring schedules, retry, and deletion operations. A code-defined `aspire-heartbeat` job runs at second zero of every minute so the dashboards have recurring activity to display without manual requests.

## Run it

Prerequisites are the .NET 10 SDK and a Docker-compatible container runtime.

```console
dotnet run --project samples/Aspire/AppHost/Immediate.Jobs.Aspire.AppHost.csproj
```

Open the Aspire dashboard URL printed in the console, then select the `jobs-api` endpoint. It opens Scalar at `/scalar`, where you can expand `POST /api/greetings/{name}`, enter a name, and select **Send API Request**. The generated scheduler captures the request IP address and User-Agent through `RequestContextExtractor`; the worker restores them into its execution scope before the greeting handler runs.

The raw OpenAPI document is available at `/openapi/v1.json`. You can also enqueue work from a terminal using the endpoint displayed by Aspire:

```console
curl -X POST http://localhost:<port>/api/greetings/Ada
```

Open `http://localhost:<port>/jobs` for the Immediate.Jobs dashboard, find the greeting job, and expand **Details** to inspect its persisted `http-request` context envelope. The **Recurring** tab lists `aspire-heartbeat`, configured by `[Job(Cron = "0 * * * * *")]`. In the Aspire dashboard, inspect the `jobs-api` resource's logs to see both the restored IP address and User-Agent and the once-per-minute heartbeat, along with traces, metrics, and health checks. The enqueue HTTP trace is linked to the background job execution activity.

The sample calls `UseSingleServer()` explicitly. Removing that line produces the same topology because selecting a durable EF Core provider defaults to single-server mode. Change it to `UseDistributed()` only when running multiple scheduler processes and using PostgreSQL as the coordination authority.

`EnsureCreatedAsync()` keeps the sample self-contained. Use normal EF Core migrations instead in a production application.
