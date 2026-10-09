using System.Data.Common;
using System.Text.Json;
using Immediate.Jobs.Shared.Apis;
using Immediate.Jobs.Shared.Storage;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	/// <inheritdoc />
	public async ValueTask MergeJobDefinitionsListAsync(JobDefinitionRegistration registration, CancellationToken cancellationToken = default)
	{
		MergeJobDefinitionsListAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		_ = JobDefinitionReconciliation.Create(registration, [], []);
		await EnsureDefinitionCatalogAsync(cancellationToken);
		await RetryConcurrencyAsync(async connection =>
		{
			var stamp = Guid.NewGuid();
			_ = await DefinitionCatalog(connection).Where(item => item.Id == 1)
				.Set(item => item.ConcurrencyStamp, stamp).UpdateAsync(cancellationToken);
			var definitions = await Definitions(connection)
				.ToDictionaryAsync(static item => item.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
			var schedules = await Recurring(connection)
				.ToDictionaryAsync(static item => item.Name, StringComparer.Ordinal, cancellationToken);
			var changes = JobDefinitionReconciliation.Create(registration,
				definitions.Values.Select(static item => JsonSerializer.Deserialize(item.Metadata, LinqToDBJsonSerializerContext.Default.JobDefinitionRecord)!).ToList(),
				schedules.Values.Select(ToRecord).ToList());
			foreach (var name in changes.RemovedDefinitionNames)
				_ = await Definitions(connection).Where(item => item.Name == name).DeleteAsync(cancellationToken);
			foreach (var definition in changes.Definitions)
			{
				var metadata = JsonSerializer.Serialize(definition, LinqToDBJsonSerializerContext.Default.JobDefinitionRecord);
				_ = definitions.ContainsKey(definition.Name)
					? await Definitions(connection).Where(item => item.Name == definition.Name)
						.Set(item => item.Metadata, metadata).UpdateAsync(cancellationToken)
					: await InsertAsync(connection, new ImmediateJobDefinitionMetadataEntity { Name = definition.Name, Metadata = metadata }, cancellationToken);
			}

			foreach (var name in changes.RemovedScheduleNames)
				_ = await Recurring(connection).Where(item => item.Name == name).DeleteAsync(cancellationToken);
			foreach (var schedule in changes.Schedules)
			{
				var entity = ToEntity(schedule);
				if (schedules.TryGetValue(schedule.Name, out var current))
				{
					if (!await UpdateRecurringAsync(connection, entity, current.ConcurrencyStamp, cancellationToken))
						throw new LostRaceException();
				}
				else
					_ = await InsertAsync(connection, entity, cancellationToken);
			}
		}, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<JobDefinitionRecord>> GetJobDefinitionsAsync(CancellationToken cancellationToken = default)
	{
		GetJobDefinitionsAsyncCalled();
		cancellationToken.ThrowIfCancellationRequested();
		await TaskScheduler.Yield();
		await using var scope = contextScope.GetScope(out var connection);
		var metadata = await Definitions(connection).Select(static item => item.Metadata).ToListAsync(cancellationToken);
		return [.. metadata.Select(static value => JsonSerializer.Deserialize(value, LinqToDBJsonSerializerContext.Default.JobDefinitionRecord)!)
			.OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)];
	}

	private async Task EnsureDefinitionCatalogAsync(CancellationToken cancellationToken)
	{
		await using var scope = contextScope.GetScope(out var connection);
		if (await DefinitionCatalog(connection).AnyAsync(item => item.Id == 1, cancellationToken))
			return;
		try
		{
			_ = await InsertAsync(connection, new ImmediateJobDefinitionCatalogEntity { Id = 1, ConcurrencyStamp = Guid.NewGuid() }, cancellationToken);
		}
		catch (DbException)
		{
			await using var verificationScope = contextScope.GetScope(out var verification);
			if (!await DefinitionCatalog(verification).AnyAsync(item => item.Id == 1, cancellationToken))
				throw;
		}
	}

	[LoggerMessage(
		EventId = LibraryEventIds.MergeJobDefinitionsListAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.MergeJobDefinitionsListAsyncCalled",
		Level = LogLevel.Debug,
		Message = "MergeJobDefinitionsListAsyncCalled"
	)]
	private partial void MergeJobDefinitionsListAsyncCalled();

	[LoggerMessage(
		EventId = LibraryEventIds.GetJobDefinitionsAsyncCalled,
		EventName = "Immediate.Jobs.LinqToDB.GetJobDefinitionsAsyncCalled",
		Level = LogLevel.Debug,
		Message = "GetJobDefinitionsAsyncCalled"
	)]
	private partial void GetJobDefinitionsAsyncCalled();
}
