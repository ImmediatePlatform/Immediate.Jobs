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
