using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Immediate.Jobs.Shared.Storage;

public sealed partial class InMemoryJobStorage
{
	// Use only under _gate. Do not retain refs across additions or removals in the same dictionary.
	[SuppressMessage("Style", "IDE0007:Use implicit type", Justification = "Interaction between IDE0007 and CS8619")]
	private static ref TElement ValidateExistingElement<TKey, TElement>(Dictionary<TKey, TElement> dictionary, TKey key)
		where TKey : notnull
	{
		ref TElement record = ref CollectionsMarshal.GetValueRefOrNullRef(dictionary, key);
		if (Unsafe.IsNullRef(ref record))
			throw new ImmediateJobException($"{typeof(TElement)} '{key}' does not exist.");
		return ref record;
	}

	// This inserts a default slot: finish validation and construct the value before calling.
	[SuppressMessage("Style", "IDE0007:Use implicit type", Justification = "Interaction between IDE0007 and CS8619")]
	private static ref TElement? ValidateNewElement<TKey, TElement>(Dictionary<TKey, TElement> dictionary, TKey key)
		where TKey : notnull
	{
		ref TElement? record = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out var exists);
		if (exists)
			throw new ImmediateJobException($"{typeof(TElement)} '{key}' already exists.");
		return ref record;
	}
}
