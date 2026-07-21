using System.Collections.Immutable;
using Immediate.Handlers.Shared;
using Immediate.Handlers.Generators;
using Immediate.Jobs.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.Tests.GeneratorTests;

internal static class GeneratorTestHelper
{
	private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);

	public static CSharpCompilation CreateCompilation(string source)
		=> CreateCompilationCore([("Test0.cs", source)], includeNodaTime: false);

	public static CSharpCompilation CreateCompilation(params (string Path, string Source)[] sources)
		=> CreateCompilationCore(sources, includeNodaTime: false);

	private static CSharpCompilation CreateCompilationCore(
		(string Path, string Source)[] sources,
		bool includeNodaTime
	)
	{
		var explicitAssemblies = new List<string>
		{
			typeof(JobAttribute).Assembly.Location,
			typeof(HandlerAttribute).Assembly.Location,
			typeof(ServiceCollection).Assembly.Location,
			typeof(IServiceCollection).Assembly.Location,
		};
		if (includeNodaTime)
		{
			explicitAssemblies.Add(typeof(Immediate.Jobs.NodaTime.NodaTimeJobSerializer).Assembly.Location);
			explicitAssemblies.Add(typeof(global::NodaTime.Instant).Assembly.Location);
		}
		var references = Basic.Reference.Assemblies.Net80.References.All
			.Concat(explicitAssemblies.Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)))
			.GroupBy(reference => reference.Display, StringComparer.Ordinal)
			.Select(group => group.First());

		return CSharpCompilation.Create(
			"GeneratorTests",
			sources.Select(source => CSharpSyntaxTree.ParseText(source.Source, ParseOptions, source.Path)),
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
		);
	}

	private static readonly string[] TrackedSteps = ["Jobs", "AssemblyBehaviors", "ResolvedJobs", "JobsCollected"];

	public static GeneratorDriverRunResult RunGenerator(string source)
		=> RunGeneratorCore(source, includeNodaTime: false);

	public static GeneratorDriverRunResult RunGeneratorWithNodaTime(string source)
		=> RunGeneratorCore(source, includeNodaTime: true);

	private static GeneratorDriverRunResult RunGeneratorCore(string source, bool includeNodaTime)
	{
		var compilation = CreateCompilationCore([("Test0.cs", source)], includeNodaTime);
		var driver = CreateDriver();

		driver = RunAndAssert(driver, compilation);
		var result = driver.GetRunResult();
		Assert.Empty(result.Diagnostics);

		VerifyIncrementality(driver, compilation);

		return result;
	}

	public static GeneratorDriver CreateDriver() => CSharpGeneratorDriver.Create(
		[
			new ImmediateHandlersGenerator().AsSourceGenerator(),
			new ImmediateJobsGenerator().AsSourceGenerator(),
		],
		parseOptions: ParseOptions,
		driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true)
	);

	public static GeneratorDriver RunAndAssert(GeneratorDriver driver, Compilation compilation)
	{
		driver = driver.RunGeneratorsAndUpdateCompilation(
			compilation,
			out var output,
			out var diagnostics,
			TestContext.Current.CancellationToken
		);
		Assert.Empty(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
		Assert.Empty(
			output.GetDiagnostics(TestContext.Current.CancellationToken)
				.Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
		);
		return driver;
	}

	// Re-runs the generator against a compilation that differs only by an unrelated syntax tree.
	// Every tracked pipeline step and source-output step for the Jobs generator must report
	// Cached/Unchanged, proving the pipeline is genuinely incremental and not re-scanning.
	private static void VerifyIncrementality(GeneratorDriver driver, Compilation compilation)
	{
		var clone = compilation.Clone().AddSyntaxTrees(
			CSharpSyntaxTree.ParseText(
				"// dummy",
				ParseOptions,
				cancellationToken: TestContext.Current.CancellationToken
			)
		);

		driver = driver.RunGeneratorsAndUpdateCompilation(
			clone,
			out _,
			out _,
			TestContext.Current.CancellationToken
		);

		var jobsResult = driver.GetRunResult().Results
			.Single(result => result.TrackedSteps.ContainsKey("Jobs"));

		foreach (var (_, steps) in jobsResult.TrackedOutputSteps)
			AssertCached(steps);

		foreach (var name in TrackedSteps)
		{
			Assert.True(
				jobsResult.TrackedSteps.TryGetValue(name, out var steps),
				$"Step `{name}` expected, but is missing."
			);
			AssertCached(steps);
		}
	}

	private static void AssertCached(ImmutableArray<IncrementalGeneratorRunStep> steps) =>
		Assert.All(
			steps.SelectMany(step => step.Outputs),
			output => Assert.True(output.Reason is IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached)
		);

	public static async Task<ImmutableArray<Diagnostic>> RunAnalyzer(string source)
	{
		var compilation = CreateCompilation(source);
		Assert.Empty(
			compilation.GetDiagnostics(TestContext.Current.CancellationToken)
				.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
		);
		return await compilation.WithAnalyzers([new ImmediateJobsAnalyzer()]).GetAnalyzerDiagnosticsAsync();
	}
}
