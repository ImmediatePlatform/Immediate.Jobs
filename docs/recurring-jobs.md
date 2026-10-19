# Recurring jobs

## Schedules

A `RecurringJobSchedule` has a unique `Name`, the `JobName` it materializes, a `Cron`, an IANA
`TimeZone`, `NextRunAt`, a paused flag, and `IsCodeDefined`. Queue routing comes from the current
job definition identified by `JobName`; schedules do not carry a queue name.

Schedule names are case-insensitive identities. Upsert, pause, resume, removal, manual triggering,
materialization, and startup reconciliation accept case aliases and retain the first stored spelling.
Keep record identity, indexes, and query matching consistent with the provider's
[name and collation rules](storage.md#names-and-collation).

- **Code-defined.** `[Job(Cron = "...", TimeZone = "...")]` on a payloadless job (`IJOB0006` rejects
  parameters). The schedule name is the job name. Its scheduler implements `IRecurringJobTrigger`
  (`TriggerNowAsync`). An omitted `TimeZone` inherits the assembly default, or uses UTC if none is
  supplied. Explicit `TimeZone = null` uses UTC even when the assembly supplies a default.
- **Dynamic.** Payloadless jobs without a `Cron` get `IRecurringJobScheduler`:
  `AddOrUpdateRecurringAsync(name, cron, timeZone)` and `RemoveRecurringAsync(name)`.
- **Expressions.** `Cron` holds either a cron expression or an RFC 5545 recurrence rule, parsed by
  `Meziantou.Framework.Scheduling` as a cron expression or recurrence rule. Time zones are resolved with
  `TimeZoneInfo.FindSystemTimeZoneById`. The analyzers validate code-defined expressions at compile
  time (`IJOB0007`).

At startup `JobSchedulingService.InitializeAsync` submits all definitions and their code-defined schedules
through `MergeJobDefinitionsListAsync`. Storage atomically reconciles the server's tag scope, removing
scoped code-defined schedules when a definition disappears or loses its cron expression. It preserves
other scopes and dynamic schedules. Unchanged cron/time zone retains the next occurrence; updates
preserve the pause state and last occurrence.

`UpsertRecurringAsync` creates dynamic schedules and updates existing schedules without changing
`IsCodeDefined`. Creating a code-defined schedule or changing ownership through upsert throws
`ImmediateJobException` without modifying the schedule. Definition reconciliation owns creation
and adoption of code-defined schedules.
Upserts of existing code-defined schedules update only `NextRunAt`; configuration, pause state,
cancellation, and materialized progress remain under their respective storage operations.

## Materialization

Each polling iteration reads due schedules through `GetDueRecurringAsync` with eligible job names.
Process schedules independently: one failing schedule is logged and skipped without blocking
others or acquisition. Providers filter eligible job names before applying the batch limit, so
unrelated schedules cannot starve this server's due work. Every due scan requires the eligible job-name
list; an empty list returns no schedules.

Indexes should support this scoped read without scanning unrelated schedules. Changes to a schedule
and its due-index membership commit atomically, including pause/resume, removal, reconciliation, and
materialization.

1. **Misfires.** If several occurrences were missed (for example while the app was down),
   `MisfireHandlingMode` decides what happens, and the count is logged:
   - `EnqueueOne` (default): one occurrence due now, then `NextRunAt` jumps past the missed ones;
   - `EnqueueAll`: one occurrence per missed time;
   - `EnqueueNone`: advance `NextRunAt` past the missed occurrences without enqueuing (an occurrence
     due exactly now still runs).
2. **Overlap.** `OverlapPolicy` decides what happens while an earlier occurrence hasn't finished:
   - `Skip` (default): the new occurrence is recorded as `Skipped`.
   - `Queue`: the new occurrence becomes a continuation of the latest unfinished one. This needs
     graph storage and throws otherwise.
   - `Concurrent`: just enqueue.
3. **Insert.** `MaterializeRecurringAsync(schedule, job, nextRunAt, dependencies)` atomically inserts
   the occurrence and advances `NextRunAt`. Deduplicate by schedule identity and occurrence time so
   concurrent nodes cannot create the same occurrence twice.
   Storage returns `false` for a stale or duplicate materialization.

## Operations

`JobMonitor` (and the dashboard) can pause, resume, and trigger a schedule immediately. Paused
schedules are excluded from the due scan.

The `Immediate.Jobs.NodaTime` package adds NodaTime overloads for scheduling, and serialization for
NodaTime payload types; `IJOB0004` reports a project that references NodaTime without it.
