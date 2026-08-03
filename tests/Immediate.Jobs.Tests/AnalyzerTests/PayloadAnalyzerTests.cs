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
			using System.Collections.Generic;
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
				public sealed record Payload(
					string Message,
					long? MaxNodeOption,
					int[] Array,
					List<int> List,
					Dictionary<Guid, int> Dictionary
				) : IJobRequest { public JobDetails? JobDetails { get; set; } }
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

	[Fact]
	public async Task UnsupportedJobPayloadPropertyTypesShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<PayloadAnalyzer>(
			"""
			#nullable enable
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.Collections.Generic;
			using System.IO;
			using System.Threading;
			using System.Threading.Tasks;
			
			public interface IContract;
			public abstract class AbstractPayloadMember;
			public delegate string Formatter(string value);
			public sealed record UnsupportedKey(string Value);
			
			[Handler, Job]
			public sealed partial class UnsupportedPayloadJob
			{
				public sealed class Payload : IJobRequest
				{
					public IContract {|IJOB0013:Contract|} { get; init; } = null!;
					public AbstractPayloadMember {|IJOB0013:Abstract|} { get; init; } = null!;
					public Formatter {|IJOB0013:Format|} { get; init; } = null!;
					public Type {|IJOB0013:RuntimeType|} { get; init; } = null!;
					public Stream {|IJOB0013:Stream|} { get; init; } = null!;
					public List<Stream> {|IJOB0013:ListOfStream|} { get; init; } = null!;
					public HashSet<int> {|IJOB0013:Set|} { get; init; } = null!;
					public Dictionary<UnsupportedKey, int> {|IJOB0013:DictionaryWithUnsupportedKey|} { get; init; } = null!;
					public int[,] {|IJOB0013:MultiDimensionalArray|} { get; init; } = null!;
					public JobDetails? JobDetails { get; set; }
				}
			
				private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task UnsupportedContextPayloadPropertyTypesShouldTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<PayloadAnalyzer>(
			"""
			#nullable enable
			using Immediate.Jobs.Shared;
			using Immediate.Handlers.Shared;
			using System;
			using System.IO;
			using System.Threading;
			using System.Threading.Tasks;
			
			public interface IContract;
			public abstract class AbstractContextMember;
			public delegate string Formatter(string value);
			
			public sealed class UnsupportedContext
			{
				public IContract {|IJOB0014:Contract|} { get; init; } = null!;
				public AbstractContextMember {|IJOB0014:Abstract|} { get; init; } = null!;
				public Formatter {|IJOB0014:Format|} { get; init; } = null!;
				public Type {|IJOB0014:RuntimeType|} { get; init; } = null!;
				public Stream {|IJOB0014:Stream|} { get; init; } = null!;
			}
			
			public sealed class UnsupportedContextExtractor : JobContextExtractor<UnsupportedContext>
			{
				public override string Key => "unsupported";
				public override UnsupportedContext? Capture() => null;
				public override void Restore(UnsupportedContext context) { }
			}
			
			[Handler, Job, UsesJobContext<UnsupportedContextExtractor>]
			public sealed partial class ContextualJob
			{
				private ValueTask HandleAsync(EmptyJobRequest payload, CancellationToken cancellationToken) => ValueTask.CompletedTask;
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
