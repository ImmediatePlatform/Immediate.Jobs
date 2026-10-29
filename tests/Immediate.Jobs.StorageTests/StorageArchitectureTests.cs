using System.Reflection;
using System.Runtime.CompilerServices;
using Immediate.Jobs.Shared.Storage;

namespace Immediate.Jobs.StorageTests;

public sealed class StorageArchitectureTests
{
	[Fact]
	public void SharedAssemblyDoesNotExposeInternals() =>
		Assert.Empty(typeof(IJobStorage).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>());
}
