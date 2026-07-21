using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Immediate.Jobs.Dashboard;

internal static class DashboardAssets
{
	private const string ResourcePrefix = "Immediate.Jobs.Dashboard.Assets.";
	private static readonly Assembly Assembly = typeof(DashboardAssets).Assembly;

	public static async Task<IResult> GetAsync(string name)
	{
		var resourceName = ResourcePrefix + name;
		await using var stream = Assembly.GetManifestResourceStream(resourceName);
		if (stream is null)
			return Results.NotFound();

		using var memory = new MemoryStream();
		await stream.CopyToAsync(memory).ConfigureAwait(false);
		return Results.Bytes(memory.ToArray(), GetContentType(name));
	}

	private static string GetContentType(string name) => Path.GetExtension(name) switch
	{
		".css" => "text/css; charset=utf-8",
		".js" => "text/javascript; charset=utf-8",
		_ => "text/html; charset=utf-8",
	};
}
