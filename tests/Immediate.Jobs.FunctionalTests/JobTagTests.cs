using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Interfaces;
using Immediate.Jobs.Shared.Internals;
using Immediate.Jobs.Shared.Storage;
using Immediate.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.FunctionalTests;

public sealed class JobTagTests
{
	[Theory]
	[InlineData(null, "ordinary,shared")]
	[InlineData(new string[0], "ordinary,shared")]
	[InlineData(new[] { "default" }, "ordinary,shared")]
	[InlineData(new[] { "email" }, "email,shared")]
	[InlineData(new[] { "email", "default", "email" }, "email,ordinary,shared")]
	[InlineData(new[] { "Email" }, "")]
	public async Task AcquisitionUsesEffectiveTagsWithoutClaimingExcludedJobs(string[]? serverTags, string expected)
	{
		var token = TestContext.Current.CancellationToken;
		var invoker = new RecordingInvoker();
		var definitions = new[]
		{
			Definition("ordinary", invoker),
			Definition("email", invoker) with { Tags = ["email"] },
			Definition("shared", invoker) with { Tags = ["email", "default"] },
		};
		await using var harness = new JobTestHarness(services =>
		{
			foreach (var definition in definitions)
				services.AddSingleton(definition);
		}, options => options.Tags = serverTags);
		var now = harness.TimeProvider.GetUtcNow();
		foreach (var definition in definitions)
		{
			await harness.Storage.EnqueueAsync(new()
			{
				JobHandle = JobHandle.FromString(definition.Name),
				JobName = definition.Name,
				QueueName = "default",
				Payload = "{}",
				State = JobState.Pending,
				CreatedAt = now,
				DueAt = now,
			}, token);
		}

		await harness.DrainAsync(token);
		var expectedNames = expected.Length == 0 ? [] : expected.Split(',');
		Assert.Equal(expectedNames, invoker.Names.Order(StringComparer.Ordinal));
		Assert.Equal(expectedNames, (await harness.Storage.GetJobDefinitionsAsync(token)).Select(static definition => definition.Name));
		foreach (var definition in definitions)
		{
			var job = await harness.GetJobAsync(JobHandle.FromString(definition.Name), token);
			var eligible = expectedNames.Contains(definition.Name, StringComparer.Ordinal);
			Assert.Equal(eligible ? JobState.Succeeded : JobState.Pending, job.State);
			Assert.Equal(eligible ? 1 : 0, job.Attempt);
		}
	}

	[Fact]
	public async Task RecurringMaterializationAndRestartRespectTheSameScope()
	{
		var token = TestContext.Current.CancellationToken;
		var invoker = new RecordingInvoker();
		var eligible = Definition("eligible", invoker) with { Tags = ["email"], Cron = "* * * * *" };
		var ignored = Definition("ignored", invoker) with { Cron = "* * * * *" };
		await using var harness = new JobTestHarness(services =>
		{
			services.AddSingleton(eligible);
			services.AddSingleton(ignored);
		}, options => options.Tags = ["email"]);
		var now = harness.TimeProvider.GetUtcNow();
		await harness.Storage.MergeJobDefinitionsListAsync(new()
		{
			Definitions = [new() { Name = ignored.Name, Cron = ignored.Cron }],
			RecurringSchedules = [new() { Name = ignored.Name, JobName = ignored.Name, QueueName = "default", Cron = ignored.Cron!, TimeZone = "UTC", IsCodeDefined = true, NextRunAt = now }],
		}, token);
		await harness.DrainAsync(token);
		await harness.AdvanceTimeAndDrainAsync(TimeSpan.FromMinutes(1), token);
		Assert.Equal(["eligible"], invoker.Names);
		harness.ResetScheduler();
		await harness.DrainAsync(token);
		Assert.Equal(["eligible"], invoker.Names);
		Assert.Equal(["eligible", "ignored"], (await harness.Storage.GetJobDefinitionsAsync(token)).Select(static definition => definition.Name));
	}

	[Fact]
	public async Task HeartbeatsReportTheEffectiveServerTags()
	{
		var token = TestContext.Current.CancellationToken;
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton<TimeProvider>(TimeProvider.System);
		services.AddImmediateJobsCore()
			.ConfigureWorkers(options => options.Tags = ["email", "default", "email"])
			.ConfigureStorage(builder => builder.UseStorage<StartupStorage>().UseDistributed());
		await using var provider = services.BuildServiceProvider();
		var scheduler = provider.GetRequiredService<JobSchedulingService>();
		await scheduler.StartAsync(token);
		var storage = provider.GetRequiredService<StartupStorage>();
		var heartbeat = await storage.Heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
		Assert.Equal(["email", "default"], heartbeat.Tags);
		await scheduler.StopAsync(token);
	}

	private static JobDefinition Definition(string name, IJobInvoker invoker) => new()
	{
		Name = name,
		Invoker = invoker,
		JobType = typeof(RecordingInvoker),
	};

	private sealed class RecordingInvoker : IJobInvoker
	{
		public List<string> Names { get; } = [];
		public ValueTask InvokeAsync(IServiceProvider scopedServices, JobExecution execution)
		{
			Names.Add(execution.Record.JobName);
			return ValueTask.CompletedTask;
		}
	}

	private sealed class StartupStorage(TimeProvider clock) : CapturingJobStorage(clock)
	{
		public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<JobServerSnapshot> Heartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
		{
			await base.MergeJobDefinitionsListAsync(registration, cancellationToken);
			Registered.TrySetResult();
		}
		public override async ValueTask HeartbeatAsync(JobServerSnapshot server, CancellationToken cancellationToken = default)
		{
			await base.HeartbeatAsync(server, cancellationToken);
			Heartbeat.TrySetResult(server);
		}
	}
}
