# Dashboard

`src/Immediate.Jobs.Dashboard` ships an HTTP API and an embedded Vue client as one package.

## Server

`AddImmediateJobsDashboard` registers services; `MapImmediateJobsDashboard` maps the API, application
shell, and embedded assets beneath a configurable route prefix. Endpoint handlers delegate domain
operations to `JobMonitor`, keeping scheduling and storage rules outside the HTTP layer.

The route group owns access control. A configured authorization policy applies to every route;
the default development restriction rejects access outside the Development environment. New
endpoints must inherit the same policy and validation pipeline.

Keep error mapping consistent: missing targets become HTTP 404 and invalid operations become HTTP
409. The underlying storage contract supplies the distinction. Request validation belongs at the
HTTP boundary through Immediate.Validations.

The API separates live observations (`GET overview`) from stored configuration (`GET definitions`).
Server-sent events provide refreshed observations, including batch progress. Definition pause/resume
and recurring controls operate on persisted identities and configuration.

JSON uses camelCase properties and string enums. Enum names, property names, and endpoint shapes are
wire contracts shared with the client. Update client contracts, state guards, presentation, and tests
together when those contracts change.

## Configuration and live state

Configuration is global: it includes definitions and code-defined or dynamic schedules, regardless
of local handlers and server tags. Live acquisition and recurring status are joined to configuration
by stable identity. A dashboard connected to one server can therefore describe stored work outside
that server's execution scope.

Treat lifecycle state, definition pause, rate limits, and concurrency exhaustion as separate facts.
A paused definition leaves queued jobs in their lifecycle state and allows already acquired jobs
to finish. Counts and filters use stored lifecycle state; control feedback explains why acquisition
is blocked. Definition configuration belongs to the definition even when shown alongside a job or
batch member.

Show configured limits alongside their observed usage. Pause/resume actions follow explicit pause
state, including when another acquisition restriction also applies. Use ordinary links for navigation
and preserve opening details in a new tab. Mutations expose pending and error feedback and refresh
the state they affect.

Jobs cancelled by definition removal retain their cancellation reason in history. Retained dynamic
schedules expose cancellation and suppress trigger/resume actions. The server enforces the same
restriction for stale requests. Restoring a definition alone does not reactivate a cancelled schedule;
explicit removal and recreation establish a new schedule.

## Refresh ownership

One shared query owns the live snapshot across views. Initial HTTP reads, disconnected polling, and
server-sent events update the same cache. Deduplicate concurrent observers and prevent an older HTTP
response from overwriting a newer streamed observation. Route navigation should reuse that state;
reconnect and fallback polling must preserve the same ownership.

Cache definition metadata independently because configuration and live status change at different
rates. Streamed status must not overwrite configuration or force a metadata read on every event.
Bound metadata staleness through periodic refresh, join by identity, and require live status for
controls that depend on acquisition state. Manual refresh updates both configuration and status;
control mutations invalidate the observations they change.

Snapshots are logical observations across storage records. Bulk-evaluate them so query count does not
grow with the number of definitions. Database round trips, work over acquisition history, and cache
freshness are separate performance concerns; keep their ownership explicit.

## Client and build integration

`DashboardClient` uses Vue, vue-router, vue-query, Vite, and Vitest. Keep server JSON contracts and
client types aligned. Run `npm run check` for linting, type checks, and component tests; use the build
and bundle-size checks when changing shipped assets.

The .NET package embeds the built client. Build integration runs the client once for a cross-targeted
build, skips unchanged inputs, and avoids running it for design-time builds. Keep the npm lockfile
consistent with package declarations so local and CI builds use the same dependencies.
