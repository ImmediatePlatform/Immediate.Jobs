using LinqToDB;
using Microsoft.Extensions.DependencyInjection;

namespace Immediate.Jobs.LinqToDB;

/// <summary>Registers LinqToDB job storage.</summary>
public static class LinqToDBServiceCollectionExtensions
{
	/// <summary>
	///		Selects LinqToDB as the Immediate.Jobs storage provider.
	/// </summary>
	/// <remarks>
	///		The application owns the configured <see cref="DataOptions"/> and its ADO.NET driver. Call <see
	///		cref="LinqToDBSchemaExtensions.CreateImmediateJobsSchemaAsync"/> explicitly when bootstrapping a new database.
	/// </remarks>
	/// <param name="builder">
	///		The Immediate.Jobs storage options builder to configure.
	/// </param>
	/// <param name="dataOptions">
	///		The immutable LinqToDB connection options.
	/// </param>
	/// <param name="schema">
	///		The database schema containing the Immediate.Jobs tables, or <see langword="null"/> for the provider default.
	/// </param>
	/// <returns>
	///		The configured Immediate.Jobs options.
	/// </returns>
	public static ImmediateJobsStorageBuilder UseLinqToDB(
		this ImmediateJobsStorageBuilder builder,
		DataOptions dataOptions,
		string? schema = null
	)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(dataOptions);

		return builder.UseStorage(
			services =>
				new LinqToDBJobStorage(
					dataOptions,
					schema,
					services.GetService<TimeProvider>()
				)
		);
	}
}
