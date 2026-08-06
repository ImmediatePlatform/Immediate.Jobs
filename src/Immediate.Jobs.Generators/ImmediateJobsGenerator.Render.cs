using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Scriban;

namespace Immediate.Jobs.Generators;

public sealed partial class ImmediateJobsGenerator
{
	private static void RenderJob(SourceProductionContext context, JobModel job, Template template)
	{
		var cancellationToken = context.CancellationToken;
		cancellationToken.ThrowIfCancellationRequested();
		var model = new
		{
			job.Namespace,
			job.ClassName,
			job.TypeName,
			job.PayloadTypeName,
			job.HasPayload,
			job.HasJobDetails,
			JobNameLiteral = job.Name.AsCSharpLiteral(),
			QueueNameLiteral = job.QueueName.AsCSharpLiteral(),
			QueuePriority = job.QueuePriority.ToString(CultureInfo.InvariantCulture),
			QueueConcurrency = job.QueueConcurrency.ToString(CultureInfo.InvariantCulture),
			RecurringSchedulerInterface = job.Cron is null ? "IRecurringJobScheduler" : "IRecurringJobTrigger",
			CronLiteral = job.Cron?.AsCSharpLiteral(),
			TimeZoneLiteral = job.TimeZone.AsCSharpLiteral(),
			MaxAttempts = job.MaxAttempts.ToString(CultureInfo.InvariantCulture),
			TimeoutLiteral = job.Timeout?.AsCSharpLiteral(),
			MaxConcurrency = job.MaxConcurrency.ToString(CultureInfo.InvariantCulture),
			job.OverlapPolicy,
			job.Backoff,
			BackoffBaseLiteral = job.BackoffBase.AsCSharpLiteral(),
			job.Contexts,
			job.Json,
			Version = ThisAssembly.InformationalVersion,
		};

		var source = template.Render(model);

		cancellationToken.ThrowIfCancellationRequested();
		context.AddSource($"IJ.{job.Namespace}.{job.ClassName}.g.cs", source);
	}

	private static void RenderRegistrations(
		SourceProductionContext context,
		ImmutableArray<JobModel> jobs,
		ImmutableArray<QueueModel> queues,
		AssemblyDefaults assemblyDefaults,
		string @namespace,
		Template template
	)
	{
		var cancellationToken = context.CancellationToken;
		cancellationToken.ThrowIfCancellationRequested();

		var model = new
		{
			assemblyDefaults.AssemblyName,
			assemblyDefaults.LanguageVersion,
			Namespace = @namespace,

			Queues = queues
				.Where(static queue => !string.Equals(queue.Name, "default", StringComparison.Ordinal))
				.OrderBy(static queue => queue.Name, StringComparer.Ordinal)
				.Select(queue => new
				{
					NameLiteral = queue.Name.AsCSharpLiteral(),
					Priority = queue.Priority.ToString(CultureInfo.InvariantCulture),
					Concurrency = queue.Concurrency.ToString(CultureInfo.InvariantCulture),
				})
				.ToList(),

			Jobs = jobs
				.Where(job => !job.HasPayload)
				.Select(job => new
				{
					job.TypeName,
					JobNameLiteral = job.Name.AsCSharpLiteral(),
				})
				.ToList(),

			JobsByTag = jobs.GroupBy(job => job.Tags, StringComparer.Ordinal),

			Version = ThisAssembly.InformationalVersion,
		};

		var source = template.Render(model);

		cancellationToken.ThrowIfCancellationRequested();
		context.AddSource("IJ.ServiceCollectionExtensions.g.cs", source);
	}

	private static Template GetTemplate(string name)
	{
		using var stream = Assembly
			.GetExecutingAssembly()
			.GetManifestResourceStream(
				$"Immediate.Jobs.Generators.Templates.{name}.sbntxt"
			);

		Debug.Assert(stream is not null);

		using var reader = new StreamReader(stream);
		return Template.Parse(reader.ReadToEnd());
	}
}
