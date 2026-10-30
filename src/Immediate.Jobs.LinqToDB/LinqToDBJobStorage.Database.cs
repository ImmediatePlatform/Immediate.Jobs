using System.Linq.Expressions;
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

	private ITable<TTable> WithSchema<TTable>(ITable<TTable> table)
		where TTable : notnull => _schema is null ? table : table.SchemaName(_schema);

	private IQueryable<ImmediateJobEntity> VersionedJobs(DataConnection connection, IReadOnlyList<ImmediateJobEntity> jobs)
	{
		// A balanced predicate bounds expression depth while fencing each row independently.
		Expression<Func<ImmediateJobEntity, bool>> Match(int start, int count)
		{
			if (count == 0)
				return static _ => false;
			if (count == 1)
			{
				var job = jobs[start];
				return entity => entity.Id == job.Id && entity.ConcurrencyStamp == job.ConcurrencyStamp;
			}

			var left = Match(start, count / 2);
			var right = Match(start + count / 2, count - count / 2);
			return Expression.Lambda<Func<ImmediateJobEntity, bool>>(
				Expression.OrElse(left.Body, Expression.Invoke(right, left.Parameters)), left.Parameters);
		}

		return Jobs(connection).Where(Match(0, jobs.Count));
	}

	private async Task InsertManyAsync<TTable>(DataConnection connection, IEnumerable<TTable> entities, CancellationToken cancellationToken)
		where TTable : class
	{
		await connection.BulkCopyAsync(new BulkCopyOptions
		{
			SchemaName = _schema,
			BulkCopyType = BulkCopyType.MultipleRows,
			MaxBatchSize = 256,
		}, entities, cancellationToken);
	}

	private Task<int> InsertAsync<TTable>(DataConnection connection, TTable entity, CancellationToken cancellationToken)
		where TTable : notnull => connection.InsertAsync(entity, schemaName: _schema, token: cancellationToken);
}
