"""Render the same fixture IR with a historical renderer and compare every output byte.

Requires the baseline commit in local Git history and an exported fixture directory.
No historical CLI, MSBuild host, or analyzer is executed.
"""

import json
import subprocess
import sys
import tempfile
from pathlib import Path

BASELINE = "f9b1cf0e1d160d2d3622bbe7845db601020bc711"
REPOSITORY = Path(__file__).resolve().parent.parent


def run(*args, cwd=REPOSITORY):
    subprocess.run(args, cwd=cwd, check=True)


def main():
    fixtures = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory(prefix="codedocs-renderer-regression-") as temporary:
        root = Path(temporary)
        legacy = root / "legacy"
        paths = subprocess.check_output(["git", "ls-tree", "-r", "--name-only", BASELINE], cwd=REPOSITORY, text=True).splitlines()
        for name in paths:
            if name == "Directory.Build.props" or name.startswith(("src/CodeDocs.Model/", "src/CodeDocs.Rendering/")):
                path = legacy / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(subprocess.check_output(["git", "show", f"{BASELINE}:{name}"], cwd=REPOSITORY))
        runner = root / "runner"
        runner.mkdir()
        project = legacy / "src/CodeDocs.Rendering/CodeDocs.Rendering.csproj"
        # XML path escaping is handled independently of shell arguments.
        from xml.sax.saxutils import escape
        (runner / "Runner.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '</PropertyGroup><ItemGroup><ProjectReference Include="' + escape(str(project), {'"': '&quot;'}) +
            '" /></ItemGroup></Project>', encoding="utf-8")
        (runner / "Program.cs").write_text('''
using System.Text.Json;
using CodeDocs.Model;
using CodeDocs.Rendering;
foreach (var fixture in Directory.GetDirectories(args[0]).Order(StringComparer.Ordinal))
{
    var doc = JsonSerializer.Deserialize<SolutionDocument>(File.ReadAllText(Path.Combine(fixture, "single/json/api.json")), SiteRenderer.JsonOptions)!;
    var output = Path.Combine(args[1], Path.GetFileName(fixture));
    var options = new BuildOptions { Input = "fixture.csproj", Output = output, Formats = ["html", "markdown", "json"], SingleFile = true };
    await SiteRenderer.RenderAsync(doc, options, Path.Combine(output, "single"));
    await SiteRenderer.RenderAsync(doc, options with { SingleFile = false }, Path.Combine(output, "multi"));
}
''', encoding="utf-8")
        baseline_output = root / "baseline"
        run("dotnet", "build", str(runner / "Runner.csproj"), "-c", "Release", "-m:1", "-p:NuGetAudit=false")
        run("dotnet", "run", "--project", str(runner), "-c", "Release", "--no-build", "--", str(fixtures), str(baseline_output))
        def contents(directory):
            return {p.relative_to(directory).as_posix(): p.read_bytes() for p in directory.rglob("*") if p.is_file()}
        before, after = contents(baseline_output), contents(fixtures)
        changed = sorted(k for k in before.keys() | after.keys() if before.get(k) != after.get(k))
        if changed:
            raise AssertionError("Renderer compatibility regression: " + json.dumps(changed))
        assert before, "No fixtures exported"
        print(f"Renderer baseline {BASELINE[:7]}: {len(before)} files are byte-identical using the same IR.")


if __name__ == "__main__":
    main()
