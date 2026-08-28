namespace Immediate.Jobs.Shared;

/// <summary>
/// 	An opaque reference to a Job which is part of a Batch which is currently being built.
/// </summary>
public sealed class BatchJobHandle
{
	internal BatchJobHandle(Batch batch, JobHandle jobHandle)
	{
		Batch = batch;
		RawJobHandle = jobHandle;
	}

	/// <summary>
	/// 	The reference to an in-progress atomic batch buffer.
	/// </summary>
	public Batch Batch { get; }

	internal JobHandle RawJobHandle { get; }

	/// <summary>
	/// 	The reference to a durable job invocation.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	///		Thrown if the <see cref="Batch"/> is uncommitted.
	/// </exception>
	/// <remarks>
	///		This property is only valid if the containing <see cref="Batch"/> has been committed.
	/// </remarks>
	public JobHandle JobHandle =>
		Batch.IsCommitted switch
		{
			true => RawJobHandle,
			false => throw new InvalidOperationException("Cannot retrieve job ids from uncommitted batches."),
		};
}
