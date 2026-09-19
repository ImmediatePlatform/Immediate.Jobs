using Immediate.Jobs.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
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
				public static async Task TestAsync(JobScheduler<string> scheduler, Batch batch)
				{
					{|#0:scheduler.Enqueue("payload", batch)|};
					await Task.CompletedTask;
				}
			}
			"""
		)
			.WithDiagnostic(
				new DiagnosticResult("CA1849", DiagnosticSeverity.Warning)
					.WithLocation(0)
					.WithIsSuppressed(true)
			)
			.RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task UnrelatedSynchronousMethodShouldNotBeSuppressed() =>
		await AnalyzerTestHelpers.CreateSuppressorTest<JobSchedulerUseAsyncMethodSuppressor, UseAsyncMethodInAsyncContext>(
			"""
			using System.IO;
			using System.Threading.Tasks;

			public static class TestClass
			{
				public static async Task TestAsync(Stream stream)
				{
					{|#0:stream.Read(new byte[1], 0, 1)|};
					await Task.CompletedTask;
				}
			}
			"""
		)
			.WithDiagnostic(
				new DiagnosticResult("CA1849", DiagnosticSeverity.Warning)
					.WithLocation(0)
			)
			.RunAsync(TestContext.Current.CancellationToken);
}
