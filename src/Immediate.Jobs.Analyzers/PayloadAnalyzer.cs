using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PayloadAnalyzer : DiagnosticAnalyzer
{
	public static readonly DiagnosticDescriptor JobRequestIsNotSerializable =
		new(
			id: DiagnosticIds.IJOB0013JobRequestIsNotSerializable,
			title: "Job Request Type is not serializable",
			messageFormat: "Job `{0}` has request type `{1}`, which cannot be serialized to JSON (error: {2})",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Serialization to JSON is a requirement for persistence.",
			customTags: [WellKnownDiagnosticTags.NotConfigurable]
		);

	public static readonly DiagnosticDescriptor JobContextIsNotSerializable =
		new(
			id: DiagnosticIds.IJOB0014JobContextIsNotSerializable,
			title: "Job Context Type is not serializable",
			messageFormat: "Context Extractor `{0}` extracts type `{1}`, which cannot be serialized to JSON (error: {2})",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Serialization to JSON is a requirement for persistence.",
			customTags: [WellKnownDiagnosticTags.NotConfigurable]
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			JobRequestIsNotSerializable,
			JobContextIsNotSerializable,
		]);

	public override void Initialize(AnalysisContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		//context.EnableConcurrentExecution();

		context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType);
	}

	private static void AnalyzeSymbol(SymbolAnalysisContext context)
	{
		AnalyzeJobParameter(context);
		AnalyzeContextExtractor(context);
	}

	private static void AnalyzeJobParameter(SymbolAnalysisContext context)
	{
		var token = context.CancellationToken;
		token.ThrowIfCancellationRequested();

		if (context.Symbol is not INamedTypeSymbol namedTypeSymbol)
			return;

		if (namedTypeSymbol.GetAttributes().JobAttribute is null)
			return;

		if (namedTypeSymbol.GetValidHandleMethod() is not { } handleMethod)
			return;

		var parameterType = handleMethod.Parameters[0].Type;

		token.ThrowIfCancellationRequested();

		_ = PayloadValidation.CanSerializeToJson(
			parameterType,
			(error, location) => context.ReportDiagnostic(
				Diagnostic.Create(
					JobRequestIsNotSerializable,
					location,
					namedTypeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
					parameterType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
					error
				)
			)
		);
	}

	private static void AnalyzeContextExtractor(SymbolAnalysisContext context)
	{
		var token = context.CancellationToken;
		token.ThrowIfCancellationRequested();

		if (context.Symbol is not INamedTypeSymbol { ContextType: { } contextType })
			return;

		_ = PayloadValidation.CanSerializeToJson(
			contextType,
			(error, location) => context.ReportDiagnostic(
				Diagnostic.Create(
					JobContextIsNotSerializable,
					location,
					context.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
					contextType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
					error
				)
			)
		);
	}
}
