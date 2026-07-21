using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Scriban;
using Scriban.Runtime;

namespace Immediate.Jobs.Generators;

/// <summary>Generates typed schedulers, direct invokers, and job registrations.</summary>
[Generator]
public sealed class ImmediateJobsGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var jobs = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Immediate.Jobs.JobAttribute",
				predicate: static (node, _) => node is ClassDeclarationSyntax,
				transform: static (ctx, cancellationToken) =>
				{
					cancellationToken.ThrowIfCancellationRequested();
					return ctx.TargetSymbol is INamedTypeSymbol type
						&& JobDiscovery.TryCreateModel(type, ctx.SemanticModel.Compilation, out var job)
							? job
							: null;
				})
			.Where(static job => job is not null)
			.Select(static (job, _) => job!)
			.WithTrackingName("Jobs");

		// Assembly-level default behaviors are their own provider so that editing the [assembly: JobBehaviors(...)]
		// attribute invalidates and regenerates the jobs that depend on it, rather than being read inline.
		var assemblyBehaviors = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Immediate.Jobs.JobBehaviorsAttribute",
				predicate: static (node, _) => node is CompilationUnitSyntax,
				transform: static (ctx, cancellationToken) => JobDiscovery.ParseAssemblyBehaviors(ctx, cancellationToken))
			.SelectMany(static (behaviors, _) => behaviors)
			.Collect()
			.WithTrackingName("AssemblyBehaviors");

		var resolvedJobs = jobs
			.Combine(assemblyBehaviors)
			.Select(static (pair, _) => ResolveBehaviors(pair.Left, pair.Right))
			.WithTrackingName("ResolvedJobs");

		var collectedJobs = resolvedJobs.Collect().WithTrackingName("JobsCollected");
		var queues = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Immediate.Jobs.QueueDefinitionAttribute",
				predicate: static (node, _) => node is ClassDeclarationSyntax,
				transform: static (ctx, cancellationToken) =>
				{
					cancellationToken.ThrowIfCancellationRequested();
					return ctx.TargetSymbol is INamedTypeSymbol type ? JobDiscovery.CreateQueueModel(type) : null;
				})
			.Where(static queue => queue is not null)
			.Select(static (queue, _) => queue!)
			.Collect()
			.WithTrackingName("QueuesCollected");

		var jobTemplate = GetTemplate("Job");
		var registrationsTemplate = GetTemplate("ServiceCollectionExtensions");
		context.RegisterSourceOutput(resolvedJobs, (productionContext, model) =>
		{
			productionContext.CancellationToken.ThrowIfCancellationRequested();
			productionContext.AddSource(
				model.HintName,
				RenderJob(model, jobTemplate, productionContext.CancellationToken)
			);
		});
		context.RegisterSourceOutput(collectedJobs.Combine(queues), (productionContext, input) =>
		{
			var jobModels = input.Left;
			var queueModels = input.Right;
			// Two jobs resolving to the same name would collide at runtime; drop them from
			// generation and let the analyzer surface the duplicate as a diagnostic instead.
			var duplicateNames = jobModels
				.GroupBy(job => job.Name, StringComparer.Ordinal)
				.Where(group => group.Count() > 1)
				.Select(group => group.Key)
				.ToImmutableHashSet(StringComparer.Ordinal);

			var models = jobModels
				.Where(job => !duplicateNames.Contains(job.Name))
				.OrderBy(job => job.TypeName, StringComparer.Ordinal)
				.ToImmutableArray();

			if (!models.IsEmpty)
			{
				productionContext.CancellationToken.ThrowIfCancellationRequested();
				productionContext.AddSource(
					"IJOB.ServiceCollectionExtensions.g.cs",
					RenderRegistrations(models, queueModels, registrationsTemplate, productionContext.CancellationToken)
				);
			}
		});
	}

	// Jobs that don't declare their own behaviors inherit the assembly-level default set, resolved for the job's payload.
	private static JobModel ResolveBehaviors(JobModel job, ImmutableArray<JobBehaviorModel> assemblyBehaviors)
	{
		if (job.HasExplicitBehaviors)
			return job;

		var behaviors = assemblyBehaviors
			.Select(behavior => JobDiscovery.ResolveBehavior(behavior, job.PayloadTypeName, job.PayloadMatchName))
			.Where(name => name is not null)
			.Select(name => name!)
			.ToEquatableReadOnlyList();

		return job with { Behaviors = behaviors };
	}

	private static string RenderJob(JobModel job, Template template, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var model = new
		{
			job.Namespace,
			job.Accessibility,
			ClassName = Escape(job.ClassName),
			job.TypeName,
			job.PayloadTypeName,
			job.HasPayload,
			JobNameLiteral = Literal(job.Name),
			QueueNameLiteral = Literal(job.QueueName),
			QueuePriority = job.QueuePriority.ToString(CultureInfo.InvariantCulture),
			QueueConcurrency = job.QueueConcurrency.ToString(CultureInfo.InvariantCulture),
			CronLiteral = job.Cron is null ? null : Literal(job.Cron),
			TimeZoneLiteral = Literal(job.TimeZone),
			MaxAttempts = job.MaxAttempts.ToString(CultureInfo.InvariantCulture),
			TimeoutLiteral = job.Timeout is null ? null : Literal(job.Timeout),
			MaxConcurrency = job.MaxConcurrency.ToString(CultureInfo.InvariantCulture),
			OverlapPolicy = job.OverlapPolicy.ToString(CultureInfo.InvariantCulture),
			Backoff = job.Backoff.ToString(CultureInfo.InvariantCulture),
			BackoffBaseLiteral = Literal(job.BackoffBase),
			Behaviors = job.Behaviors.Select((behavior, index) => new
			{
				Index = index,
				NextIndex = index + 1,
				TypeName = behavior,
			}).ToArray(),
			Contexts = job.Contexts.Select((context, index) => new
			{
				Index = index,
				context.ExtractorTypeName,
				context.ContextTypeName,
				context.JsonPropertyName,
			}).ToArray(),
			job.Json,
			Version = ThisAssembly.InformationalVersion,
		};

		var source = Render(template, model);
		cancellationToken.ThrowIfCancellationRequested();
		return source;
	}

	private static string RenderRegistrations(
		ImmutableArray<JobModel> jobs,
		ImmutableArray<QueueModel> queues,
		Template template,
		CancellationToken cancellationToken
	)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var model = new
		{
			Queues = queues
				.Concat(jobs.Select(job => new QueueModel
				{
					Name = job.QueueName,
					Priority = job.QueuePriority,
					Concurrency = job.QueueConcurrency,
				}))
				.Where(static queue => queue.Name != "default")
				.Distinct()
				.OrderBy(static queue => queue.Name, StringComparer.Ordinal)
				.Select(queue => new
				{
					NameLiteral = Literal(queue.Name),
					Priority = queue.Priority.ToString(CultureInfo.InvariantCulture),
					Concurrency = queue.Concurrency.ToString(CultureInfo.InvariantCulture),
				})
				.ToArray(),
			Jobs = jobs.Select(job => new
			{
				job.TypeName,
				Contexts = job.Contexts.Select(context => new
				{
					context.ExtractorTypeName,
				}).ToArray(),
				Behaviors = job.Behaviors.Select(behavior => new
				{
					TypeName = behavior,
				}).ToArray(),
			}).ToArray(),
			Version = ThisAssembly.InformationalVersion,
		};

		var source = Render(template, model);
		cancellationToken.ThrowIfCancellationRequested();
		return source;
	}

	private static string Render(Template template, object model)
	{
		var globals = new ScriptObject(StringComparer.Ordinal);
		globals.Import(model);
		var context = new TemplateContext(StringComparer.Ordinal)
		{
			LoopLimit = 0,
		};
		context.PushGlobal(globals);
		return template.Render(context);
	}

	private static Template GetTemplate(string name)
	{
		using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
			$"Immediate.Jobs.Generators.Templates.{name}.sbntxt"
		);
		Debug.Assert(stream is not null);
		using var reader = new StreamReader(stream);
		var template = Template.Parse(reader.ReadToEnd());
		if (template.HasErrors)
			throw new InvalidOperationException(string.Join("\n", template.Messages));
		return template;
	}

	private static string Escape(string identifier) =>
		Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(identifier) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
			? "@" + identifier
			: identifier;

	private static string Literal(string value) =>
		Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
}
