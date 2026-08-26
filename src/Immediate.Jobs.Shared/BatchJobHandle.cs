namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An opaque reference to a Job which is part of a Batch which is currently being built.
/// </summary>
public sealed class BatchJobHandle
{
	internal BatchJobHandle(Batch batch, JobHandle jobId)
	{
		Batch = batch;
		RawJobId = jobId;
	}

	/// <summary>
	/// 	The reference to an in-progress atomic batch buffer.
	/// </summary>
	public Batch Batch { get; }

	internal JobHandle RawJobId { get; }

	/// <summary>
	/// 	The reference to a durable job invocation.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	///		Thrown if the <see cref="Batch"/> is uncommitted.
	/// </exception>
	/// <remarks>
	///		This property is only valid if the containing <see cref="Batch"/> has been committed.
	/// </remarks>
	public JobHandle JobId =>
		Batch.IsCommitted switch
		{
			true => RawJobId,
			false => throw new InvalidOperationException("Cannot retrieve job ids from uncommitted batches."),
		};
}
