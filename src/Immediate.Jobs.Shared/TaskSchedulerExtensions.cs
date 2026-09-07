using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Immediate.Jobs.Shared;

internal static class TaskSchedulerExtensions
{
	extension(TaskScheduler)
	{
		/// <summary>
		///	    A method to ensure that the remainder of the calling <see langword="async"/> method is run on the <see
		///	    cref="TaskScheduler.Default"/> threadpool, rather than whichever sync context is calling it.
		/// </summary>
		/// <remarks>
		///		Stolen liberally from <see href="https://github.com/dotnet/runtime/issues/130434"/>.
		/// </remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static TaskSchedulerYieldAwaitable Yield()
		{
			return default;
		}
	}

	[EditorBrowsable(EditorBrowsableState.Never)]
	public readonly struct TaskSchedulerYieldAwaitable
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Awaiter GetAwaiter()
		{
			return default;
		}

		[EditorBrowsable(EditorBrowsableState.Never)]
		public readonly struct Awaiter : ICriticalNotifyCompletion
		{
			private static readonly WaitCallback OnCompletedCallback = static state => Unsafe.As<Action>(state!)();

			public bool IsCompleted => TaskScheduler.Current == TaskScheduler.Default;

			public void GetResult() { }

			public void OnCompleted(Action continuation)
			{
				ThreadPool.QueueUserWorkItem(OnCompletedCallback, continuation);
			}

			public void UnsafeOnCompleted(Action continuation)
			{
				ThreadPool.UnsafeQueueUserWorkItem(OnCompletedCallback, continuation);
			}
		}
	}
}
