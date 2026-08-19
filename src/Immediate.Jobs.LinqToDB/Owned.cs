using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.LinqToDB;

/// <summary>
///	    Represents a container for a scope and a scoped service that is rooted by the scope.
/// </summary>
/// <typeparam name="T">
///	    The type of the service contained by the scope.
/// </typeparam>
internal sealed class OwnedScope<T> : IAsyncDisposable
{
	internal OwnedScope(
		T service,
		IAsyncDisposable disposable
	)
	{
		Service = service;
		_disposable = disposable;
	}

	/// <summary>
	///	    The instance of the service contained by the scope.
	/// </summary>
	public T Service { get; }

	private readonly IAsyncDisposable _disposable;

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		await _disposable.DisposeAsync().ConfigureAwait(false);
	}
}

/// <summary>
///		A factory for creating a scope containing a strong-type service as it's root.
/// </summary>
/// <typeparam name="T">
///		The type of the service that should be created at the root of the scope.
/// </typeparam>
/// <param name="serviceScopeFactory">
///		A <see cref="IServiceScopeFactory"/> used to create the scope for the service.
/// </param>
internal sealed class Owned<T>(
	IServiceScopeFactory serviceScopeFactory
) where T : class
{
	/// <summary>
	///	    Creates a temporary scope and gets an instance of the service from that scope.
	/// </summary>
	/// <returns>
	///	    An <see cref="OwnedScope{T}"/> containing both the scope and the service, so that the scope can be disposed
	///     at the appropriate time.
	/// </returns>
	public OwnedScope<T> GetScope() => GetScope(out _);

	/// <summary>
	///	    Creates a temporary scope and gets an instance of the service from that scope.
	/// </summary>
	/// <param name="service">
	///		The instance of the service created from the scope.
	/// </param>
	/// <returns>
	///	    An <see cref="OwnedScope{T}"/> containing both the scope and the service, so that the scope can be disposed
	///     at the appropriate time.
	/// </returns>
	public OwnedScope<T> GetScope(out T service)
	{
		var scope = serviceScopeFactory.CreateAsyncScope();
		try
		{
			service = scope.ServiceProvider.GetRequiredService<T>();
			return new(service, scope);
		}
		catch
		{
			scope.Dispose();
			throw;
		}
	}
}
