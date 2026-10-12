using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JobDefaultsAnalyzer : DiagnosticAnalyzer
{
	public static readonly DiagnosticDescriptor InvalidJobDefaults = new(
		id: DiagnosticIds.IJOB0017JobDefaultsInvalid,
		title: "Job Defaults Invalid",
		messageFormat: "Invalid job defaults: {0}",
		category: "ImmediateJobs",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		customTags: [WellKnownDiagnosticTags.NotConfigurable, WellKnownDiagnosticTags.CompilationEnd]);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [InvalidJobDefaults];

	public override void Initialize(AnalysisContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();
		context.RegisterCompilationAction(AnalyzeDefaults);
	}

	private static void AnalyzeDefaults(CompilationAnalysisContext context)
	{
		foreach (var attribute in context.Compilation.Assembly.GetAttributes().Where(attribute => attribute.AttributeClass.IsJobDefaultsAttribute))
		{
			foreach (var error in attribute.NamedArguments.GetJobDefaultsErrors())
				context.ReportDiagnostic(Diagnostic.Create(InvalidJobDefaults, attribute.Location, error));
		}
	}
}
