# Design: Consolidate storage interfaces

**Reduce the storage hierarchy to `IJobStorage` and `IJobGraphStorage`.**

Status: Approved v0.2 · Date: 2026-10-02 · Package: `Immediate.Jobs` (core) and
all storage providers. Revises [`storage-capabilities.md`](storage-capabilities.md).

---

## 1. Proposal

Today a provider assembles its feature set from six interfaces and reports it through a flags enum.
In practice only one split matters: **graph support** (batches and continuations). Graph support needs
atomic multi-row writes that some backends can't offer. Recurring schedules and fair acquisition don't
have that problem; any realistic backend can implement them.

So:

- **`IJobStorage`** covers queue, recurring, and fair acquisition. Every provider implements all of it.
- **`IJobGraphStorage : IJobStorage`** covers batches and continuations, plus the two
  single-server recovery reads that only graph-capable providers need.
- **Capability reporting** keeps the `StorageCapabilities` flags enum, reduced to `Queue` and `Graph`.
  The enum stays rather than becoming a boolean so callers keep using the same type, and so a future
  capability boundary can be added without another reshape.

Immediate.Jobs is pre-1.0 (`v0.6.x`, preview), so breaking changes to custom providers are
acceptable.

## 2. Current state

| Interface | Members | In-memory | EF Core | LinqToDB | Single-server | Redis | Capturing (testing) |
| --- | --- | :-: | :-: | :-: | :-: | :-: | :-: |
| `IJobStorage` | 18 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `IRecurringJobStorage` | 7 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `IFairQueueStorage` | 0 (marker) | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `IJobGraphStorage` | 11 | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `IJobStorageReplica` | 1 | ❌ | ✅ | ✅ | — | ❌ | ❌ |
| `IJobGraphStorageReplica` | 1 | ❌ | ✅ | ✅ | — | ❌ | ❌ |

What this shows:

- **Recurring is already universal.** Every provider implements it, yet the runtime still guards it:
  - `JobStorageCapabilityGuards.RequireRecurring`, called from the scheduler's recurring methods;
  - a "recurring features disabled" startup log in `JobSchedulingService`;
  - nullable-result branches in `JobMonitor`;
  - a single-server constructor check;
  - a `Recurring` check in the dashboard (`App.vue`).
- **`IFairQueueStorage` gates nothing.** It is a marker with no members, used only to set the
  `FairQueues` flag. Fair acquisition is actually requested through `JobAcquisitionRequest.FairQueues`,
  which every provider receives. Redis rejects it at runtime with `NotSupportedException` ("Direct
  distributed fair-queue acquisition is not supported by the Redis provider"), and nothing warns at
  startup.
- **The replica interfaces aren't really capabilities.** They are the extra reads single-server mode
  needs to recover from its durable store. Single-server already requires its durable store to be
  graph-capable, so only graph providers ever implement them.
- **Graph is the only real distinction.** Redis lacks it, and that is the one split the dashboard,
  scheduler, and monitor need to know about.

## 3. Target shape

```text
IJobStorage                 queue + recurring + fair acquisition         every provider
└── IJobGraphStorage        batches, continuations, single-server reads  SQL providers, in-memory
```

### 3.1 `IJobStorage`

- **Recurring methods move in.** The seven recurring methods (`MergeRecurringSchedulesListAsync`,
  `UpsertRecurringAsync`, `RemoveRecurringAsync`, `PauseRecurringAsync`, `ResumeRecurringAsync`,
  `GetDueRecurringAsync`, `MaterializeRecurringAsync`) move into `IJobStorage`.
  `IRecurringJobStorage` is deleted.
- **Fair acquisition becomes part of the contract.** `AcquireDueJobsAsync` must honor
  `JobAcquisitionRequest.FairQueues` when it is set. `IFairQueueStorage` is deleted.

### 3.2 `IJobGraphStorage`

- **Existing graph members are unchanged.**
- **The replica reads are folded in:**
  - `AcquireJobsAsync(IReadOnlyCollection<JobHandle>, string workerId, TimeSpan lease, ...)` (from
    `IJobStorageReplica`);
  - `GetIncomingEdgesAsync(IReadOnlyCollection<JobHandle>, ...)` (from `IJobGraphStorageReplica`).

  Both replica interfaces are deleted.

  In-memory, single-server, and capturing storage implement both reads by throwing
  `NotSupportedException`, with XML docs explaining that they can't act as a single-server durable
  store. Single-server rejects them by type before it would ever call these reads, so the throw is
  never reached in a valid configuration.

### 3.3 Capability reporting

- **The flags enum stays, with fewer members.** `StorageCapabilities` keeps `None`, `Queue = 1`, and
  `Graph = 4`. `Recurring = 2`, `FairQueues = 8`, and `Replica = 16` are removed. The remaining values
  keep their numbers, so serialized snapshots and health-check data stay readable.
- **The extension stays.** `JobStorageCapabilities.GetCapabilities` returns `Queue`, plus `Graph` when
  the storage is `IJobGraphStorage`.
- **Reporting is unchanged in shape.** `JobMonitoringSnapshot.Capabilities` and the health check's
  `storageCapabilities` entry keep their types. The dashboard keeps its `Graph` check and drops the
  `Recurring` check in `App.vue`.
- **Only the graph guard remains.** `JobStorageCapabilityGuards` keeps `RequireGraph` and loses
  `RequireRecurring`.

## 4. Changes by area

| Area | Change |
| --- | --- |
| `JobScheduler<TPayload>` | Recurring methods call `Storage` directly. |
| `JobSchedulingService` | Drop the recurring-disabled log and the `is IRecurringJobStorage` branches. Keep the graph-disabled log. `OverlapPolicy.Queue` still requires graph. |
| `JobMonitor` | Recurring operations no longer have an unsupported path. Graph reads keep theirs. |
| `JobAttribute` | The `OverlapPolicy.Queue` remark mentions only graph support. |
| `SingleServerJobStorage` | Durable-store validation becomes: not in-memory, not single-server, must be `IJobGraphStorage`. Four type checks and four cast properties collapse into one. |
| EF Core / LinqToDB | Change the declared interfaces. No behavior changes. |
| In-memory / single-server / capturing | Add the two replica reads as documented `NotSupportedException` throws. |
| Redis | Implement fair acquisition (§5). Drop the `IRecurringJobStorage` declaration. |
| Dashboard | Remove the `Recurring` capability check; keep the `Graph` check. |
| Docs | Update `storage-capabilities.md`, `provider-suitability.md`, the root readme, and the Redis readme ("Redis does not support fair queues"). |

## 5. Redis fair acquisition

This is the main cost of the proposal. Without it, `IJobStorage` would promise something Redis
rejects at runtime, the very situation the proposal is meant to remove.

[`fair-queues.md`](fair-queues.md) defines the behavior. Acquisition needs two pieces of state per
group, both of which map onto Redis structures updated by the acquisition Lua script:

- **Durable last-served order** for round-robin across groups. SQL keeps this in
  `immediate_fair_queue_groups`; in Redis it becomes a sorted set scored by last-served time.
- **In-flight counts per group** for noisy-neighbor detection. In Redis this becomes a hash
  incremented on acquire and decremented on complete, fail, and lease expiry.

The acquisition script picks due jobs, then filters and orders them by group using both structures, all
in one atomic script, the same way the existing queue scripts work. The shared fair-queue conformance
suite then runs against Redis like any other provider.

This work is done as a separate PR stacked on the interface merge, to keep both reviews focused. The
two ship in the same release, so no release has an `IJobStorage` contract that Redis doesn't fulfil.

## 6. Effect on other plans

[`wait-for-trigger.md`](wait-for-trigger.md) currently adds `IJobTriggerStorage`,
`IJobGraphTriggerStorage`, and a `StorageCapabilities.Triggers` flag. Under this proposal:

- `UpdatePayloadAsync` and `TryTriggerAsync` move into `IJobStorage`.
- `TryTriggerBatchAsync` moves into `IJobGraphStorage`.
- There is no `Triggers` flag and no creation-time capability check. Only the parent and batch variants
  call `RequireGraph`.

That plan should be updated if this one is accepted.

## 7. Testing

- **Conformance suites regroup into two tiers.** The queue, recurring, and fair-queue suites become one
  tier that every provider must pass. The graph and replica suites become the graph tier.
  `JobStorageConformanceSuite` selects tiers by `is IJobGraphStorage` instead of capability flags.
- **Redis** passes the full base tier, including fair queues.
- **`StorageCapabilityTests`:**
  - checks that graph APIs fail fast on non-graph storage;
  - checks that the snapshot and health check report `Queue` or `Queue | Graph`;
  - drops the recurring and fair-queue cases.
- **Single-server:**
  - accepts EF Core and LinqToDB as durable stores;
  - rejects in-memory, single-server, and non-graph storage with clear messages.

## 8. Breaking changes

- **Custom `IJobStorage` implementations** must add the seven recurring methods and honor
  `JobAcquisitionRequest.FairQueues`.
- **Custom graph providers** must add the two replica reads. They may throw `NotSupportedException` if
  the provider is never meant to back single-server mode.
- **Removed public types:** `IRecurringJobStorage`, `IFairQueueStorage`, `IJobStorageReplica`,
  `IJobGraphStorageReplica`.
- **Removed enum members:** `StorageCapabilities.Recurring`, `FairQueues`, and `Replica`. The monitoring
  JSON and health-check string no longer contain `Recurring` or `FairQueues`.

## 9. Milestones

1. **Interface merge** (PR 1):
   - move the recurring and replica members, delete the four interfaces;
   - trim `StorageCapabilities`;
   - update providers, single-server, the scheduler, the monitor, testing storage, and the dashboard
     check;
   - regroup the conformance suites, with Redis temporarily excluded from the fair-queue suite.
2. **Redis fair acquisition** (PR 2, stacked on PR 1):
   - implement §5 and run the full base tier against Redis;
   - remove the `NotSupportedException` and the temporary exclusion.

   It is reviewed separately but released together with PR 1.
3. **Docs**, plus the follow-up edit to `wait-for-trigger.md`.

## 10. Decisions

1. **Two interfaces:** `IJobStorage` (queue, recurring, fair acquisition) and `IJobGraphStorage`. Graph
   support is the only split that backends realistically need.
2. **Replica reads fold into `IJobGraphStorage`.** Implementations that can't be a durable store
   (in-memory, single-server, capturing) document that and throw `NotSupportedException`.
3. **`StorageCapabilities` stays as a flags enum**, trimmed to `Queue` and `Graph` with their existing
   values. Callers keep using the type, and a future capability boundary has a place to go.
4. **Redis fair acquisition is a separate stacked PR**, completed separately but released together with
   the merge.
