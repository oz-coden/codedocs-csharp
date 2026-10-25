using System.Text.Json;
using System.Text.Json.Serialization;
using CodeDocs.Model;

namespace CodeDocs.Rendering;

/// <summary>Serializes the semantic model without presentation-specific transformations.</summary>
public static class JsonRenderer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Render(SolutionDocument document, string directory) =>
        RenderOutput.Write(directory, "api.json", JsonSerializer.Serialize(document, Options));
}
