using System.Text;

namespace CodeDocs.Rendering;

internal static class RenderOutput
{
    public static void Write(string directory, string file, string content)
    {
        var path = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\r\n", "\n").TrimEnd() + "\n", new UTF8Encoding(false));
    }
}
