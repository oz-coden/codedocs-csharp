using System.Text.Json;
using CodeDocs.Model;
using CodeDocs.Rendering;

namespace CodeDocs.Cli;

public sealed class OutputTransaction : IDisposable
{
    private const string Marker = ".codedocs-output.json";
    private readonly string destination;
    private readonly string staging;
    private readonly FileStream outputLock;
    private bool committed;
    private bool renderStarted;
    private bool prepared;

    public OutputTransaction(BuildOptions options)
    {
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Output));
        var input = Path.GetFullPath(options.Input);
        if (destination == Path.GetPathRoot(destination) || IsWithin(input, destination) ||
            destination.Equals(Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory), PathComparison))
            throw new ArgumentExceptionWithUsage("Output must be a dedicated directory, not the input directory or one of its ancestors.");
        RejectSymlinks(destination);
        if (File.Exists(destination)) throw new ArgumentExceptionWithUsage("Output path is an existing file.");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var lockPath = Path.Combine(parent, "." + Path.GetFileName(destination) + ".codedocs.lock");
        RejectSymlinks(lockPath);
        outputLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (Directory.Exists(destination))
            {
                ValidateExistingTree(destination);
                if (File.Exists(Path.Combine(destination, ".git")) || Directory.Exists(Path.Combine(destination, ".git")) || Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Any(f => Path.GetExtension(f).ToLowerInvariant() is ".cs" or ".csproj" or ".sln"))
                    throw new ArgumentExceptionWithUsage("Output contains source files or a Git repository; choose a dedicated documentation directory.");
                var marker = Path.Combine(destination, Marker);
                if (File.Exists(marker))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(marker));
                    if (!json.RootElement.TryGetProperty("generator", out var generator) || generator.GetString() != "CodeDocs.CSharp")
                        throw new ArgumentExceptionWithUsage("Invalid CodeDocs output marker.");
                }
                else if (Directory.EnumerateFileSystemEntries(destination).Any() && !options.Clean)
                    throw new ArgumentExceptionWithUsage("Existing output is not managed by CodeDocs. Use a new directory, or --clean to replace this directory.");
            }
            staging = Path.Combine(parent, "." + Path.GetFileName(destination) + ".codedocs-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
        }
        catch { outputLock.Dispose(); throw; }
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool IsWithin(string path, string directory) => path.Equals(directory, PathComparison) || path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, PathComparison);
    private static void RejectSymlinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentExceptionWithUsage("Output paths must not contain symbolic links or reparse points.");
    }
    private static void ValidateExistingTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentExceptionWithUsage("Existing output contains symbolic links or reparse points.");
            if ((attributes & FileAttributes.Directory) != 0) ValidateExistingTree(entry);
        }
    }
    public async Task RenderAsync(SolutionDocument document, BuildOptions options, CancellationToken cancellationToken = default)
    {
        if (renderStarted) throw new InvalidOperationException("An output transaction can render only once.");
        renderStarted = true;
        await SiteRenderer.RenderAsync(document, options, staging, cancellationToken);
        var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToArray();
        if (files.Length == 0 || files.Any(f => new FileInfo(f).Length == 0)) throw new IOException("Rendered output validation failed.");
        foreach (var format in options.Formats)
        {
            var root = options.Formats.Count > 1 ? Path.Combine(staging, format) : staging;
            var entry = format switch { "html" => "index.html", "markdown" => options.SingleFile ? "api.md" : "README.md", _ => "api.json" };
            if (!File.Exists(Path.Combine(root, entry))) throw new IOException("Missing output entry: " + entry);
        }
        OutputValidator.Validate(staging);
        File.WriteAllText(Path.Combine(staging, Marker), "{\n  \"generator\": \"CodeDocs.CSharp\",\n  \"version\": \"0.1.0\"\n}\n");
        prepared = true;
    }
    public void Commit()
    {
        if (committed) throw new InvalidOperationException("Output has already been committed.");
        if (!prepared) throw new InvalidOperationException("Output must be rendered and validated before commit.");
        var backup = destination + ".codedocs-backup-" + Guid.NewGuid().ToString("N");
        var existed = Directory.Exists(destination);
        if (existed) Directory.Move(destination, backup);
        try { Directory.Move(staging, destination); committed = true; }
        catch
        {
            if (existed) Directory.Move(backup, destination);
            throw;
        }
        if (existed)
        {
            try { Directory.Delete(backup, true); }
            catch (IOException) { /* The committed snapshot is valid; retain the backup if cleanup fails. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
    public void Dispose()
    {
        try { if (!committed && Directory.Exists(staging)) Directory.Delete(staging, true); }
        finally { outputLock.Dispose(); }
    }
}
