using LinqToDB;
using LinqToDB.Data;

namespace Immediate.Jobs.LinqToDB;

internal sealed partial class LinqToDBJobStorage<T>
	where T : DataConnection
{
	private ITable<ImmediateJobEntity> Jobs(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobEntity>());

	private ITable<ImmediateJobExecutionEntity> Executions(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobExecutionEntity>());

	private ITable<ImmediateJobBatchEntity> Batches(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobBatchEntity>());

	private ITable<ImmediateFairQueueGroupEntity> FairQueueGroups(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateFairQueueGroupEntity>());

	private ITable<ImmediateJobContinuationEntity> Continuations(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobContinuationEntity>());

	private ITable<ImmediateRecurringJobEntity> Recurring(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateRecurringJobEntity>());

	private ITable<ImmediateJobServerEntity> Servers(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobServerEntity>());

	private ITable<ImmediateJobDefinitionMetadataEntity> Definitions(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobDefinitionMetadataEntity>());

	private ITable<ImmediateJobDefinitionCatalogEntity> DefinitionCatalog(DataConnection connection) =>
		WithSchema(connection.GetTable<ImmediateJobDefinitionCatalogEntity>());

	private ITable<TTable> WithSchema<TTable>(ITable<TTable> table)
		where TTable : notnull => _schema is null ? table : table.SchemaName(_schema);

	private Task<int> InsertAsync<TTable>(DataConnection connection, TTable entity, CancellationToken cancellationToken)
		where TTable : notnull => connection.InsertAsync(entity, schemaName: _schema, token: cancellationToken);
}
