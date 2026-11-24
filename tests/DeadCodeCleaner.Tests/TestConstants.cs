namespace DeadCodeCleaner.Tests;

/// <summary>Fixed values of the test infrastructure.</summary>
public static class TestConstants
{
    public const string RepoSolutionFileName = "DeadCodeCleaner.sln";
    public const string TestsDirectoryName = "tests";
    public const string FixtureDirectoryName = "Fixture";
    public const string FixtureSolutionFileName = "Fixture.sln";
    public const string FixtureConfigFileName = "dcc-config.json";
    public const string ScenariosDirectoryName = "Scenarios";
    public const string ScenarioSolutionFileName = "Scratch.sln";
    public const string StubsDirectoryName = "stubs";
    public const string StubsOutputDirectoryName = "out";
    public const string TempRootPrefix = "dcc-tests-";
    public const string CSharpProjectTypeGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
    public const string WebSiteProjectEntry = "Project(\"{E24C65DC-7377-472B-9ABA-BC803B73C61A}\") = \"OldSite\", \"http://localhost/OldSite\", \"{11111111-2222-3333-4444-555555555555}\"\nEndProject\n";
    public const string ReferencePackName = "Microsoft.NETCore.App.Ref";
    public const string ReferencePackFramework = "net9.0";
    public const string ReferencePackVersionPrefix = "9.";
    public const string Marker = "todo: to be deleted";
    public const string ReportFileName = "report.json";
    public const string FixtureControllersPath = "Fixture.Web/Controllers.cs";
    public const string FixtureProgramPath = "Fixture.Web/Program.cs";
    /// <summary>Prepended to a fixture file in Windows-1252: 'é' is one byte there, two in UTF-8.</summary>
    public const string AccentedComment = "// café\n";
    public const string GeneratedGlobalUsingsPath = "Api/obj/Gen/Api.GlobalUsings.g.cs";

    /// <summary>MSBuild loads at once: each opens a design-time build host, so too many at a time only thrash.</summary>
    public const int MaxParallelLoads = 4;

    public static readonly string[] SnapshotExtensions = { ".cs", ".csproj", ".sln" };
    public static readonly string[] SkippedCopyDirectories = { "bin", "obj" };
}
