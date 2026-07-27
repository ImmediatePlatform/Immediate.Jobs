using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NodaTimePackageRequiredAnalyzer : DiagnosticAnalyzer
{
	public static readonly DiagnosticDescriptor NodaTimePackageRequired =
		new(
			id: DiagnosticIds.IJOB0004NodaTimePackageRequired,
			title: "NodaTime integration package is required",
			messageFormat: "`NodaTime` package is referenced, but `Immediate.Jobs.NodaTime` is not",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "`Immediate.Jobs.NodaTime` is required to ensure proper serialization of `NodaTime` types.",
			customTags: [WellKnownDiagnosticTags.CompilationEnd]
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			NodaTimePackageRequired,
		]);

	public override void Initialize(AnalysisContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();

		context.RegisterCompilationAction(AnalyzeCompilation);
	}

	private void AnalyzeCompilation(CompilationAnalysisContext context)
	{
		context.CancellationToken.ThrowIfCancellationRequested();

		if (!context.Compilation.ReferencedAssemblyNames
				.Any(identity => identity.Name == "NodaTime"))
		{
			return;
		}

		if (context.Compilation.ReferencedAssemblyNames
				.Any(identity => identity.Name == "Immediate.Jobs.NodaTime"))
		{
			return;
		}

		context.ReportDiagnostic(
			Diagnostic.Create(
				NodaTimePackageRequired,
				location: null
			)
		);
	}
}
