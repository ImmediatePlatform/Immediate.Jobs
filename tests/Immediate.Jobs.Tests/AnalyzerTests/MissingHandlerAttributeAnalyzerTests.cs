using Immediate.Jobs.Analyzers;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class MissingHandlerAttributeAnalyzerTests
{
	[Fact]
	public async Task MissingHandlerAttributeShouldError() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<MissingHandlerAttributeAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			[Job]
			public sealed partial class {|IJOB0001:GetUsersQuery|}
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
	public async Task PresentHandlerAttributeShouldNotError() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<MissingHandlerAttributeAnalyzer>(
			"""
			using System.Threading;
			using System.Threading.Tasks;
			using Immediate.Handlers.Shared;
			using Immediate.Jobs.Shared;
			
			namespace Dummy;

			[Handler, Job]
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
