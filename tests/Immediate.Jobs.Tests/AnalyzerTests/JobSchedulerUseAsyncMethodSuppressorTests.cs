using Immediate.Jobs.Analyzers;
using Microsoft.NetCore.Analyzers.Runtime;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class JobSchedulerUseAsyncMethodSuppressorTests
{
	[Fact]
	public async Task JobSchedulerBatchMethodShouldBeSuppressed() =>
		await AnalyzerTestHelpers.CreateSuppressorTest<JobSchedulerUseAsyncMethodSuppressor, UseAsyncMethodInAsyncContext>(
			"""
			using Immediate.Jobs.Shared;
			using System.Threading.Tasks;

			public static class TestClass
			{
				public static async Task AddAsync(JobScheduler<string> scheduler, Batch batch)
				{
					scheduler.Enqueue("payload", batch);
					await Task.CompletedTask;
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task UnrelatedSynchronousMethodShouldNotBeSuppressed() =>
		await AnalyzerTestHelpers.CreateSuppressorTest<JobSchedulerUseAsyncMethodSuppressor, UseAsyncMethodInAsyncContext>(
			"""
			using System.IO;
			using System.Threading.Tasks;

			public static class TestClass
			{
				public static async Task ReadAsync(Stream stream)
				{
					{|CA1849:stream.Read(new byte[1], 0, 1)|};
					await Task.CompletedTask;
				}
			}
			"""
		).RunAsync(TestContext.Current.CancellationToken);
}
