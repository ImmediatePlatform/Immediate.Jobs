using Immediate.Jobs.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public sealed class NodaTimePackageRequiredAnalyzerTests
{
	[Fact]
	public async Task NodaTimeNotPresentDoesNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<NodaTimePackageRequiredAnalyzer>(
			string.Empty
		).RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task NodaTimePresentWithoutIJNodaTimeDoesNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<NodaTimePackageRequiredAnalyzer>(
			string.Empty,
			additionalReferences:
			[
				MetadataReference.CreateFromFile(typeof(global::NodaTime.Instant).Assembly.Location),
			]
		)
		.WithDiagnostic(
			new DiagnosticResult("IJOB0004", DiagnosticSeverity.Error)
		)
		.RunAsync(TestContext.Current.CancellationToken);

	[Fact]
	public async Task NodaTimePresentWithIJNodaTimeDoesNotTrigger() =>
		await AnalyzerTestHelpers.CreateAnalyzerTest<NodaTimePackageRequiredAnalyzer>(
			string.Empty,
			includeNodaTime: true
		).RunAsync(TestContext.Current.CancellationToken);
}
