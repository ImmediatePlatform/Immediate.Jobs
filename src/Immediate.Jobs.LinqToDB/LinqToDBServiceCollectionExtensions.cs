using LinqToDB;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.LinqToDB;

/// <summary>Registers LinqToDB job storage.</summary>
public static class LinqToDBServiceCollectionExtensions
{
	/// <summary>Selects LinqToDB as the Immediate.Jobs storage provider.</summary>
	/// <remarks>
	/// The application owns the configured <see cref="DataOptions"/> and its ADO.NET driver. Call
	/// <see cref="LinqToDBSchemaExtensions.CreateImmediateJobsSchemaAsync"/> explicitly when bootstrapping a new database.
	/// </remarks>
	/// <param name="options">The Immediate.Jobs options to configure.</param>
	/// <param name="dataOptions">The immutable LinqToDB connection options.</param>
	/// <param name="schema">The database schema containing the Immediate.Jobs tables, or <see langword="null"/> for the provider default.</param>
	/// <returns>The configured Immediate.Jobs options.</returns>
	public static ImmediateJobsOptions UseLinqToDB(
		this ImmediateJobsOptions options,
		DataOptions dataOptions,
		string? schema = null
	)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(dataOptions);
		return options.UseStorage(services => new LinqToDBJobStorage(
			dataOptions,
			schema,
			services.GetService<TimeProvider>()
		));
	}
}
