using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Immediate.Jobs.Analyzers;

/// <summary>Validates declarations marked with <c>Immediate.Jobs.Shared.JobAttribute</c>.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ImmediateJobsAnalyzer : DiagnosticAnalyzer
{
	/// <inheritdoc />
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
		ImmutableArray.Create(
			DiagnosticDescriptors.InvalidCron,
			DiagnosticDescriptors.UnsupportedPayload,
			DiagnosticDescriptors.InvalidMethodSignature,
			DiagnosticDescriptors.InvalidConfiguration,
			DiagnosticDescriptors.CronPayload,
			DiagnosticDescriptors.InvalidQueueConfiguration,
			DiagnosticDescriptors.InvalidQueueTarget,
			DiagnosticDescriptors.InvalidContextExtractor,
			DiagnosticDescriptors.UnsupportedContext
		);

	/// <inheritdoc />
	public override void Initialize(AnalysisContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();
		context.RegisterCompilationAction(AnalyzeCompilation);
	}

	private static void AnalyzeCompilation(CompilationAnalysisContext context)
	{
		AnalyzeQueues(context);
		var jobs = JobDiscovery.FindJobs(context.Compilation, context.CancellationToken);
		var groups = jobs.GroupBy(JobDiscovery.GetName, StringComparer.Ordinal);

		var hasNodaTimeIntegration = context.Compilation.ReferencedAssemblyNames
			.Any(identity => identity.Name == "Immediate.Jobs.NodaTime");
		foreach (var job in jobs)
		{
			context.CancellationToken.ThrowIfCancellationRequested();
			var location = GetAttributeLocation(job);
			var contextUses = JobDiscovery.GetJobContextUses(job);
			foreach (var contextUse in contextUses)
			{
				var contextLocation = contextUse.AppliedAttribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? location;
				if (contextUse.ContextType is null)
				{
					context.ReportDiagnostic(Diagnostic.Create(
						DiagnosticDescriptors.InvalidContextExtractor,
						contextLocation,
						contextUse.ExtractorType.ToDisplayString()
					));
					continue;
				}

				var contextProblem = PayloadValidation.FindProblem(contextUse.ContextType, context.Compilation);
				if (contextProblem is not null)
				{
					context.ReportDiagnostic(Diagnostic.Create(
						DiagnosticDescriptors.UnsupportedContext,
						contextLocation,
						contextUse.ContextType.ToDisplayString(),
						contextProblem
					));
				}
			}

			if (JobDiscovery.GetUsesQueueAttribute(job) is { AttributeClass.TypeArguments: [INamedTypeSymbol queueType] } usesQueue &&
				JobDiscovery.GetQueueDefinitionAttribute(queueType) is null)
			{
				context.ReportDiagnostic(Diagnostic.Create(
					DiagnosticDescriptors.InvalidQueueTarget,
					usesQueue.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? location,
					queueType.ToDisplayString()
				));
				continue;
			}

			var methods = job.GetMembers("HandleAsync").OfType<IMethodSymbol>()
				.Where(method => !method.IsImplicitlyDeclared)
				.ToArray();
			if (methods.Length != 1 || !JobDiscovery.IsValidMethod(methods[0], out var payloadType, out var hasPayload))
			{
				context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidMethodSignature, location, job.Name));
				continue;
			}

			var attribute = JobDiscovery.GetJobAttribute(job)!;
			var cron = JobDiscovery.GetNamedString(attribute, "Cron");
			if (cron is not null && !CronValidator.TryValidate(cron, out var cronError))
				context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidCron, location, cron, cronError));
			if (cron is not null && hasPayload)
				context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CronPayload, location, job.Name));

			var timeZone = JobDiscovery.GetNamedString(attribute, "TimeZone");
			if (timeZone is not null && !JobDiscovery.IsValidTimeZone(timeZone))
				context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidCron, location, timeZone, "time zone must not be empty"));
			var configurationProblem = JobDiscovery.FindConfigurationProblem(attribute);
			if (configurationProblem is not null)
				context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidConfiguration, location, job.Name, configurationProblem));

			if (!hasPayload)
				continue;

			var problem = PayloadValidation.FindProblem(payloadType!, context.Compilation);
			if (problem is not null)
			{
				context.ReportDiagnostic(Diagnostic.Create(
					DiagnosticDescriptors.UnsupportedPayload,
					methods[0].Parameters[0].Locations.FirstOrDefault() ?? location,
					payloadType!.ToDisplayString(),
					problem
				));
			}
		}
	}

	private static void AnalyzeQueues(CompilationAnalysisContext context)
	{
		var queues = JobDiscovery.FindQueueDefinitions(context.Compilation, context.CancellationToken);
		foreach (var queue in queues)
		{
			var attribute = JobDiscovery.GetQueueDefinitionAttribute(queue)!;
			var name = JobDiscovery.GetQueueName(queue);
			string? problem = null;
			if (string.IsNullOrWhiteSpace(name))
				problem = "Name cannot be empty";
			else if (name == "default")
				problem = "Name 'default' is reserved";
			else if (JobDiscovery.GetNamedInt(attribute, "Concurrency", 0) < 0)
				problem = "Concurrency cannot be negative";

			if (problem is not null)
			{
				context.ReportDiagnostic(Diagnostic.Create(
					DiagnosticDescriptors.InvalidQueueConfiguration,
					attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? queue.Locations.FirstOrDefault(),
					queue.Name,
					problem
				));
			}
		}
	}

	private static Location GetAttributeLocation(INamedTypeSymbol symbol)
	{
		var attribute = JobDiscovery.GetJobAttribute(symbol);
		return attribute?.ApplicationSyntaxReference?.GetSyntax().GetLocation()
			?? symbol.Locations.FirstOrDefault()
			?? Location.None;
	}
}
