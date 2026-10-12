using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JobDefaultsAnalyzer : DiagnosticAnalyzer
{
	public static readonly DiagnosticDescriptor InvalidJobDefaults =
		new(
			id: DiagnosticIds.IJOB0017JobDefaultsInvalid,
			title: "Job Defaults Invalid",
			messageFormat: "Invalid job defaults: {0}",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			customTags: [WellKnownDiagnosticTags.NotConfigurable]
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create([InvalidJobDefaults]);

	public override void Initialize(AnalysisContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();

		context.RegisterOperationAction(AnalyzeDefaults, OperationKind.Attribute);
	}

	private static void AnalyzeDefaults(OperationAnalysisContext context)
	{
		context.CancellationToken.ThrowIfCancellationRequested();

		if (context.Operation is not IAttributeOperation { Operation: IObjectCreationOperation { Type.IsJobDefaultsAttribute: true } })
			return;

		var syntax = context.Operation.Syntax;

		var attribute = context.Compilation.Assembly.GetAttributes()
			.FirstOrDefault(
				attribute =>
					attribute.ApplicationSyntaxReference is { } reference
					&& reference.SyntaxTree == syntax.SyntaxTree
					&& reference.Span == syntax.Span
			);

		if (attribute is null)
			return;

		foreach (var error in attribute.GetJobDefaultsErrors())
			context.ReportDiagnostic(Diagnostic.Create(InvalidJobDefaults, syntax.GetLocation(), error));
	}
}
