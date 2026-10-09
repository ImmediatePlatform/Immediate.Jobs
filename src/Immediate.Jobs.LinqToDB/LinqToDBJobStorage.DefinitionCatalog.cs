using System.Data;
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
		JobDefinitionReconciliation.Create(registration, [], []);
		await RetryConcurrencyAsync(async connection =>
		{
			var definitions = await Definitions(connection)
				.ToDictionaryAsync(static item => item.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
			var schedules = await Recurring(connection)
				.ToDictionaryAsync(static item => item.Name, StringComparer.Ordinal, cancellationToken);
			var changes = JobDefinitionReconciliation.Create(registration,
				definitions.Values.Select(static item => JsonSerializer.Deserialize(item.Metadata, LinqToDBJsonSerializerContext.Default.JobDefinitionRecord)!).ToList(),
				schedules.Values.Select(ToRecord).ToList());
			await MergeDefinitionMetadataAsync(connection, changes, cancellationToken);
			await MergeRecurringDefinitionsAsync(connection, changes, cancellationToken);
		}, cancellationToken, isolationLevel: IsolationLevel.Serializable);
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

	private async Task MergeDefinitionMetadataAsync(
		DataConnection connection,
		JobDefinitionReconciliation changes,
		CancellationToken cancellationToken
	)
	{
		var source = changes.Definitions.Select(static definition => new
		{
			definition.Name,
			Metadata = JsonSerializer.Serialize(definition, LinqToDBJsonSerializerContext.Default.JobDefinitionRecord),
			Remove = false,
		}).Concat(changes.RemovedDefinitionNames.Select(static name => new { Name = name, Metadata = "", Remove = true })).ToList();
		if (source.Count == 0)
			return;

		// SQLite has no SQL MERGE. Keep its upserts and deletes in the same serializable transaction.
		if (connection.DataProvider.Name.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
		{
			await Definitions(connection).Where(item => item.Name.In(changes.RemovedDefinitionNames)).DeleteAsync(cancellationToken);
			foreach (var item in source.Where(static item => !item.Remove))
				await connection.InsertOrReplaceAsync(
					new ImmediateJobDefinitionMetadataEntity { Name = item.Name, Metadata = item.Metadata },
					schemaName: _schema,
					token: cancellationToken
				);
			return;
		}

		await Definitions(connection).Merge()
			.Using(source)
			.On(static (target, item) => target.Name == item.Name)
			.DeleteWhenMatchedAnd(static (target, item) => item.Remove)
			.UpdateWhenMatched(static (target, item) => new ImmediateJobDefinitionMetadataEntity { Metadata = item.Metadata })
			.InsertWhenNotMatchedAnd(static item => !item.Remove,
				static item => new ImmediateJobDefinitionMetadataEntity { Name = item.Name, Metadata = item.Metadata })
			.MergeAsync(cancellationToken);
	}

	private async Task MergeRecurringDefinitionsAsync(
		DataConnection connection,
		JobDefinitionReconciliation changes,
		CancellationToken cancellationToken
	)
	{
		// Only code-defined schedules are upserted. Non-code-defined source rows mark obsolete names for deletion.
		var source = changes.Schedules.Select(ToEntity)
			.Concat(changes.RemovedScheduleNames.Select(static name => new ImmediateRecurringJobEntity { Name = name }))
			.ToList();
		if (source.Count == 0)
			return;

		if (connection.DataProvider.Name.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
		{
			await Recurring(connection).Where(item => item.Name.In(changes.RemovedScheduleNames)).DeleteAsync(cancellationToken);
			foreach (var item in source.Where(static item => item.IsCodeDefined))
				await connection.InsertOrReplaceAsync(item, schemaName: _schema, token: cancellationToken);
			return;
		}

		await Recurring(connection).Merge()
			.Using(source)
			.OnTargetKey()
			.DeleteWhenMatchedAnd(static (target, item) => !item.IsCodeDefined)
			.UpdateWhenMatched()
			.InsertWhenNotMatchedAnd(static item => item.IsCodeDefined)
			.MergeAsync(cancellationToken);
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
