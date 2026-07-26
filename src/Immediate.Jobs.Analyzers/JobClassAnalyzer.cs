using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JobClassAnalyzer : DiagnosticAnalyzer
{
	public static readonly DiagnosticDescriptor JobConfigurationInvalid =
		new(
			id: DiagnosticIds.IJOB0005JobConfigurationInvalid,
			title: "Job Configuration Invalid",
			messageFormat: "Job `{0}` has an invalid configuration: {1}",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Invalid configurations of jobs will prevent proper usage.",
			customTags: [WellKnownDiagnosticTags.NotConfigurable]
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			JobConfigurationInvalid,
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

		if (attributes.GetJobAttribute() is not { } jobAttribute)
			return;

		token.ThrowIfCancellationRequested();

		AnalyzeJobConfiguration(context, jobAttribute);
	}

	private static void AnalyzeJobConfiguration(SymbolAnalysisContext context, AttributeData jobAttribute)
	{
		var arguments = jobAttribute.NamedArguments;

		var maxAttempts = arguments.GetIntValue("MaxAttempts", 3);
		if (maxAttempts < 1)
		{
			ReportInvalidConfigurationDiagnostic(
				context,
				"`MaxAttempts` must be at least one"
			);
		}

		var maxConcurrency = arguments.GetIntValue("MaxConcurrency", 0);
		if (maxConcurrency < 0)
		{
			ReportInvalidConfigurationDiagnostic(
				context,
				"`MaxConcurrency` must not be negative"
			);
		}

		var backoff = arguments.GetArgumentValue("Backoff") switch
		{
			{ } av => av.GetEnumValue()?.Name,
			_ => "ExponentialJitter",
		};
		if (backoff is not { })
		{
			ReportInvalidConfigurationDiagnostic(
				context,
				"`Backoff` must be a defined enum value"
			);
		}

		var overlapPolicy = arguments.GetArgumentValue("OverlapPolicy") switch
		{
			{ } av => av.GetEnumValue()?.Name,
			_ => "Skip",
		};
		if (overlapPolicy is not { })
		{
			ReportInvalidConfigurationDiagnostic(
				context,
				"`OverlapPolicy` must be a defined enum value"
			);
		}

		var timeout = arguments.GetStringValue("Timeout");
		if (timeout is { })
		{
			if (!TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var timeoutValue))
			{
				ReportInvalidConfigurationDiagnostic(
					context,
					"`Timeout` is not a parseable `TimeSpan` value"
				);
			}

			else if (timeoutValue <= TimeSpan.Zero)
			{
				ReportInvalidConfigurationDiagnostic(
					context,
					"`Timeout` must not be negative"
				);
			}
		}

		var backoffBase = arguments.GetStringValue("BackoffBase") ?? "00:00:05";
		if (backoffBase is { })
		{
			if (!TimeSpan.TryParse(backoffBase, CultureInfo.InvariantCulture, out var backoffBaseValue))
			{
				ReportInvalidConfigurationDiagnostic(
					context,
					"`BackoffBase` is not a parseable `TimeSpan` value"
				);
			}

			else if (backoffBaseValue <= TimeSpan.Zero)
			{
				ReportInvalidConfigurationDiagnostic(
					context,
					"`BackoffBase` must not be negative"
				);
			}
		}

		static void ReportInvalidConfigurationDiagnostic(
			SymbolAnalysisContext context,
			string errorMessage
		)
		{
			context.ReportDiagnostic(
				Diagnostic.Create(
					JobConfigurationInvalid,
					context.Symbol.Locations.FirstOrDefault(),
					context.Symbol.Name,
					errorMessage
				)
			);
		}
	}
}
