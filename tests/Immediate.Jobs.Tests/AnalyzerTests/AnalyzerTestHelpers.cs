using System.Diagnostics.CodeAnalysis;
using Immediate.Handlers.Generators;
using Immediate.Jobs.Generators;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Immediate.Jobs.Tests.AnalyzerTests;

public static class AnalyzerTestHelpers
{
	public static CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> CreateAnalyzerTest<TAnalyzer>(
		[StringSyntax("c#-test")] string inputSource,
		bool includeNodaTime = false
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
			.AddRange(Utility.GetMetadataReferences(includeNodaTime));

		return csTest;
	}

	public sealed class CSharpGeneratorAnalyzerTest<TAnalyzer, TVerifier> : CSharpAnalyzerTest<TAnalyzer, TVerifier>
		where TAnalyzer : DiagnosticAnalyzer, new()
		where TVerifier : IVerifier, new()
	{
		protected override IEnumerable<Type> GetSourceGenerators() =>
			[typeof(ImmediateJobsGenerator), typeof(ImmediateHandlersGenerator)];
	}
}
