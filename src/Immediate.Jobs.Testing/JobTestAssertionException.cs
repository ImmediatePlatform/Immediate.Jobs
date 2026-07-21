namespace Immediate.Jobs.Testing;

/// <summary>Indicates that a job harness assertion did not match captured durable work.</summary>
public sealed class JobTestAssertionException : Exception
{
	/// <summary>Creates an assertion exception without a message.</summary>
	public JobTestAssertionException()
	{
	}

	/// <summary>Creates an assertion exception with a message.</summary>
	public JobTestAssertionException(string message)
		: base(message)
	{
	}

	/// <summary>Creates an assertion exception with a message and underlying cause.</summary>
	public JobTestAssertionException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
