namespace Immediate.Jobs.Testing;

/// <summary>Indicates that a job harness assertion did not match captured durable work.</summary>
public sealed class JobTestAssertionException : Exception
{
	/// <summary>Creates an assertion exception without a message.</summary>
	public JobTestAssertionException()
	{
	}

	/// <summary>Creates an assertion exception with a message.</summary>
	/// <param name="message">The message that describes the failed assertion.</param>
	public JobTestAssertionException(string message)
		: base(message)
	{
	}

	/// <summary>Creates an assertion exception with a message and underlying cause.</summary>
	/// <param name="message">The message that describes the failed assertion.</param>
	/// <param name="innerException">The exception that caused the assertion to fail.</param>
	public JobTestAssertionException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
