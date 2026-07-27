using Immediate.Jobs.Analyzers;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class QueueDefinitionAnalyzerTests
{
	[Fact]
	public async Task MissingQueueDefinitionAttributeShouldError() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<QueueDefinitionAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			public sealed class MyQueue;

			[Handler, Job, {|IJOB0009:UsesQueue<MyQueue>|}]
			public sealed partial class GetUsersQuery
			{
				public record Query;

				private async ValueTask Handle(
					Query _,
					CancellationToken token)
				{
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task PresentQueueDefinitionAttributeShouldNotError() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<MissingHandlerAttributeAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			[QueueDefinition]
			public sealed class MyQueue;

			[Handler, Job, UsesQueue<MyQueue>]
			public sealed partial class GetUsersQuery
			{
				public record Query;

				private async ValueTask Handle(
					Query _,
					CancellationToken token)
				{
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
