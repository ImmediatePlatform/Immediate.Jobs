# Conventions

The overriding rule: **make new code look like the code next to it.** Each provider and subsystem
has established idioms; follow them rather than introducing a parallel style.

## Build settings

From `Directory.Build.props` and `.editorconfig`:

- **Language.** Tabs for indentation, file-scoped namespaces, the language version selected by the
  build, and nullable reference types with nullable warnings as errors.
- **Analysis.** `AnalysisLevel` `latest-all`, code style enforced in the build, Meziantou analyzers,
  and warnings as errors in CI. A Release build must be warning-free.
- **Banned APIs** (`BannedSymbols.txt`): `System.DateTime` (use `DateTimeOffset` and `TimeProvider`),
  and `ToArray`/`ToArrayAsync` (use collection expressions or lists).
- **String comparisons** always pass a `StringComparison` or comparer, usually ordinal.

## Code patterns

- **Readability.** Insert blank lines between logical groups of statements to make code easier for
  human readers to follow. Split chained operations across lines when that makes them easier to read;
  use guard clauses and inline straightforward assignments. Name values for their domain role, such as
  `HandlerTags` and `JobTags`.
- **Async entry points.** Public async methods yield first with `await TaskScheduler.Yield()` (an
  internal helper that resumes on the thread pool, whatever the caller's synchronization context).
  Storage methods also log their "…Called" message and call
  `cancellationToken.ThrowIfCancellationRequested()` first.
- **Throwing.** Use the established exception types and throw helpers. Storage distinguishes missing
  targets from invalid state transitions; callers and the dashboard rely on that distinction
  (see [storage](storage.md#contract)).
- **Time.** Always come from an injected `TimeProvider`, so tests can use `FakeTimeProvider`.
- **Options.** Classes use Immediate.Validations (`[Validate]` with attribute rules) rather than
  hand-written validation.
- **Shared logic.** Extract repeated rules into helpers that express their meaning. Keep single-use
  logic with its caller when a separate helper adds no clarity. Analyzer and generator validation should
  agree, including the distinction between omitted settings and invalid explicit values. Public
  overloads should share the implementation of their common behavior.
- **Responsibilities.** Establish invariants at the owning configuration or model boundary, and let
  downstream code rely on them. Storage persists supplied snapshots without repeating normalization.
  Resolve storage-owned metadata inside storage rather than fetching it in callers.
- **Simplicity.** Add APIs, wrappers, allocations, defensive checks, and storage calls only when the
  contract requires them. An interface's mutability restrictions do not imply a particular wrapper
  or allocation. Use explicit discards only when required by the compiler or analyzer.
- **Generated code** in the Scriban templates uses `global::`-qualified names throughout.

## Documentation comments

- XML docs are required (`GenerateDocumentationFile`). Doc text is indented with a tab after `///`.
- Always use the repo-wide multiline XML documentation style: put opening tags (including
  `<param name="…">`), their contents, and closing tags on separate lines.
- Interfaces carry the full documentation, including `<exception>` tags; implementations use
  `/// <inheritdoc />`, adding `<remarks>` only for implementation-specific behaviour (for example a
  provider that throws `NotSupportedException`).

## Logging

All logging uses source-generated `[LoggerMessage]` partial methods with explicit `EventId`s from the
assembly's `LibraryEventIds`. Those files are the source of truth for allocated ids and ranges.
Append new ids within the assembly's range; preserve gaps left by removed events. Never reuse or
renumber an id, because users filter on them. `EventName` is `Immediate.Jobs.{Assembly}.{Name}`.

## Public API and compatibility

- **Pre-1.0 policy.** The library is pre-1.0. Breaking changes are acceptable when they simplify the
  design, but must be listed in the pull request: public API changes, custom-provider contract
  changes, EF Core schema changes, and wire-format changes (dashboard JSON uses string enums).
  The project does not provide migrations or backfills for prior pre-1.0 schemas; storage may need
  to be recreated when its schema changes.
- **Persisted enums.** `JobState` and `BatchState` are stored numerically: add members at the end,
  and never renumber.
- **Infrastructure types.** Types that must be public but aren't meant for direct use are marked
  `[EditorBrowsable(EditorBrowsableState.Never)]`, like `InMemoryJobStorage`.

## Maintaining documentation

Document decisions at the layer that owns them, and link to that explanation from related topics.
Keep contracts and the reasons behind them; leave individual patch history and one-time upgrade
steps in pull requests or release notes. See the [documentation scope](README.md#what-belongs-here).

## Analyzers

A new diagnostic needs:

- an id in `DiagnosticIds.cs` (`IJOB####`, the next free number);
- a row in `AnalyzerReleases.Unshipped.md`;
- the descriptor in its analyzer;
- tests in `tests/Immediate.Jobs.Tests/AnalyzerTests`.

## Tests

- Follow the existing test organization and assertion style. Put invalid generator cases in
  `InvalidJobsTests`, verify the expected generated files, and use the established snapshot helpers.
- Test the contract at the layer responsible for it. Tests should not require downstream code to
  repeat validation or normalization owned by another layer.

### Storage tests

- `InternalsVisibleTo` is forbidden in `Immediate.Jobs.Shared`. Exercise acquisition through public
  storage conformance methods; do not expose or independently test its internal selectors.
- Test provider-neutral storage behavior through conformance cases under
  `src/Immediate.Jobs.Testing/Storage` (see [testing](testing.md)). Cover a storage regression through
  that shared suite so it holds every applicable provider to the same rule. Avoid standalone tests
  tied to a particular provider and engine combination.

## Pull requests

- **Description.** Use a **Summary** (what changed and why, grouped by area) and a **Tests** section
  (what was added and what was run).
- **Stacked work.** Identify dependencies between stacked pull requests and merge them in order.
- **Docs.** Update `docs/` in the same pull request when behaviour described there changes.
