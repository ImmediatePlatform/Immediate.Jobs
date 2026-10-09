# Recurring jobs

## Schedules

A `RecurringJobSchedule` has a unique `Name`, the `JobName` it materializes, a `Cron`, an IANA
`TimeZone`, `NextRunAt`, a paused flag, and `IsCodeDefined`.

- **Code-defined.** `[Job(Cron = "...", TimeZone = "...")]` on a payloadless job (`IJOB0006` rejects
  parameters). The schedule name is the job name. Its scheduler implements `IRecurringJobTrigger`
  (`TriggerNowAsync`).
- **Dynamic.** Payloadless jobs without a `Cron` get `IRecurringJobScheduler`:
  `AddOrUpdateRecurringAsync(name, cron, timeZone)` and `RemoveRecurringAsync(name)`, implemented by
  `JobScheduler<TPayload>.AddOrUpdateRecurringCoreAsync` / `RemoveRecurringCoreAsync`.
- **Expressions.** `Cron` holds either a cron expression or an RFC 5545 recurrence rule, parsed by
  `RecurringJobScheduleExtensions.ParseCronExpression` using `Meziantou.Framework.Scheduling`
  (`CronExpression`, then `RecurrenceRule`). Time zones are resolved with
  `TimeZoneInfo.FindSystemTimeZoneById`. The analyzers validate code-defined expressions at compile
  time (`IJOB0007`).

At startup `JobSchedulingService.InitializeAsync` submits all definitions and their code-defined schedules
through `MergeJobDefinitionsListAsync`. Storage atomically reconciles the catalogue, removing code-defined schedules when a definition disappears or loses its cron expression. It preserves
dynamic schedules. Unchanged cron/time zone retains the next occurrence; updates
preserve the pause state and last occurrence. The older `MergeRecurringSchedulesListAsync` operation
still performs an explicit global replacement and is not used by application startup.

## Materialization

Each polling iteration calls `GetDueRecurringAsync(now, AcquisitionBatchSize)` and, per schedule,
`MaterializeRecurringScheduleAsync`. One failing schedule is logged and skipped; it never blocks
others or acquisition.

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
   the occurrence and advances `NextRunAt`. The occurrence's `RecurringKey` is
   `{schedule name}:{occurrence ticks}`, so concurrent nodes can't create the same occurrence twice.
   Storage returns `false` for a stale or duplicate materialization.

## Operations

`JobMonitor` (and the dashboard) can pause, resume, and trigger a schedule immediately. Paused
schedules are excluded from the due scan.

The `Immediate.Jobs.NodaTime` package adds NodaTime overloads for scheduling, and serialization for
NodaTime payload types; `IJOB0004` reports a project that references NodaTime without it.
