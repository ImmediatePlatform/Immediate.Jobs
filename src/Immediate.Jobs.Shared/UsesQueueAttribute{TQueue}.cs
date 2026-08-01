namespace Immediate.Jobs.Shared;

/// <summary>
/// 	Assigns a generated job to a strongly typed queue.
/// </summary>
/// <typeparam name="TQueue">
/// 	A type marked with <see cref="QueueDefinitionAttribute"/>.
/// </typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UsesQueueAttribute<TQueue> : Attribute;
