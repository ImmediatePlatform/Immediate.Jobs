using Immediate.Jobs.Analyzers;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class PayloadAnalyzerTests
{
	[Fact]
	public async Task ValidJobPayloadAndContextPayloadShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<PayloadAnalyzer>(
			"""
			#nullable enable
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			
			public enum Region { Europe, America }
			public sealed record UsageContext(Guid UserId, Region Region);
			public sealed class CorrelationContext
			{
				public CorrelationContext(string value) => Value = value;
				public string Value { get; }
			}
			
			public sealed class UsageContextExtractor : JobContextExtractor<UsageContext>
			{
				public override string Key => "usage";
				public override UsageContext? Capture() => new(Guid.Empty, Region.Europe);
				public override void Restore(UsageContext context) { }
			}
			
			public sealed class CorrelationExtractor : JobContextExtractor<CorrelationContext>
			{
				public override string Key => "correlation";
				public override CorrelationContext? Capture() => new("abc");
				public override void Restore(CorrelationContext context) { }
			}
			
			[UsesJobContext<UsageContextExtractor>]
			[UsesJobContext<CorrelationExtractor>]
			public sealed class WebJobAttribute : Attribute;
			
			[Handler, Job, UsesJobContext<CorrelationExtractor>, WebJob]
			public sealed partial class ContextualJob
			{
				public sealed record Payload(string Message) : IJobRequest { public JobDetails? JobDetails { get; set; } }
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task PointerJobPayloadAndContextPayloadShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<PayloadAnalyzer>(
			"""
			#nullable enable
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			
			public enum Region { Europe, America }
			public sealed record UsageContext(Guid UserId, Region Region);
			public unsafe sealed class CorrelationContext
			{
				public CorrelationContext(string value) => Value = null;
				public int* {|IJOB0014:Value|} { get; }
			}
			
			public sealed class UsageContextExtractor : JobContextExtractor<UsageContext>
			{
				public override string Key => "usage";
				public override UsageContext? Capture() => new(Guid.Empty, Region.Europe);
				public override void Restore(UsageContext context) { }
			}
			
			public sealed class CorrelationExtractor : JobContextExtractor<CorrelationContext>
			{
				public override string Key => "correlation";
				public override CorrelationContext? Capture() => new("abc");
				public override void Restore(CorrelationContext context) { }
			}
			
			[UsesJobContext<UsageContextExtractor>]
			[UsesJobContext<CorrelationExtractor>]
			public sealed class WebJobAttribute : Attribute;
			
			[Handler, Job, UsesJobContext<CorrelationExtractor>, WebJob]
			public sealed partial class ContextualJob
			{
				public unsafe sealed class Payload : IJobRequest
				{
					public int* {|IJOB0013:Value|} { get; init; }
					public JobDetails? JobDetails { get; set; }
				}

				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
