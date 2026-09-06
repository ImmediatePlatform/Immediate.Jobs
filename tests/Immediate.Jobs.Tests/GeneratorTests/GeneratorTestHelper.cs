using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Immediate.Handlers.Generators;
using Immediate.Jobs.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Tests.GeneratorTests;

internal static class GeneratorTestHelper
{
	public static GeneratorDriverRunResult RunGenerator(
		[StringSyntax("c#")] string source,
		bool includeNodaTime = false,
		params ReadOnlySpan<string> skippedSteps
	)
	{
		var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

		var syntaxTree = CSharpSyntaxTree.ParseText(
			source,
			options,
			cancellationToken: TestContext.Current.CancellationToken
		);

		var compilation = CSharpCompilation.Create(
			assemblyName: "Tests",
			syntaxTrees: [syntaxTree],
			references:
			[
				..Utility.NetCoreAssemblies,
				..Utility.GetAdditionalReferences(includeNodaTime: includeNodaTime),
			],
			options: new(
				outputKind: OutputKind.DynamicallyLinkedLibrary,
				nullableContextOptions: NullableContextOptions.Enable,
				specificDiagnosticOptions:
				[
					KeyValuePair.Create("CS1701", ReportDiagnostic.Suppress),
					KeyValuePair.Create("CS1998", ReportDiagnostic.Suppress),
				]
			)
		);

		GeneratorDriver driver = CSharpGeneratorDriver.Create(
			generators:
			[
				new ImmediateHandlersGenerator().AsSourceGenerator(),
				new ImmediateJobsGenerator().AsSourceGenerator(),
			],
			parseOptions: options,
			optionsProvider: new OptionsProvider(),
			driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true)
		);

		driver = RunGenerator(driver, compilation);
		var result = driver.GetRunResult();

		VerifyIncrementality(driver, compilation, options, skippedSteps);

		return result;
	}

	private static GeneratorDriver RunGenerator(
		GeneratorDriver driver,
		Compilation compilation
	)
	{
		driver = driver
			.RunGeneratorsAndUpdateCompilation(
				compilation,
				out var outputCompilation,
				out var diagnostics,
				TestContext.Current.CancellationToken
			);

		Assert.Empty(
			outputCompilation
				.GetDiagnostics(TestContext.Current.CancellationToken)
				.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
		);

		Assert.Empty(diagnostics);
		return driver;
	}

	private static void VerifyIncrementality(
		GeneratorDriver driver,
		Compilation compilation,
		CSharpParseOptions options,
		ReadOnlySpan<string> skippedSteps
	)
	{
		var clone = compilation.Clone().AddSyntaxTrees(
			CSharpSyntaxTree.ParseText(
				"// dummy",
				options,
				cancellationToken: TestContext.Current.CancellationToken
			)
		);

		driver = RunGenerator(driver, clone);

		if (
			driver.GetRunResult() is not
			{
				Results:
				[
				_,
				{
					TrackedOutputSteps: { } outputSteps,
					TrackedSteps: { } trackedSteps,
				},
				],
			}
		)
		{
			Assert.Fail("Unable to verify incrementality.");
			return;
		}

		foreach (var (_, step) in outputSteps)
			AssertSteps(step);

		foreach (var step in TrackedSteps)
		{
			if (skippedSteps.Contains(step))
			{
				if (trackedSteps.ContainsKey(step))
					Assert.Fail($"Step `{step}` should have been skipped, but is present.");
			}
			else
			{
				if (!trackedSteps.TryGetValue(step, out var outputs))
					Assert.Fail($"Step `{step}` expected, but is missing.");

				AssertSteps(outputs);
			}
		}
	}

	private static ReadOnlySpan<string> TrackedSteps =>
		new string[]
		{
			"AssemblyDefaults",
			"RootNamespace",
			"Jobs",
			"JobsCollected",
		};

	private static void AssertSteps(
		ImmutableArray<IncrementalGeneratorRunStep> steps
	)
	{
		var outputs = steps.SelectMany(o => o.Outputs);

		Assert.All(outputs, o => Assert.True(o.Reason is IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached));
	}

	private sealed class OptionsProvider : AnalyzerConfigOptionsProvider
	{
		private static readonly AnalyzerConfigOptions Options =
			new DictionaryAnalyzerOptions(
				new(StringComparer.OrdinalIgnoreCase)
				{
					["build_property.rootnamespace"] = "Immediate.Jobs.Testing",
				}
			);

		public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options;
		public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options;
		public override AnalyzerConfigOptions GlobalOptions => Options;
	}

	private sealed class DictionaryAnalyzerOptions(Dictionary<string, string> properties) : AnalyzerConfigOptions
	{
		public override bool TryGetValue(string key, out string value)
			=> properties.TryGetValue(key, out value!);
	}
}
