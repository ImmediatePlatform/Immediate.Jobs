namespace Immediate.Jobs.Shared;

/// <summary>Indicates an invalid Immediate.Jobs runtime operation or state.</summary>
public sealed class ImmediateJobException : Exception
{
	/// <summary>Creates an Immediate.Jobs exception without a message.</summary>
	public ImmediateJobException()
	{
	}

	/// <summary>Creates an Immediate.Jobs exception with a message.</summary>
	public ImmediateJobException(string message)
		: base(message)
	{
	}

	/// <summary>Creates an Immediate.Jobs exception with a message and underlying cause.</summary>
	public ImmediateJobException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
