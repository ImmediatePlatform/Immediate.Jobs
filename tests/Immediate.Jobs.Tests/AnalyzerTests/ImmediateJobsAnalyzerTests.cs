using Immediate.Jobs.Tests.GeneratorTests;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class ImmediateJobsAnalyzerTests
{
	public static TheoryData<string, string> InvalidJobs => new()
	{
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job(Cron = "not cron")] public sealed partial class BadCronJob { private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB001"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job("same")] public sealed partial class OneJob { private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask; }
			[Handler, Job("same")] public sealed partial class TwoJob { private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB002"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System; using System.Threading; using System.Threading.Tasks;
			[Handler, Job] public sealed partial class UnsupportedJob { public sealed record Payload(Action Callback) : IJobRequest { public JobDetails? JobDetails { get; set; } } private ValueTask HandleAsync(Payload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB003"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading.Tasks;
			[Handler, Job] public sealed partial class SignatureJob { private ValueTask HandleAsync() => ValueTask.CompletedTask; }
			""",
			"IJOB004"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job] public sealed class NonPartialJob { private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB005"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			public sealed record Payload(string Value) : IJobRequest { public JobDetails? JobDetails { get; set; } }
			[Handler, Job(Cron = "0 * * * *")] public sealed partial class CronPayloadJob { private ValueTask HandleAsync(Payload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB006"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			namespace NodaTime { public readonly struct Instant { } }
			public sealed record Payload(NodaTime.Instant Value) : IJobRequest { public JobDetails? JobDetails { get; set; } }
			[Handler, Job] public sealed partial class NodaJob { private ValueTask HandleAsync(Payload payload, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB007"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Handler, Job(MaxAttempts = 0)] public sealed partial class InvalidConfigurationJob { private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB008"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			[Job] public sealed partial class NotAHandlerJob { private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB009"
		},
		{
			"""
			using Immediate.Jobs.Shared;
			[QueueDefinition(Concurrency = -1)] public sealed class InvalidQueue;
			""",
			"IJOB010"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			public sealed class MissingDefinition;
			[Handler, Job, UsesQueue<MissingDefinition>] public sealed partial class InvalidQueueJob { private ValueTask HandleAsync(NoPayload request, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB011"
		},
		{
			"""
			using Immediate.Jobs.Shared; using Immediate.Handlers.Shared; using System.Threading; using System.Threading.Tasks;
			public sealed record Payload(string Value);
			[Handler, Job] public sealed partial class MissingRequestContractJob { private ValueTask HandleAsync(Payload request, CancellationToken ct) => ValueTask.CompletedTask; }
			""",
			"IJOB015"
		},
		{
			"""
			using Immediate.Jobs.Shared;
			[QueueDefinition(Name = "same")] public sealed class FirstQueue;
			[QueueDefinition(Name = "same")] public sealed class SecondQueue;
			""",
			"IJOB012"
		},
	};

	[Theory]
	[MemberData(nameof(InvalidJobs))]
	public async Task ReportsExpectedDiagnostic(string source, string expectedId)
	{
		var diagnostics = await GeneratorTestHelper.RunAnalyzer(source);
		Assert.Contains(diagnostics, diagnostic => diagnostic.Id == expectedId);
	}

	[Theory]
	[InlineData(
		"public sealed class BadExtractor;",
		"BadExtractor",
		"IJOB013"
	)]
	[InlineData(
		"public sealed record BadContext(System.Action Callback); public sealed class BadExtractor : IJobContextExtractor<BadContext> { public string Key => \"bad\"; public ValueTask<BadContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<BadContext?>(null); public ValueTask RestoreAsync(BadContext context, CancellationToken ct) => ValueTask.CompletedTask; }",
		"BadExtractor",
		"IJOB014"
	)]
	[InlineData(
		"namespace NodaTime { public readonly struct Instant { } } public sealed record BadContext(NodaTime.Instant Value); public sealed class BadExtractor : IJobContextExtractor<BadContext> { public string Key => \"bad\"; public ValueTask<BadContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<BadContext?>(null); public ValueTask RestoreAsync(BadContext context, CancellationToken ct) => ValueTask.CompletedTask; }",
		"BadExtractor",
		"IJOB007"
	)]
	public async Task ContextUsageReportsExpectedDiagnostic(string declaration, string extractor, string expectedId)
	{
		var source = $$"""
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System.Threading;
			using System.Threading.Tasks;
			{{declaration}}
			[Handler, Job, UsesJobContext<{{extractor}}>]
			public sealed partial class ContextJob
			{
				private ValueTask HandleAsync(NoPayload payload, CancellationToken ct) => ValueTask.CompletedTask;
			}
			""";

		var diagnostics = await GeneratorTestHelper.RunAnalyzer(source);

		Assert.Contains(diagnostics, diagnostic => diagnostic.Id == expectedId);
	}

	[Fact]
	public async Task ValidContextUsageIsClean()
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
				public ValueTask<ValidContext?> CaptureAsync(CancellationToken ct) => ValueTask.FromResult<ValidContext?>(new("one"));
				public ValueTask RestoreAsync(ValidContext context, CancellationToken ct) => ValueTask.CompletedTask;
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
}
