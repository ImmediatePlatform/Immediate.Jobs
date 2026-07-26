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

	public static readonly DiagnosticDescriptor CronJobCannotHaveParameters =
		new(
			id: DiagnosticIds.IJOB0006CronJobCannotHaveParameters,
			title: "Cron Job Parameter must be EmptyJobRequest",
			messageFormat: "Job `{0}` is marked as a Cron job, but has parameter type `{1}`; parameter type must be `EmptyJobRequest`",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Cron jobs cannot have parameters.",
			customTags: [WellKnownDiagnosticTags.NotConfigurable]
		);

	public static readonly DiagnosticDescriptor CronJobConfigurationInvalid =
		new(
			id: DiagnosticIds.IJOB0007CronJobConfigurationInvalid,
			title: "Cron Job Configuration Invalid",
			messageFormat: "Cron Job `{0}` has an invalid configuration: {1}",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "Invalid configurations of cron jobs will prevent proper usage.",
			customTags: [WellKnownDiagnosticTags.NotConfigurable]
		);

	public static readonly DiagnosticDescriptor JobNameInvalid =
		new(
			id: DiagnosticIds.IJOB0008JobNameInvalid,
			title: "Job Name Invalid",
			messageFormat: "Job `{0}` has an invalid name: {1}",
			category: "ImmediateJobs",
			defaultSeverity: DiagnosticSeverity.Error,
			isEnabledByDefault: true,
			description: "A job must have a name that can identify it in storage.",
			customTags: [WellKnownDiagnosticTags.NotConfigurable]
		);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(
		[
			JobConfigurationInvalid,
			CronJobCannotHaveParameters,
			CronJobConfigurationInvalid,
			JobNameInvalid,
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

		AnalyzeJobName(context, jobAttribute);
		AnalyzeJobConfiguration(context, jobAttribute);
		AnalyzeCronConfiguration(context, jobAttribute);
	}

	private static void AnalyzeJobName(SymbolAnalysisContext context, AttributeData jobAttribute)
	{
		if (jobAttribute.GetJobName(className: context.Symbol.Name).HasNameContent())
			return;

		var explicitName = jobAttribute.NamedArguments.GetStringValue("Name");

		context.ReportDiagnostic(
			Diagnostic.Create(
				JobNameInvalid,
				context.Symbol.Locations.FirstOrDefault(),
				context.Symbol.Name,
				explicitName is null
					? $"a name cannot be derived from the class name `{context.Symbol.Name}`; rename the class or set `Name`"
					: "`Name` must contain at least one letter or digit"
			)
		);
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

	private static void AnalyzeCronConfiguration(SymbolAnalysisContext context, AttributeData jobAttribute)
	{
		if (((INamedTypeSymbol)context.Symbol).GetValidHandleMethod() is not { } handleMethod)
			return;

		var parameterType = handleMethod.Parameters[0].Type;
		var hasPayload = !parameterType.IsEmptyJobRequest;

		var arguments = jobAttribute.NamedArguments;

		var cron = arguments.GetStringValue("Cron");
		var timeZone = arguments.GetStringValue("TimeZone") ?? "UTC";

		if (cron is not null)
		{
			if (hasPayload)
			{
				context.ReportDiagnostic(
					Diagnostic.Create(
						CronJobCannotHaveParameters,
						context.Symbol.Locations.FirstOrDefault(),
						context.Symbol.Name,
						parameterType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
					)
				);
			}

			if (!CronValidator.TryValidate(cron, out _))
			{
				context.ReportDiagnostic(
					Diagnostic.Create(
						CronJobConfigurationInvalid,
						context.Symbol.Locations.FirstOrDefault(),
						context.Symbol.Name,
						"Cron expression is invalid"
					)
				);
			}

			if (timeZone.IsWhiteSpace())
			{
				context.ReportDiagnostic(
					Diagnostic.Create(
						CronJobConfigurationInvalid,
						context.Symbol.Locations.FirstOrDefault(),
						context.Symbol.Name,
						"Cron time zone is invalid"
					)
				);
			}
		}
	}
}
