using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class QueueDefinitionAnalyzer : DiagnosticAnalyzer
{
	public static readonly DiagnosticDescriptor MissingQueueDefinition =
		new(
			id: DiagnosticIds.IJOB0009MissingQueueDefinition,
			title: "UsesQueue<T> references an invalid queue definition",
			messageFormat: "Job `{0}` uses queue type `{1}`, but that type is not marked `[QueueDefinition]`",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Using a queue without defining the queue prevents the job from being able to run.",
			customTags: [WellKnownDiagnosticTags.NotConfigurable]
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			MissingQueueDefinition,
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

		AnalyzeJob(context, namedTypeSymbol);
		AnalyzeQueue(context, namedTypeSymbol);
	}

	private static void AnalyzeJob(SymbolAnalysisContext context, INamedTypeSymbol namedTypeSymbol)
	{

		if (
			namedTypeSymbol.GetAttributes() is not
			{
				JobAttribute: { },
				UsesQueueAttribute: { AttributeClass.TypeArguments: [{ } queueType] } usesQueueAttribute
			}
		)
		{
			return;
		}

		if (queueType.GetAttributes() is { QueueDefinitionAttribute: { } })
			return;

		context.ReportDiagnostic(
			Diagnostic.Create(
				MissingQueueDefinition,
				usesQueueAttribute.Location,
				namedTypeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
				queueType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)
			)
		);
	}

	private static void AnalyzeQueue(SymbolAnalysisContext context, INamedTypeSymbol namedTypeSymbol)
	{
	}
}
