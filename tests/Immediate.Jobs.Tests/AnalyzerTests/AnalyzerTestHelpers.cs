using System.Diagnostics.CodeAnalysis;
using Immediate.Handlers.Generators;
using Immediate.Jobs.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Immediate.Jobs.Tests.AnalyzerTests;

internal static class AnalyzerTestHelpers
{
	public static CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> CreateAnalyzerTest<TAnalyzer>(
		[StringSyntax("c#-test")] string inputSource,
		bool includeNodaTime = false,
		params ReadOnlySpan<MetadataReference> additionalReferences
	)
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		var csTest = new CSharpGeneratorAnalyzerTest<TAnalyzer, DefaultVerifier>
		{
			TestBehaviors = TestBehaviors.SkipGeneratedSourcesCheck,
			TestState =
			{
				Sources = { inputSource },
				ReferenceAssemblies = Utility.ReferenceAssemblies,
			},
		};

		csTest.TestState.AdditionalReferences
			.AddRange(Utility.GetAdditionalReferences(includeNodaTime));

		csTest.TestState.AdditionalReferences
			.AddRange(additionalReferences);

		return csTest;
	}

	public sealed class CSharpGeneratorAnalyzerTest<TAnalyzer, TVerifier> : CSharpAnalyzerTest<TAnalyzer, TVerifier>
		where TAnalyzer : DiagnosticAnalyzer, new()
		where TVerifier : IVerifier, new()
	{
		protected override IEnumerable<Type> GetSourceGenerators() =>
			[typeof(ImmediateJobsGenerator), typeof(ImmediateHandlersGenerator)];
	}

	public static CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> WithDiagnostic<TAnalyzer>(
		this CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> analyzerTest,
		DiagnosticResult diagnosticResult
	)
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		analyzerTest.ExpectedDiagnostics.Add(diagnosticResult);
		return analyzerTest;
	}
}
