using Immediate.Jobs.Tests.GeneratorTests;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class ImmediateJobsAnalyzerTests
{
	[Fact]
	public async Task AnalyzerTriggersForInvalidCron() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job(Cron = "not cron")]
			public sealed partial class BadCronJob
			{
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB001"
		);

	[Fact]
	public async Task AnalyzerTriggersForDuplicateJobName() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job("same")]
			public sealed partial class OneJob
			{
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}

			[Handler, Job("same")]
			public sealed partial class TwoJob
			{
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB002"
		);

	[Fact]
	public async Task AnalyzerTriggersForUnsupportedPayload() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class UnsupportedJob
			{
				public sealed record Payload(Action Callback) : IJobRequest
				{
					public JobDetails? JobDetails { get; set; }
				}

				private ValueTask HandleAsync(Payload payload, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB003"
		);

	[Fact]
	public async Task AnalyzerTriggersForInvalidMethodSignature() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed partial class SignatureJob
			{
				private ValueTask HandleAsync() => ValueTask.CompletedTask;
			}
			""",
			"IJOB004"
		);

	[Fact]
	public async Task AnalyzerTriggersForNonPartialJob() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job]
			public sealed class NonPartialJob
			{
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB005"
		);

	[Fact]
	public async Task AnalyzerTriggersForCronJobWithPayload() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			public sealed record Payload(string Value) : IJobRequest
			{
				public JobDetails? JobDetails { get; set; }
			}

			[Handler, Job(Cron = "0 * * * *")]
			public sealed partial class CronPayloadJob
			{
				private ValueTask HandleAsync(Payload payload, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB006"
		);

	[Fact]
	public async Task AnalyzerTriggersForNodaTimePayloadWithoutIntegrationPackage() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			namespace NodaTime
			{
				public readonly struct Instant;
			}

			public sealed record Payload(NodaTime.Instant Value) : IJobRequest
			{
				public JobDetails? JobDetails { get; set; }
			}

			[Handler, Job]
			public sealed partial class NodaJob
			{
				private ValueTask HandleAsync(Payload payload, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB007"
		);

	[Fact]
	public async Task AnalyzerTriggersForInvalidJobConfiguration() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Handler, Job(MaxAttempts = 0)]
			public sealed partial class InvalidConfigurationJob
			{
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB008"
		);

	[Fact]
	public async Task AnalyzerTriggersForJobWithoutHandlerAttribute() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			[Job]
			public sealed partial class NotAHandlerJob
			{
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB009"
		);

	[Fact]
	public async Task AnalyzerTriggersForInvalidQueueConfiguration() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;

			[QueueDefinition(Concurrency = -1)]
			public sealed class InvalidQueue;
			""",
			"IJOB010"
		);

	[Fact]
	public async Task AnalyzerTriggersForInvalidQueueTarget() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			public sealed class MissingDefinition;

			[Handler, Job, UsesQueue<MissingDefinition>]
			public sealed partial class InvalidQueueJob
			{
				private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB011"
		);

	[Fact]
	public async Task AnalyzerTriggersForDuplicateQueueName() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;

			[QueueDefinition(Name = "same")]
			public sealed class FirstQueue;

			[QueueDefinition(Name = "same")]
			public sealed class SecondQueue;
			""",
			"IJOB012"
		);

	[Fact]
	public async Task AnalyzerDoesNotTriggerWhenJobRequestContractIsAbsent()
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			public sealed record Payload(string Value);

			[Handler, Job]
			public sealed partial class PlainRequestJob
			{
				private ValueTask HandleAsync(Payload request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			""";

		var diagnostics = await GeneratorTestHelper.RunAnalyzer(source);

		Assert.Empty(diagnostics);
	}

	[Fact]
	public async Task AnalyzerTriggersWhenContextExtractorDoesNotImplementContract() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			public sealed class BadExtractor;

			[Handler, Job, UsesJobContext<BadExtractor>]
			public sealed partial class ContextJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB013"
		);

	[Fact]
	public async Task AnalyzerTriggersForUnsupportedContext() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;

			public sealed record BadContext(Action Callback);

			public sealed class BadExtractor : IJobContextExtractor<BadContext>
			{
				public string Key => "bad";
				public BadContext? Capture() => null;
				public void Restore(BadContext context) { }
			}

			[Handler, Job, UsesJobContext<BadExtractor>]
			public sealed partial class ContextJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB014"
		);

	[Fact]
	public async Task AnalyzerTriggersForNodaTimeContextWithoutIntegrationPackage() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			namespace NodaTime
			{
				public readonly struct Instant;
			}

			public sealed record BadContext(NodaTime.Instant Value);

			public sealed class BadExtractor : IJobContextExtractor<BadContext>
			{
				public string Key => "bad";
				public BadContext? Capture() => null;
				public void Restore(BadContext context) { }
			}

			[Handler, Job, UsesJobContext<BadExtractor>]
			public sealed partial class ContextJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""",
			"IJOB007"
		);

	[Fact]
	public async Task AnalyzerDoesNotTriggerForValidContextUsage()
	{
		var source = """
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;

			public sealed record ValidContext(string TenantId);

			public sealed class ValidExtractor : IJobContextExtractor<ValidContext>
			{
				public string Key => "tenant";
				public ValidContext? Capture() => new("one");
				public void Restore(ValidContext context) { }
			}

			[Handler, Job, UsesJobContext<ValidExtractor>]
			public sealed partial class ContextJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""";

		var diagnostics = await GeneratorTestHelper.RunAnalyzer(source);

		Assert.Empty(diagnostics);
	}

	[Fact]
	public async Task AnalyzerRejectsConstantDetachedMidJobBatchAddition() =>
		await AssertDiagnostic(
			"""
			using Immediate.Jobs.Shared;

			public sealed class Scheduler
			{
				public void AddToBatchAsync(JobDetails current, int payload, ContinuationOptions options) { }

				public void Schedule(JobDetails current) =>
					AddToBatchAsync(current, 42, ContinuationOptions.Detached);
			}
			""",
			"IJOB020"
		);

	[Fact]
	public async Task AnalyzerAllowsNonConstantMidJobBatchOption()
	{
		var diagnostics = await GeneratorTestHelper.RunAnalyzer(
			"""
			using Immediate.Jobs.Shared;

			public sealed class Scheduler
			{
				public void AddToBatchAsync(JobDetails current, int payload, ContinuationOptions options) { }

				public void Schedule(JobDetails current, ContinuationOptions options) =>
					AddToBatchAsync(current, 42, options);
			}
			"""
		);

		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "IJOB020");
	}

	private static async Task AssertDiagnostic(string source, string expectedId)
	{
		var diagnostics = await GeneratorTestHelper.RunAnalyzer(source);

		Assert.Contains(diagnostics, diagnostic => diagnostic.Id == expectedId);
	}
}
