using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingHandlerAttributeAnalyzer : DiagnosticAnalyzer
{
	public static readonly DiagnosticDescriptor MissingHandlerAttribute =
		new(
			id: DiagnosticIds.IJOB0001MissingHandlerAttribute,
			title: "[Handler] must be used",
			messageFormat: "Handler `{0}` must be marked with [Handler]",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "An endpoint registration can only be generated for an Immediate.Handlers handler."
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			MissingHandlerAttribute,
		]);

	public override void Initialize(AnalysisContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();

		context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType);
	}

	private static void AnalyzeSymbol(SymbolAnalysisContext context)
	{
		var token = context.CancellationToken;
		token.ThrowIfCancellationRequested();

		if (context.Symbol is not INamedTypeSymbol namedTypeSymbol)
			return;

		var attributes = namedTypeSymbol.GetAttributes();

		if (attributes.GetJobAttribute() is null)
			return;

		token.ThrowIfCancellationRequested();

		if (attributes.GetHandlerAttribute() is { })
			return;

		context.ReportDiagnostic(
			Diagnostic.Create(
				MissingHandlerAttribute,
				namedTypeSymbol.Locations[0],
				namedTypeSymbol.Name
			)
		);
	}
}
