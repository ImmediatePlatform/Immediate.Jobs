using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Immediate.Handlers.Shared;
using Immediate.Handlers.Generators;
using Immediate.Jobs.Analyzers;
using Immediate.Jobs.Generators;
using Immediate.Jobs.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.Tests.GeneratorTests;

internal static class GeneratorTestHelper
{
	internal static CSharpParseOptions ParseOptions { get; } = CSharpParseOptions.Default
		.WithLanguageVersion(LanguageVersion.Latest);

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

		var references = GetFrameworkReferences()
			.Concat(explicitAssemblies.Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)))
			.GroupBy(reference => reference.Display, StringComparer.Ordinal)
			.Select(group => group.First());

		return CSharpCompilation.Create(
			"GeneratorTests",
			sources.Select(source => CSharpSyntaxTree.ParseText(source.Source, ParseOptions, source.Path)),
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
				.WithNullableContextOptions(NullableContextOptions.Enable)
		);
	}

	private static ImmutableArray<PortableExecutableReference> GetFrameworkReferences() =>
#if NET8_0
		Basic.Reference.Assemblies.Net80.References.All;
#elif NET9_0
		Basic.Reference.Assemblies.Net90.References.All;
#elif NET10_0
		Basic.Reference.Assemblies.Net100.References.All;
#elif NET11_0
		Basic.Reference.Assemblies.Net110.References.All;
#else
#error Unsupported test target framework.
#endif

	public static GeneratorDriverRunResult RunGenerator(
		[StringSyntax("c#")] string source,
		bool includeNodaTime = false,
		params ReadOnlySpan<string> skippedSteps
	)
	{
		var syntaxTree = CSharpSyntaxTree.ParseText(
			source,
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
				]
			)
		);

		GeneratorDriver driver = CSharpGeneratorDriver.Create(
			generators: [new ImmediateHandlersGenerator().AsSourceGenerator(), new ImmediateJobsGenerator().AsSourceGenerator()],
			driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true)
		);

		driver = RunGenerator(driver, compilation);
		var result = driver.GetRunResult();

		VerifyIncrementality(driver, compilation, skippedSteps);

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
				.Where(diagnostic =>
					(diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning) &&
					diagnostic.Id != "CS1701"
				)
		);
		return driver;
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
		ReadOnlySpan<string> skippedSteps
	)
	{
		var clone = compilation.Clone().AddSyntaxTrees(
			CSharpSyntaxTree.ParseText(
				"// dummy",
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
			"Jobs",
			"JobsCollected",
			"QueuesCollected",
		};

	private static void AssertSteps(
		ImmutableArray<IncrementalGeneratorRunStep> steps
	)
	{
		var outputs = steps.SelectMany(o => o.Outputs);

		Assert.All(outputs, o => Assert.True(o.Reason is IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached));
	}

	public static SettingsTask VerifyJob(
		GeneratorDriverRunResult result,
		[CallerFilePath] string sourceFile = ""
	) => Verify(result, sourceFile: sourceFile)
		.IgnoreGeneratedResult(static generated =>
			Path.GetFileName(generated.HintName).StartsWith("IH.", StringComparison.Ordinal))
		.IgnoreGeneratedResult(static generated =>
			Path.GetFileName(generated.HintName) == "IJ.ServiceCollectionExtensions.g.cs");

	public static void AssertGeneratedTrees(
		GeneratorDriverRunResult result,
		string handlerHintName,
		string jobHintName
	) => Assert.Equal(
		[
			$"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/{handlerHintName}",
			"Immediate.Handlers.Generators/Immediate.Handlers.Generators.ImmediateHandlersGenerator/IH.ServiceCollectionExtensions.g.cs",
			$"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/{jobHintName}",
			"Immediate.Jobs.Generators/Immediate.Jobs.Generators.ImmediateJobsGenerator/IJ.ServiceCollectionExtensions.g.cs",
		],
		result.GeneratedTrees.Select(static tree => tree.FilePath.Replace('\\', '/'))
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
