# Immediate.Jobs Aspire sample

This sample composes an ASP.NET Core API and PostgreSQL with .NET Aspire. Immediate.Jobs uses its EF Core provider in memory-primary single-server mode: active queued and working state lives in process, while PostgreSQL is the write-through durable copy used for restart recovery.

The AppHost gives PostgreSQL a named data volume and persists its generated password through its user-secrets identity, so database contents and credentials remain aligned across AppHost restarts.

The Aspire dashboard shows resource health, structured logs, traces, and the `Immediate.Jobs`
metrics. The API's `/jobs` dashboard complements it with job history, recurring schedules, batch
progress, workflow graphs, retry, cancellation, and deletion operations. A code-defined
`aspire-heartbeat` job runs at second zero of every minute so the dashboards have recurring activity
to display without manual requests.

## Run it

Prerequisites are the .NET 10 SDK and a Docker-compatible container runtime.

```console
dotnet run --project samples/Aspire/AppHost/Immediate.Jobs.Aspire.AppHost.csproj
```

Open the Aspire dashboard URL printed in the console, then select the `jobs-api` endpoint. It opens
Scalar at `/scalar`. `POST /api/greetings/{name}` demonstrates captured request context.
`POST /api/order-fulfillment-batches` creates an atomic ten-job workflow with chains, parallel
inventory/fraud/payment work, two fan-in joins, and a `Complete` audit continuation. While the
fraud-check member runs, it uses its `JobDetails` to schedule a retry-safe eleventh member before the
fulfillment join. The response contains the batch ID, initial and expected job counts, and dashboard
URL.

`POST /api/game-release-batches/{title}` creates an atomic 19-job global game-release workflow. It
starts with approval, fans out into client and online-service workstreams, splits each workstream
again, and joins each pair into separate certifications. Those certifications converge into one
release candidate, which fans out across store publishing, service deployment, CDN prewarming, and
support preparation. A four-way release gate then fans out into announcement, matchmaking, and
telemetry work before the final global-launch confirmation:

```text
approval
├─ client build ─┬─ compatibility ─┐
│                └─ binary signing ┴─ client certification ─┐
└─ services ─────┬─ data migration ─┐                       │
                 └─ load testing ───┴─ service certification┴─ release candidate
                                                               ├─ store publish ─┐
                                                               ├─ deploy services│
                                                               ├─ prewarm CDN ───┼─ release gate
                                                               └─ brief support ─┘      ├─ announcement ─┐
                                                                                         ├─ matchmaking ──┼─ launch confirmed
                                                                                         └─ telemetry ─────┘
```

The raw OpenAPI document is available at `/openapi/v1.json`. You can also enqueue work from a terminal using the endpoint displayed by Aspire:

```console
curl -X POST http://localhost:<port>/api/greetings/Ada
curl -X POST http://localhost:<port>/api/order-fulfillment-batches
curl -X POST http://localhost:<port>/api/game-release-batches/Starfall
```

Open `http://localhost:<port>/jobs` for the Immediate.Jobs dashboard. The **Batches** tab shows the
order and game-release workflows progressing through their real dependency graphs; select any node
for payload, timing, attempt, and error details. Watch for `order-record-fraud-assessment` appearing
dynamically after the fraud check succeeds. The **Jobs** tab pages on the server in groups of 50 and
links each batch member back to its workflow. The **Recurring** tab lists `aspire-heartbeat`, configured by
`[Job(Cron = "0 * * * * *")]`. In the Aspire dashboard, inspect the `jobs-api` resource's logs to see
the order steps, restored request context, once-per-minute heartbeat, traces, metrics, and health
checks.

The sample calls `UseSingleServer()` explicitly. Removing that line produces the same topology because selecting a durable EF Core provider defaults to single-server mode. Change it to `UseDistributed()` only when running multiple scheduler processes and using PostgreSQL as the coordination authority.

`EnsureCreatedAsync()` keeps the sample self-contained. Use normal EF Core migrations instead in a production application.
