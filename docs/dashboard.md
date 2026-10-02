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
- **Mutations.** `DashboardApiEndpointOperations` wraps them: `KeyNotFoundException` becomes 404 and
  `ImmediateJobException` becomes 409, so storage must keep that exception convention.
- **Live updates.** `GET events` and `GET batches/{batchHandle}/stream` are server-sent event streams
  polling at `UpdateInterval` (default 2 seconds).
- **JSON.** `DashboardJsonSerializerContext` uses camelCase properties and **string enums**, so
  renaming a `JobState` or `BatchState` member changes the wire contract.

## Client

`DashboardClient/` is Vue 3, vue-router, `@tanstack/vue-query`, Vite, and Vitest.

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
