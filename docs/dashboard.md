# Dashboard

`src/Immediate.Jobs.Dashboard` is an HTTP API plus a Vue client, shipped as one package.

## Server

- **Registration.** `AddImmediateJobsDashboard()` (on `IImmediateJobsBuilder`) registers the
  services; `MapImmediateJobsDashboard("/jobs")` maps a route group serving the API, the SPA shell,
  and the embedded `app.js`/`app.css`.
- **Access.** If `ImmediateJobsDashboardOptions.AuthorizationPolicy` is set, the group requires it.
  Otherwise, with `RestrictToDevelopmentEnvironment` (default `true`), `DevelopmentDashboardFilter`
  returns 403 outside the Development environment.
- **Endpoints.** Each endpoint is an Immediate.Handlers handler exposed with Immediate.Apis attributes
  in `Endpoints/` (`[MapGet("jobs")]`, `[MapPost("jobs/{jobHandle}/retry")]`, …), backed by
  `JobMonitor`. Request validation runs through `DashboardValidationFilter` with Immediate.Validations.
- **Monitoring.** `GET overview` returns counts, servers, capabilities, bulk job acquisition
  status, and recurring pause/progress. `GET definitions` returns `{ jobs, recurring }` configuration
  without live state. There are no separate recurring or server projection endpoints.
- **Definition controls.** `POST definitions/{name}/pause` and `POST definitions/{name}/resume`
  use persisted limits and canonical names through `JobMonitor`. Unknown definitions return 404.
  These routes inherit the dashboard's access policy.
- **Mutations.** `DashboardApiEndpointOperations` wraps them: `KeyNotFoundException` becomes 404 and
  `ImmediateJobException` becomes 409, so storage must keep that exception convention.
- **Live updates.** `GET events` and `GET batches/{batchHandle}/stream` are server-sent event streams
  polling at `UpdateInterval` (default 2 seconds).
- **JSON.** `DashboardJsonSerializerContext` uses camelCase properties and **string enums**, so
  renaming a `JobState` or `BatchState` member changes the wire contract.

## Definitions and server tags

`GET definitions` returns every persisted job and both code-defined and dynamic recurring
schedule configuration, irrespective of local handlers or server tags. The snapshot contains only
job acquisition state and recurring pause/progress, joined by stable identity in the client.
The Definitions page displays tags, queue, attempts, timeout, concurrency and windows alongside
live status and pause/resume actions. The Servers page displays effective server tags.

## Client

`DashboardClient/` is Vue 3, vue-router, `@tanstack/vue-query`, Vite, and Vitest.

- **Definitions view.** `/definitions` shows one row per stored definition with queue, acquisition
  status, active count and concurrency limit, sliding/fixed windows, and next eligible time. A paused
  definition still displays its active jobs and an independent at-capacity indicator. Rows link to
  filtered jobs, and server-sent snapshots keep the table current.
  Each row exposes Pause or Resume based on its explicit pause state, including rate-limited or
  concurrency-limited definitions. Actions show pending/error feedback and refresh dashboard queries.
- **Contracts.** `src/contracts.ts` mirrors the server JSON. When an enum gains or renames a member,
  update `jobStates`/`batchStates` there, `isBatchState` in `src/api.ts`, any component that switches
  on states (`JobStateSummary.vue`, `WorkflowGraph.vue`, the batch cancel buttons), and the
  `data-state` selectors in `src/styles.css`, which use the lower-cased state name.
- **Checks.** `npm run check` runs ESLint, `vue-tsc`, and Vitest (`tests/`).
- **Build.** `npm run build` type-checks and bundles; `npm run size` fails if the gzipped bundle
  exceeds 200 KiB.

## Build integration

`Immediate.Jobs.Dashboard.csproj` runs `npm ci` and `npm run build` once per build, in the outer
(cross-targeting) build only, with inputs and outputs so unchanged sources are skipped. It then
embeds the output as resources (`DashboardAssets` serves them). Design-time builds skip the client
entirely. If the dashboard build fails in CI but not locally, check that `package-lock.json` is
committed and matches `package.json`.

## Refresh ownership

The application owns one snapshot query, shared across Overview, Definitions, Recurring, and
Servers. HTTP initial loading and disconnected polling call `GET overview`; concurrent observers
share one request. SSE updates that same cache and cancel an older in-flight snapshot read.
While live, the snapshot stays fresh across route navigation; on disconnect, active observers poll
every five seconds. Definition controls invalidate only live status.

Definition metadata is loaded when a Definitions or Recurring view needs it, cached for five
minutes, and polled every five minutes while the view is active. It survives route traversal and
is never overwritten or refreshed by each SSE event. Metadata changes with the same identity can
therefore take up to five minutes to appear. New metadata is obtained after that refresh; removed
identities are hidden once absent from live status. The Definitions page cannot offer controls
without acquisition status. Manual refresh invalidates both metadata and live state.

Snapshots are logical observations, not an atomic cross-table transaction. EF Core and LinqToDB
bulk-read catalog metadata, control rows, grouped active leases and the largest relevant sliding
window, evaluating all definitions in memory. Query count does not grow per definition. Redis
uses one catalog read and one Lua evaluation for the complete status collection; its execution
cost still grows with definitions, history, and leases. In-memory groups active leases once.
Single-server monitoring reads the committed durable authority and adds its process-local
heartbeats without collecting another full snapshot.
