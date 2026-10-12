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
			job.JobTags,
			QueueNameLiteral = job.QueueName.AsCSharpLiteral(),
			QueuePriority = job.QueuePriority.ToString(CultureInfo.InvariantCulture),
			QueueConcurrency = job.QueueConcurrency.ToString(CultureInfo.InvariantCulture),
			RecurringSchedulerInterface = job.Cron is null ? "IRecurringJobScheduler" : "IRecurringJobTrigger",
			CronLiteral = job.Cron?.AsCSharpLiteral(),
			TimeZoneLiteral = job.TimeZone.AsCSharpLiteral(),
			MaxAttempts = job.MaxAttempts.ToString(CultureInfo.InvariantCulture),
			TimeoutLiteral = DurationLiteral(job.Timeout),
			MaxConcurrency = job.MaxConcurrency.ToString(CultureInfo.InvariantCulture),
			SlidingWindowMax = job.SlidingWindowMax.ToString(CultureInfo.InvariantCulture),
			SlidingWindowPeriodLiteral = DurationLiteral(job.SlidingWindowPeriod),
			FixedWindowMax = job.FixedWindowMax.ToString(CultureInfo.InvariantCulture),
			FixedWindowPeriodLiteral = DurationLiteral(job.FixedWindowPeriod),
			job.OverlapPolicy,
			job.MisfireHandlingMode,
			job.Backoff,
			BackoffBaseLiteral = DurationLiteral(job.BackoffBase),
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

			Jobs = jobs
				.Where(job => !job.HasPayload)
				.Select(job => new
				{
					job.TypeName,
					JobNameLiteral = job.Name.AsCSharpLiteral(),
				})
				.ToList(),

			JobsByTag = jobs.GroupBy(job => job.HandlerTags, StringComparer.Ordinal),

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

	private static string? DurationLiteral(string? value)
	{
		if (value is null)
			return null;

		return TimeSpanSerializer.TryParse(value, out var duration)
			? duration.ToString("c", CultureInfo.InvariantCulture).AsCSharpLiteral()
			: null;
	}
}
