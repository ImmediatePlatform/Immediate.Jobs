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
			.Select((cp, _) => new AssemblyDefaults
			{
				AssemblyName = cp.GetAssemblyIdentifier(),
				LanguageVersion = (cp.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions)?.LanguageVersion ?? LanguageVersion.LatestMajor,
			})
			.WithTrackingName("AssemblyDefaults");

		var jobs = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Immediate.Jobs.Shared.JobAttribute",
				predicate: static (node, _) => node is ClassDeclarationSyntax,
				transform: TransformJob
			)
			.WhereNotNull()
			.WithTrackingName("Jobs");

		var jobTemplate = GetTemplate("Job");
		context.RegisterSourceOutput(
			jobs,
			(productionContext, model) => RenderJob(productionContext, model, jobTemplate)
		);

		var collectedJobs = jobs.Collect().WithTrackingName("JobsCollected");

		var queues = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Immediate.Jobs.Shared.QueueDefinitionAttribute",
				predicate: static (node, _) => node is ClassDeclarationSyntax,
				transform: TransformQueue
			)
			.WhereNotNull()
			.Collect()
			.WithTrackingName("QueuesCollected");

		var registrationsTemplate = GetTemplate("ServiceCollectionExtensions");
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

file static class Extensions
{
	public static string GetAssemblyIdentifier(this Compilation compilation)
	{
		if (compilation.Assembly.GetAttributes()
				.FirstOrDefault(a => a.AttributeClass.IsImmediateAssemblyIdentifierAttribute)
				is { ConstructorArguments: [{ Value: string { Length: >= 1 } identifier }] }
			&& identifier[0] != '@'
			&& SyntaxFacts.IsValidIdentifier(identifier))
		{
			return identifier;
		}

		return compilation.AssemblyName!
			.Replace(".", string.Empty, StringComparison.Ordinal)
			.Replace(" ", string.Empty, StringComparison.Ordinal)
			.Replace("-", string.Empty, StringComparison.Ordinal)
			.Trim();
	}

	public static IncrementalValuesProvider<T> WhereNotNull<T>(this IncrementalValuesProvider<T?> values)
		where T : class => values.Where(static value => value is not null)!;
}
