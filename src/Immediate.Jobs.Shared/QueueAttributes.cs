namespace Immediate.Jobs.Shared;

/// <summary>Defines a strongly typed job queue.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class QueueDefinitionAttribute : Attribute
{
	/// <summary>The stable persisted queue name, or <see langword="null"/> to derive it from the type name.</summary>
	/// <value>The stable queue name, or <see langword="null"/> to derive it from the type name.</value>
	public string? Name { get; init; }

	/// <summary>The dispatch priority. Larger values are dispatched first.</summary>
	/// <value>The queue dispatch priority.</value>
	public int Priority { get; init; }

	/// <summary>Maximum in-flight jobs on one scheduler node. Zero means unbounded.</summary>
	/// <value>The maximum in-flight jobs per node, or zero for no limit.</value>
	public int Concurrency { get; init; }
}

/// <summary>Assigns a generated job to a strongly typed queue.</summary>
/// <typeparam name="TQueue">A type marked with <see cref="QueueDefinitionAttribute"/>.</typeparam>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UsesQueueAttribute<TQueue> : Attribute;
