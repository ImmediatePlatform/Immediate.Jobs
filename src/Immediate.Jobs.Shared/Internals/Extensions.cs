using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Immediate.Jobs.Shared.Internals;

internal static class ArgumentExceptionExtensions
{
	extension(ArgumentException)
	{
		[DoesNotReturn]
		public static void Throw(string paramName, string message) =>
			throw new ArgumentException(message: message, paramName: paramName);
	}
}

internal static class ArgumentOutOfRangeExceptionExtensions
{
	extension(ArgumentOutOfRangeException)
	{
		[DoesNotReturn]
		public static void Throw(string paramName, string message) =>
			throw new ArgumentOutOfRangeException(message: message, paramName: paramName);
	}
}

internal static class ActivityExtensions
{
	extension(Activity? activity)
	{
		public void Deconstruct(out string? parent, out string? state) =>
			(parent, state) = (activity?.Id, activity?.TraceStateString);
	}
}
