# Conventions

The overriding rule: **make new code look like the code next to it.** Each provider and subsystem
has established idioms; follow them rather than introducing a parallel style.

## Build settings

From `Directory.Build.props` and `.editorconfig`:

- **Language.** Tabs for indentation, file-scoped namespaces, `LangVersion` preview (the code uses
  C# 14 extension blocks and C# 15 `[with(...)]` collection-expression arguments), nullable enabled
  with nullable warnings as errors.
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
- **Throwing.** Use the throw helpers: `ImmediateJobException.Throw(message)` for invalid operations,
  and the `ArgumentException.Throw(paramName, message)` /
  `ArgumentOutOfRangeException.Throw(paramName, message)` extensions in `Internals/Extensions.cs`.
  Storage throws `KeyNotFoundException` for missing ids (see [storage](storage.md#contract)).
- **Time.** Always come from an injected `TimeProvider`, so tests can use `FakeTimeProvider`.
- **Options.** Classes use Immediate.Validations (`[Validate]` with attribute rules) rather than
  hand-written validation.
- **Shared logic.** Reuse existing helpers before writing another implementation. Extract repeated
  rules into a helper that expresses their meaning; analyzer and generator validation should agree,
  including the distinction between omitted settings and invalid explicit values. Scheduler overloads
  delegate to private `…CoreAsync` helpers.
- **Responsibilities.** Establish invariants at the owning configuration or model boundary, and let
  downstream code rely on them. Storage persists supplied snapshots without repeating normalization.
  Resolve storage-owned metadata inside storage rather than fetching it in callers.
- **Simplicity.** Add APIs, wrappers, allocations, defensive checks, and storage calls only when the
  contract requires them. An `IReadOnlyList<T>` return type alone does not require `AsReadOnly()`.
  Use `_ =` only when the compiler or analyzer requires an explicit discard.
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
assembly's `LibraryEventIds`:

| Assembly | Range in use |
| --- | --- |
| `Immediate.Jobs.Shared` | 11000–11103 (scheduler, in-memory, single-server) |
| `Immediate.Jobs.EntityFrameworkCore` | 11500–11547 |
| `Immediate.Jobs.LinqToDB` | 11600–11647 |
| `Immediate.Jobs.Redis` | 11700–11733 |

Append new ids after the current maximum of the assembly's range; never reuse or renumber an id,
because users filter on them. `EventName` is `Immediate.Jobs.{Assembly}.{Name}`.

## Public API and compatibility

- **Pre-1.0 policy.** The library is pre-1.0. Breaking changes are acceptable when they simplify the
  design, but must be listed in the pull request: public API changes, custom-provider contract
  changes, EF Core schema changes, and wire-format changes (dashboard JSON uses string enums).
- **Persisted enums.** `JobState` and `BatchState` are stored numerically: add members at the end,
  and never renumber.
- **Infrastructure types.** Types that must be public but aren't meant for direct use are marked
  `[EditorBrowsable(EditorBrowsableState.Never)]`, like `InMemoryJobStorage`.

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

- Add storage tests only as conformance tests under `src/Immediate.Jobs.Testing/Storage` (see
  [testing](testing.md)). A storage test must never be specific to a particular combination of
  storage provider and underlying engine; cover the behaviour through the shared conformance suite.

## Pull requests

- **Description.** Use a **Summary** (what changed and why, grouped by area) and a **Tests** section
  (what was added and what was run).
- **Stacked work.** Each pull request names the one it is stacked on, and is merged in order.
- **Docs.** Update `docs/` in the same pull request when behaviour described there changes.
