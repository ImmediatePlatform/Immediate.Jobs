namespace Immediate.Jobs;

/// <summary>Defines a strongly typed job queue.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class QueueDefinitionAttribute : Attribute
{
	/// <summary>The stable persisted queue name, or <see langword="null"/> to derive it from the type name.</summary>
	public string? Name { get; init; }

	/// <summary>The dispatch priority. Larger values are dispatched first.</summary>
	public int Priority { get; init; }

	/// <summary>Maximum in-flight jobs on one scheduler node. Zero means unbounded.</summary>
	public int Concurrency { get; init; }
}

/// <summary>Assigns a generated job to a strongly typed queue.</summary>
/// <typeparam name="TQueue">A type marked with <see cref="QueueDefinitionAttribute"/>.</typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UsesQueueAttribute<TQueue> : Attribute;
