# Storage provider suitability

> **Status:** Guidance / reference. Informs which backends are worth building providers for, and why
> batches/continuations gate the choice.
> **See also:** [`storage-capabilities.md`](storage-capabilities.md) (how a provider implements a
> subset of the surface) and [`fair-queues.md`](fair-queues.md) (per-group fairness at acquisition).

## 1. The contract has two personalities

`IJobStorage` is a 32-method seam, but the methods fall into two very different workloads, and no
single storage technology is equally good at both:

- **(a) Mutable claim / lease / state-machine.** Enqueue a row, atomically claim due work with a
  lease, renew, complete, fail, retry, reclaim orphaned leases. This is random-access mutation of
  individual rows with optimistic concurrency. **KV and wide-column stores excel here.**
- **(b) Atomic multi-item graph + rich queries.** `EnqueueBatchAsync` writes a header + N members + M
  edges **all-or-nothing**; completing a job atomically decrements dependent counters and promotes
  children; the dashboard runs ad-hoc queries (counts by state, name search, pagination, batch
  graphs). **Relational / transactional stores excel here; KV stores struggle.**

Since batches and continuations are now core features (see [`../spec.md`](../spec.md) §2.8), workload
(b) is a first-class requirement — which raises the bar for any non-transactional backend.

The practical consequence is captured by [`storage-capabilities.md`](storage-capabilities.md): a
provider may implement only the capabilities it can honor. Workload (a) ≈ the **queue** capability;
workload (b) ≈ the **graph** capability. A store that's great at (a) and bad at (b) ships as a
**queue-only** provider, and we tell people **batching needs a SQL database**.

## 2. Backend matrix

| Backend | Claim/lease (a) | Atomic batches + graph (b) | Dashboard queries | Verdict |
| --- | --- | --- | --- | --- |
| **Relational SQL** (optimistic claims) | Strong | **Strong** | Strong | Full fidelity; PostgreSQL can scale further with tuned claims and partitioning |
| **DynamoDB** | Excellent (conditional writes) | Weak (100-item txn cap) | Weak (GSI-per-pattern, no text search) | Great **queue-only** provider; TTL purge is a bonus |
| **Cassandra / ScyllaDB** | Good (LWT) | Weak (batches ≠ ACID at scale) | Weak | Same shape as DynamoDB |
| **Redis** | Excellent (Lua atomicity) | Weak (durability + multi-key limits) | Weak | Prime **queue-only** target; memory-bound |
| **FoundationDB** | Good | **Strong — real multi-key ACID** | Manual (build a layer) | Best technical fit for (b); operationally heavy/niche |
| **Kafka / NATS JetStream** | Poor (no per-item claim/mutate/delete) | N/A | Poor | Log model fights this contract — avoid |

**Headline:** portable optimistic concurrency supplies correctness on every validated relational
database. If throughput is the driver, PostgreSQL-specific `SKIP LOCKED` claiming and table
partitioning are useful future optimizations while retaining *full* feature fidelity. Reach for a
KV/wide-column store only when its scale/latency envelope justifies a queue-only provider.

## 3. DynamoDB in detail

A representative KV/wide-column analysis (Cassandra/Scylla/Redis rhyme with it).

**Strong fits — the queue capability:**
- **Claim model** maps directly onto conditional writes: `UpdateItem` with
  `ConditionExpression: State = :pending AND version = :v` gives the same "two nodes never claim the
  same version" guarantee EF Core gets from its concurrency stamp.
- **Due-work + orphan reclaim** via GSIs on `DueAt` and `LeaseExpiresAt`.
- **Recurring dedupe** — `MaterializeRecurringAsync`'s exactly-once is a conditional `PutItem` with
  `attribute_not_exists(PK)` on `jobName#occurrence`. Cleaner than a SQL unique constraint.
- **History purge** — `PurgeAsync` is just **DynamoDB TTL**; auto-deletes terminal rows. *Better* than
  the SQL provider here.

**Poor fits — the graph capability:**
- **Atomic batches.** `TransactWriteItems` caps at **100 items / 4 MB**. The flagship
  "1000 emails, atomic, retry-safe" batch **cannot** be one transaction. A two-phase visibility scheme
  (write members in chunks, then flip a header `Building`→`Committed`) weakens the mechanical
  all-or-nothing guarantee.
- **Large continuation cascades.** Per-item counter decrements are atomic, but a big fan-in/fan-out or
  `Success` subtree-cancel exceeds the 100-item transaction boundary.
- **Dashboard queries.** GSI per access pattern, no `LIKE`/text search (name search → scan or external
  index), counts must be maintained as atomic counters.
- **Hot partitions.** One queue = one GSI partition key ≈ 1000 WCU ceiling; real throughput needs
  **sharded queue keys** (`queue#shardN`), which makes ordered claiming approximate.

**Conclusion:** DynamoDB is an excellent **queue-only** provider and a poor **graph** provider — the
exact split the capability model expects. Ship it implementing `IJobQueueStorage`
(+ `IRecurringJobStorage`); direct batching users to SQL.

## 4. Fair queues across backends

Per-group fairness ([`fair-queues.md`](fair-queues.md)) lives inside the acquisition path, but it does
not automatically travel with the queue capability. The stateful round-robin cursor must be made
atomic with each provider's claim operation. Phase 1 implements that contract in memory and in the
EF Core and LinqToDB SQL providers; single-server mode inherits the in-memory decision. Redis persists
`GroupId` but explicitly rejects fair acquisition until its Lua claim path gains equivalent
cluster-wide cursor semantics.

## 5. Recommendations

1. **Default / full-fidelity:** SQL through either the EF Core or LinqToDB adapter. PostgreSQL,
   SQLite, and SQL Server are validated; PostgreSQL with `SKIP LOCKED` and partitioning remains a
   useful future optimization for high-throughput deployments, not a requirement for correctness.
2. **High-throughput queue-only:** Redis first (Lua atomicity, simplest), DynamoDB second (managed
   scale, TTL purge). Both ship as queue(+recurring) providers; batching requires SQL.
3. **Avoid** log-oriented systems (Kafka/JetStream) as a primary `IJobStorage` — the model fights the
   claim/lease/mutable-state contract.
4. **FoundationDB** is the one non-relational store that could do the graph capability well, but its
   operational weight makes it a niche choice, not a default.
