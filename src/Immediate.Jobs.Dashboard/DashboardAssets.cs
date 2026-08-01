using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;

namespace Immediate.Jobs.Dashboard;

internal static class DashboardAssets
{
	private const string ResourcePrefix = "Immediate.Jobs.Dashboard.Assets.";
	private const string BaseElement = "<base data-dashboard-base href=\"./\">";
	private static readonly Assembly Assembly = typeof(DashboardAssets).Assembly;

	public static async Task<IResult> GetIndexAsync(HttpContext context, string prefix)
	{
		var resourceName = ResourcePrefix + "index.html";
		await using var stream = Assembly.GetManifestResourceStream(resourceName);
		if (stream is null)
			return Results.NotFound();

		using var reader = new StreamReader(stream, Encoding.UTF8);
		var template = await reader.ReadToEndAsync(context.RequestAborted);
		var dashboardBase = HtmlEncoder.Default.Encode(context.Request.PathBase + prefix + "/");
		var html = template.Replace(
			BaseElement,
			$"<base data-dashboard-base href=\"{dashboardBase}\">",
			StringComparison.Ordinal
		);
		return Results.Text(html, "text/html; charset=utf-8", Encoding.UTF8);
	}

	public static async Task<IResult> GetAsync(string name)
	{
		var resourceName = ResourcePrefix + name;
		await using var stream = Assembly.GetManifestResourceStream(resourceName);
		if (stream is null)
			return Results.NotFound();

		using var memory = new MemoryStream();
		await stream.CopyToAsync(memory);
		return Results.Bytes(memory.ToArray(), GetContentType(name));
	}

	private static string GetContentType(string name) => Path.GetExtension(name) switch
	{
		".css" => "text/css; charset=utf-8",
		".js" => "text/javascript; charset=utf-8",
		_ => "text/html; charset=utf-8",
	};
}
