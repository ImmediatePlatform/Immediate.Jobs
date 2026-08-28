using System.ComponentModel;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.Shared.Internals;

/// <summary>
/// 	Per-attempt buffer used by generated schedulers during job execution.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class JobExecutionBuffer
{
	private readonly Lock _gate = new();
	private readonly List<JobContinuationAddition> _additions = [];
	private bool _sealed;

	internal void Add(JobContinuationAddition addition)
	{
		lock (_gate)
		{
			if (_sealed)
				throw new ImmediateJobException("The execution buffer is sealed and cannot accept additional continuations.");
			_additions.Add(addition);
		}
	}

	internal IReadOnlyList<JobContinuationAddition> SealAndSnapshot()
	{
		lock (_gate)
		{
			if (_sealed)
				throw new ImmediateJobException("The execution buffer has already been sealed.");
			_sealed = true;
			return _additions.AsReadOnly();
		}
	}
}
