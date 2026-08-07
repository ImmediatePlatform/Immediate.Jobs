namespace Immediate.Jobs.DistributedAspire.Shared.Data.Contracts;

public interface IKey<T>
{
	T Id { get; }
}
