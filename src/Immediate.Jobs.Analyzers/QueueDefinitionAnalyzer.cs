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

	public static readonly DiagnosticDescriptor JobCannotBeQueueDefinition =
		new(
			id: DiagnosticIds.IJOB0010JobCannotBeQueueDefinition,
			title: "Applying both `Job` and `QueueDefinition` on the same class is invalid",
			messageFormat: "Job `{0}` is also marked as `[QueueDefinition]`",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Warning,
			isEnabledByDefault: true,
			description: "Marking a single class as both a `Job` and a `QueueDefinition` is probably a mis-application."
		);

	public static readonly DiagnosticDescriptor QueueConfigurationInvalid =
		new(
			id: DiagnosticIds.IJOB0011QueueConfigurationInvalid,
			title: "Queue Configuration Invalid",
			messageFormat: "Queue `{0}` has an invalid configuration: {1}",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Invalid configurations of queues will prevent proper usage.",
			customTags: [WellKnownDiagnosticTags.NotConfigurable]
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			MissingQueueDefinition,
			JobCannotBeQueueDefinition,
			QueueConfigurationInvalid,
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
		var attributes = namedTypeSymbol.GetAttributes();

		if (attributes.QueueDefinitionAttribute is not { } queueDefinitionAttribute)
			return;

		if (attributes.JobAttribute is { } jobAttribute)
		{
			context.ReportDiagnostic(
				Diagnostic.Create(
					JobCannotBeQueueDefinition,
					queueDefinitionAttribute.Location,
					namedTypeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)
				)
			);

			context.ReportDiagnostic(
				Diagnostic.Create(
					JobCannotBeQueueDefinition,
					jobAttribute.Location,
					namedTypeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)
				)
			);
		}

		var queueName = queueDefinitionAttribute.GetQueueName(namedTypeSymbol.Name);
		if (string.IsNullOrWhiteSpace(queueName))
		{
			context.ReportDiagnostic(
				Diagnostic.Create(
					QueueConfigurationInvalid,
					queueDefinitionAttribute.Location,
					namedTypeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
					"Name cannot be empty."
				)
			);
		}
		else if (string.Equals(queueName, "default", StringComparison.Ordinal))
		{
			context.ReportDiagnostic(
				Diagnostic.Create(
					QueueConfigurationInvalid,
					queueDefinitionAttribute.Location,
					namedTypeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
					"Name 'default' is reserved"
				)
			);
		}

		var concurrency = queueDefinitionAttribute.NamedArguments.GetIntValue("Concurrency", 0);
		if (concurrency < 0)
		{
			context.ReportDiagnostic(
				Diagnostic.Create(
					QueueConfigurationInvalid,
					queueDefinitionAttribute.Location,
					namedTypeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
					"Concurrency cannot be negative"
				)
			);
		}
	}
}
