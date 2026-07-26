using Immediate.Jobs.Analyzers;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class DuplicateElementsAnalyzerTests
{
	[Fact]
	public async Task DuplicateJobNamesShouldTriggerAtAllLocations() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<DuplicateElementsAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[Handler, Job(Name = "my-name")]
			public sealed partial class {|IJOB0002:JobOne|}
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			
			[Handler, Job(Name = "my-name")]
			public sealed partial class {|IJOB0002:JobTwo|}
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task DuplicateJobNamesFromClassNameShouldTriggerAtAllLocations() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<DuplicateElementsAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[Handler, Job(Name = "my-name")]
			public sealed partial class {|IJOB0002:JobOne|}
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			
			[Handler, Job]
			public sealed partial class {|IJOB0002:MyNameJob|}
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task NonDuplicateJobNameShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<DuplicateElementsAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[Handler, Job]
			public sealed partial class JobOne
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			
			[Handler, Job]
			public sealed partial class JobTwo
			{
				private async ValueTask Handle(EmptyJobRequest _, CancellationToken token) { }
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task DuplicateQueueNamesShouldTriggerAtAllLocations() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<DuplicateElementsAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[QueueDefinition(Name = "my-name")]
			public sealed partial class {|IJOB0003:QueueOne|};
			
			[QueueDefinition(Name = "my-name")]
			public sealed partial class {|IJOB0003:QueueTwo|};
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task DuplicateQueueNamesFromClassNameShouldTriggerAtAllLocations() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<DuplicateElementsAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[QueueDefinition(Name = "my-name-queue")]
			public sealed partial class {|IJOB0003:QueueOne|};

			[QueueDefinition]
			public sealed partial class {|IJOB0003:MyNameQueue|};
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task NonDuplicateQueueNameShouldNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<DuplicateElementsAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;
			
			[QueueDefinition]
			public sealed partial class QueueOne;
			
			[QueueDefinition]
			public sealed partial class QueueTwo;
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
