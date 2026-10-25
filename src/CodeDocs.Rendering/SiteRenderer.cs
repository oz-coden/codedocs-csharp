using System.Text.Json;
using CodeDocs.Model;

namespace CodeDocs.Rendering;

/// <summary>Routes selected formats into their output directories. Replacement is owned by the caller.</summary>
public static class SiteRenderer
{
    // Keep the original entry points available to existing consumers.
    public static readonly string[] Categories = Presentation.Categories;
    public static readonly JsonSerializerOptions JsonOptions = JsonRenderer.Options;

    public static Task RenderAsync(SolutionDocument document, BuildOptions options, string destination, CancellationToken cancellationToken = default)
    {
        foreach (var format in options.Formats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = options.Formats.Count > 1 ? Path.Combine(destination, format) : destination;
            switch (format)
            {
                case "html": HtmlRenderer.Render(document, options, directory); break;
                case "markdown": MarkdownRenderer.Render(document, options, directory); break;
                case "json": JsonRenderer.Render(document, directory); break;
                default: throw new ArgumentException("Unsupported output format: " + format, nameof(options));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
