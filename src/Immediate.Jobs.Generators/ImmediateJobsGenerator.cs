using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Immediate.Jobs.Generators;

/// <summary>Generates typed schedulers, direct invokers, and job registrations.</summary>
[Generator]
public sealed partial class ImmediateJobsGenerator : IIncrementalGenerator
{
	/// <inheritdoc />
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var assemblyDefaults = context.CompilationProvider
			.Select((compilation, _) => new AssemblyDefaults
			{
				LanguageVersion = (compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions)?.LanguageVersion
					?? LanguageVersion.LatestMajor,
			})
			.WithTrackingName("AssemblyDefaults");

		var jobs = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				JobDiscovery.JobAttributeName,
				predicate: static (node, _) => node is ClassDeclarationSyntax,
				transform: TransformJob)
			.WhereNotNull()
			.WithTrackingName("Jobs");

		var collectedJobs = jobs.Collect().WithTrackingName("JobsCollected");
		var queues = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				JobDiscovery.QueueDefinitionAttributeName,
				predicate: static (node, _) => node is ClassDeclarationSyntax,
				transform: TransformQueue)
			.WhereNotNull()
			.Collect()
			.WithTrackingName("QueuesCollected");

		var jobTemplate = GetTemplate("Job");
		var registrationsTemplate = GetTemplate("ServiceCollectionExtensions");
		context.RegisterSourceOutput(jobs, (productionContext, model) => RenderJob(productionContext, model, jobTemplate));
		context.RegisterSourceOutput(
			collectedJobs.Combine(queues).Combine(assemblyDefaults),
			(productionContext, input) => RenderRegistrations(
				productionContext,
				input.Left.Left,
				input.Left.Right,
				input.Right,
				registrationsTemplate
			)
		);
	}
}
