using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Immediate.Jobs.Testing;

internal static class ArgumentExceptionExtensions
{
	extension(ArgumentException)
	{
		[MethodImpl(MethodImplOptions.NoInlining)]
		[DoesNotReturn]
		public static void Throw(string paramName, string message) =>
			throw new ArgumentException(message: message, paramName: paramName);
	}
}

internal static class ArgumentOutOfRangeExceptionExtensions
{
	extension(ArgumentOutOfRangeException)
	{
		[MethodImpl(MethodImplOptions.NoInlining)]
		[DoesNotReturn]
		public static void Throw(string paramName, string message) =>
			throw new ArgumentOutOfRangeException(message: message, paramName: paramName);
	}
}
