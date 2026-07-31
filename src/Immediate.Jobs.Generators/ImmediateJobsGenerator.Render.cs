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
			JobNameLiteral = Literal(job.Name),
			QueueNameLiteral = Literal(job.QueueName),
			QueuePriority = job.QueuePriority.ToString(CultureInfo.InvariantCulture),
			QueueConcurrency = job.QueueConcurrency.ToString(CultureInfo.InvariantCulture),
			RecurringSchedulerInterface = job.Cron is null ? "IRecurringJobScheduler" : "IRecurringJobTrigger",
			CronLiteral = job.Cron is null ? null : Literal(job.Cron),
			TimeZoneLiteral = Literal(job.TimeZone),
			MaxAttempts = job.MaxAttempts.ToString(CultureInfo.InvariantCulture),
			TimeoutLiteral = job.Timeout is null ? null : Literal(job.Timeout),
			MaxConcurrency = job.MaxConcurrency.ToString(CultureInfo.InvariantCulture),
			job.OverlapPolicy,
			job.Backoff,
			BackoffBaseLiteral = Literal(job.BackoffBase),
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

		// Two jobs resolving to the same name would collide at runtime; drop them from
		// generation and let the analyzer surface the duplicate as a diagnostic instead.
		var duplicateNames = jobs
			.GroupBy(job => job.Name, StringComparer.Ordinal)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key)
			.ToImmutableHashSet(StringComparer.Ordinal);

		var models = jobs
			.Where(job => !duplicateNames.Contains(job.Name))
			.OrderBy(job => job.TypeName, StringComparer.Ordinal)
			.ToImmutableArray();

		var model = new
		{
			assemblyDefaults.AssemblyName,
			assemblyDefaults.LanguageVersion,
			Namespace = @namespace,

			Queues = queues
				.Concat(models.Select(job => new QueueModel
				{
					Name = job.QueueName,
					Priority = job.QueuePriority,
					Concurrency = job.QueueConcurrency,
				}))
				.Where(static queue => !string.Equals(queue.Name, "default", StringComparison.Ordinal))
				.Distinct()
				.OrderBy(static queue => queue.Name, StringComparer.Ordinal)
				.Select(queue => new
				{
					NameLiteral = Literal(queue.Name),
					Priority = queue.Priority.ToString(CultureInfo.InvariantCulture),
					Concurrency = queue.Concurrency.ToString(CultureInfo.InvariantCulture),
				})
				.ToArray(),

			JobsByTag = models.GroupBy(job => job.Tags, StringComparer.Ordinal),
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

	private static string Literal(string value) =>
		Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
}
