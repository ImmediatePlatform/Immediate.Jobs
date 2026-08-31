using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Immediate.Jobs.EntityFrameworkCore;

internal static class TaskSchedulerExtensions
{
	extension(TaskScheduler)
	{
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

			public bool IsCompleted
			{
				[MethodImpl(MethodImplOptions.AggressiveInlining)]
				get => TaskScheduler.Current == TaskScheduler.Default;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void GetResult()
			{
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void OnCompleted(Action continuation)
			{
				ThreadPool.QueueUserWorkItem(OnCompletedCallback, continuation);
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public void UnsafeOnCompleted(Action continuation)
			{
				ThreadPool.UnsafeQueueUserWorkItem(OnCompletedCallback, continuation);
			}
		}
	}
}
