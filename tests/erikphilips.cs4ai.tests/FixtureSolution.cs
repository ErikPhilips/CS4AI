namespace ErikPhilips.Cs4Ai.Tests;

/// <summary>
/// Creates a tiny throwaway .slnx + .csproj + source file under a temp directory for verb-level
/// integration tests. Implements <see cref="IDisposable"/> so test classes can use a using
/// statement and the fixture cleans up after itself.
/// </summary>
internal sealed class FixtureSolution : IDisposable
{
    public string Root { get; }
    public string SlnxPath { get; }
    public string SrcDir => Path.Combine(Root, "src");

    public FixtureSolution(string sourceCode, string sourceFileName = "Calc.cs")
    {
        Root = Path.Combine(Path.GetTempPath(), "cs4ai-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SrcDir);

        File.WriteAllText(Path.Combine(SrcDir, "Fixture.csproj"), CsprojContent);
        File.WriteAllText(Path.Combine(SrcDir, sourceFileName), sourceCode);
        // Use classic .sln in tests — MSBuildLocator picked up by the test process may not
        // support .slnx. The verbs handle both formats; .sln keeps the test surface portable.
        SlnxPath = Path.Combine(Root, "Fixture.sln");
        File.WriteAllText(SlnxPath, SlnContent);

        // Initialize config so verbs don't return exit 6.
        var configPath = Cs4AiConfig.PathFor(Root);
        File.WriteAllText(configPath, Cs4AiConfig.Preset("sa1201").ToJson());
    }

    public string SourceFile(string name) => Path.Combine(SrcDir, name);

    public string ReadSource(string name) => File.ReadAllText(SourceFile(name));

    /// <summary>Write throwaway content (a member body, etc.) to a temp file inside the fixture
    /// and return its path — for passing to --from.</summary>
    public string WriteContent(string content)
    {
        var path = Path.Combine(Root, "content-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string SlnContent =
        "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
        "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Fixture\", \"src\\Fixture.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\r\n" +
        "EndProject\r\n" +
        "Global\r\n" +
        "EndGlobal\r\n";

    private const string CsprojContent = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;
}
