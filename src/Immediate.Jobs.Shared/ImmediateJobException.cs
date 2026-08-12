using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Indicates an invalid Immediate.Jobs runtime operation or state.
/// </summary>
public sealed class ImmediateJobException : Exception
{
	/// <summary>
	/// 	Creates an Immediate.Jobs exception without a message.
	/// </summary>
	public ImmediateJobException()
	{
	}

	/// <summary>
	/// 	Creates an Immediate.Jobs exception with a message.
	/// </summary>
	/// <param name="message">
	/// 	The error message that explains the reason for the exception.
	/// </param>
	public ImmediateJobException(string message)
		: base(message)
	{
	}

	/// <summary>
	/// 	Creates an Immediate.Jobs exception with a message and underlying cause.
	/// </summary>
	/// <param name="message">
	/// 	The error message that explains the reason for the exception.
	/// </param>
	/// <param name="innerException">
	/// 	The exception that caused the current exception.
	/// </param>
	public ImmediateJobException(string message, Exception innerException)
		: base(message, innerException)
	{
	}

	/// <summary>
	/// 	Throws an Immediate.Jobs exception with a message.
	/// </summary>
	/// <param name="message">
	/// 	The error message that explains the reason for the exception.
	/// </param>
	[MethodImpl(MethodImplOptions.NoInlining)]
	[DoesNotReturn]
	public static void Throw(string message) =>
		throw new ImmediateJobException(message);

	/// <summary>
	/// 	Throws an Immediate.Jobs exception with a message and underlying cause.
	/// </summary>
	/// <param name="message">
	/// 	The error message that explains the reason for the exception.
	/// </param>
	/// <param name="innerException">
	/// 	The exception that caused the current exception.
	/// </param>
	[MethodImpl(MethodImplOptions.NoInlining)]
	[DoesNotReturn]
	public static void Throw(string message, Exception innerException) =>
		throw new ImmediateJobException(message, innerException);
}
