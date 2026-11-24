using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DeadCodeCleaner.Tests;

/// <summary>Prepares the solutions the tests analyze, once per test run: the fixture and every
/// scenario folder are copied to a temp directory on the local file system, stub framework assemblies
/// are compiled in memory, and each solution is loaded with MSBuild at most once and shared.</summary>
public static class TestSolutions
{
    static readonly string RunRoot = Path.Combine(Path.GetTempPath(), TestConstants.TempRootPrefix + Guid.NewGuid().ToString("N"));
    static readonly ConcurrentDictionary<string, Lazy<string>> Prepared = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, Lazy<Task<LoadedSolution>>> Loaded = new(StringComparer.Ordinal);
    static readonly SemaphoreSlim LoadGate = new(TestConstants.MaxParallelLoads);
    static string _referencePackDirectory;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // MSBuild types must not be touched before the SDK's MSBuild is registered.
        if (!MSBuildLocator.IsRegistered) { MSBuildLocator.RegisterDefaults(); }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteRunRoot();
    }

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FixtureConfigPath { get; } = Path.Combine(RepoRoot, TestConstants.TestsDirectoryName, TestConstants.FixtureDirectoryName, TestConstants.FixtureConfigFileName);

    /// <summary>The shared, never-modified copy of tests/Fixture.</summary>
    public static string Fixture
    {
        get
        {
            var dir = Prepared.GetOrAdd("fixture", _ => new Lazy<string>(() => CopyFixture("shared"))).Value;
            return Path.Combine(dir, TestConstants.FixtureSolutionFileName);
        }
    }

    /// <summary>The shared, never-modified copy of tests/Scenarios/&lt;name&gt;.</summary>
    public static string Scenario(string name)
    {
        var dir = Prepared.GetOrAdd("scenario:" + name, _ => new Lazy<string>(() => CopyScenario(name, "shared"))).Value;
        return Path.Combine(dir, TestConstants.ScenarioSolutionFileName);
    }

    /// <summary>A private copy of tests/Fixture that a test may modify.</summary>
    public static string FreshFixture()
    {
        var dir = CopyFixture(Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, TestConstants.FixtureSolutionFileName);
    }

    /// <summary>A private copy of a scenario that a test may modify.</summary>
    public static string FreshScenario(string name)
    {
        var dir = CopyScenario(name, Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, TestConstants.ScenarioSolutionFileName);
    }

    /// <summary>The loaded solution, shared by every analysis of the same (unmodified) path.</summary>
    public static Task<LoadedSolution> Load(string solutionPath)
    {
        return Loaded.GetOrAdd(solutionPath, p => new Lazy<Task<LoadedSolution>>(() => LoadGated(p))).Value;
    }

    /// <summary>Loads a solution afresh (after files changed on disk); the caller disposes it.</summary>
    public static Task<LoadedSolution> LoadFresh(string solutionPath)
    {
        return LoadGated(solutionPath);
    }

    public static AnalyzerConfig FixtureConfig()
    {
        return new AnalyzerConfig { InSolutionRegistrations = true };
    }

    public static AnalyzerConfig KeepConfig(string pattern, bool inSolutionRegistrations = true)
    {
        return new AnalyzerConfig { InSolutionRegistrations = inSolutionRegistrations, Keep = new List<string> { pattern } };
    }

    /// <summary>A private copy of a scenario with an ASP.NET Web Site project added to its solution.</summary>
    public static string ScenarioWithWebSite(string name)
    {
        var solution = FreshScenario(name);
        AddWebSiteProject(solution);
        return solution;
    }

    /// <summary>Compile errors of every project, after loading the solution from disk afresh.</summary>
    public static async Task<List<string>> CompileErrors(string solutionPath)
    {
        using var loaded = await LoadFresh(solutionPath);
        var errors = new List<string>();
        // A project that silently failed to load would make "no errors" vacuous.
        var expectedProjects = File.ReadLines(solutionPath).Count(l => l.StartsWith("Project(", StringComparison.Ordinal) && l.Contains(".csproj", StringComparison.Ordinal));
        var actualProjects = loaded.Solution.Projects.Count();
        if (actualProjects != expectedProjects) { errors.Add($"{actualProjects} of {expectedProjects} projects loaded"); }
        foreach (var project in loaded.Solution.Projects)
        {
            var comp = await project.GetCompilationAsync();
            if (comp == null)
            {
                errors.Add(project.Name + ": no compilation");
                continue;
            }
            errors.AddRange(comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => project.Name + ": " + d));
        }
        return errors;
    }

    /// <summary>Fails when the analysis could not see the whole solution: every assertion about what
    /// is or is not deleted would otherwise hold for nothing.</summary>
    public static DeletionReport RequireCleanLoad(DeletionReport report)
    {
        if (report.ProjectsFailedToLoad > 0 || report.ProjectsWithCompileErrors > 0)
        {
            throw new InvalidOperationException($"{report.Solution}: {report.ProjectsFailedToLoad} project(s) failed to load, {report.ProjectsWithCompileErrors} had compile errors");
        }
        return report;
    }

    /// <summary>True when the symbol is deleted, itself or with a containing type.</summary>
    public static bool IsDeleted(DeletionReport report, string fqn)
    {
        return report.Symbols.Any(s => s.Fqn == fqn || fqn.StartsWith(s.Fqn + ".", StringComparison.Ordinal));
    }

    /// <summary>Content hashes of the sources, projects and solution under a directory.</summary>
    public static Dictionary<string, string> Snapshot(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!TestConstants.SnapshotExtensions.Contains(Path.GetExtension(file))) { continue; }
            if (file.Split(Path.DirectorySeparatorChar).Any(TestConstants.SkippedCopyDirectories.Contains)) { continue; }
            result[file] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        }
        return result;
    }

    /// <summary>Adds an ASP.NET Web Site project entry (no csproj) to a solution file.</summary>
    public static void AddWebSiteProject(string solutionPath)
    {
        var text = File.ReadAllText(solutionPath);
        var at = text.IndexOf("\nGlobal", StringComparison.Ordinal) + 1;
        File.WriteAllText(solutionPath, text.Insert(at, TestConstants.WebSiteProjectEntry));
    }

    static async Task<LoadedSolution> LoadGated(string solutionPath)
    {
        await LoadGate.WaitAsync();
        try
        {
            var loaded = await Engine.LoadSolution(solutionPath);
            return loaded;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    static string FindRepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, TestConstants.RepoSolutionFileName))) { return dir; }
        }
        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }

    static string CopyFixture(string copyName)
    {
        var target = Path.Combine(RunRoot, "fixture-" + copyName);
        CopyDirectory(Path.Combine(RepoRoot, TestConstants.TestsDirectoryName, TestConstants.FixtureDirectoryName), target);
        return target;
    }

    static string CopyScenario(string name, string copyName)
    {
        var target = Path.Combine(RunRoot, name + "-" + copyName);
        CopyDirectory(Path.Combine(RepoRoot, TestConstants.TestsDirectoryName, TestConstants.ScenariosDirectoryName, name), target);
        CompileStubs(target);
        WriteSolution(target);
        return target;
    }

    /// <summary>Copies sources only: build output (bin, obj) holds absolute paths of the original location.</summary>
    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (TestConstants.SkippedCopyDirectories.Contains(name)) { continue; }
            CopyDirectory(dir, Path.Combine(target, name));
        }
    }

    /// <summary>Every stubs/&lt;Name&gt;/ folder becomes stubs/out/&lt;AssemblyName&gt;.dll, the assembly name
    /// read from its csproj: compiled in memory against the reference pack, no dotnet build.</summary>
    static void CompileStubs(string scenarioDir)
    {
        var stubsDir = Path.Combine(scenarioDir, TestConstants.StubsDirectoryName);
        if (!Directory.Exists(stubsDir)) { return; }
        var outDir = Path.Combine(stubsDir, TestConstants.StubsOutputDirectoryName);
        Directory.CreateDirectory(outDir);
        var references = ReferencePackAssemblies();
        foreach (var stub in Directory.GetDirectories(stubsDir))
        {
            if (Path.GetFileName(stub) == TestConstants.StubsOutputDirectoryName) { continue; }
            var csproj = Directory.GetFiles(stub, "*.csproj").Single();
            var assemblyName = System.Xml.Linq.XDocument.Load(csproj).Descendants().Single(e => e.Name.LocalName == "AssemblyName").Value.Trim();
            var trees = Directory.GetFiles(stub, "*.cs").Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f)).ToList();
            var compilation = CSharpCompilation.Create(assemblyName, trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var result = compilation.Emit(Path.Combine(outDir, assemblyName + ".dll"));
            if (!result.Success)
            {
                throw new InvalidOperationException($"stub {assemblyName} failed to compile: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            }
        }
    }

    static List<MetadataReference> ReferencePackAssemblies()
    {
        var dir = _referencePackDirectory ??= FindReferencePack();
        return Directory.GetFiles(dir, "*.dll").Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
    }

    static string FindReferencePack()
    {
        var sdk = MSBuildLocator.QueryVisualStudioInstances().First();
        var dotnetRoot = Path.GetFullPath(Path.Combine(sdk.MSBuildPath, "..", ".."));
        var packs = Path.Combine(dotnetRoot, "packs", TestConstants.ReferencePackName);
        var version = Directory.GetDirectories(packs)
            .Select(Path.GetFileName)
            .Where(v => v.StartsWith(TestConstants.ReferencePackVersionPrefix, StringComparison.Ordinal))
            .OrderByDescending(v => Version.TryParse(v.Split('-')[0], out var parsed) ? parsed : new Version())
            .First();
        return Path.Combine(packs, version, "ref", TestConstants.ReferencePackFramework);
    }

    /// <summary>Writes Scratch.sln listing every project outside stubs/, in ordinal path order.</summary>
    static void WriteSolution(string scenarioDir)
    {
        var stubsPrefix = TestConstants.StubsDirectoryName + Path.DirectorySeparatorChar;
        var projects = Directory.GetFiles(scenarioDir, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(scenarioDir, p))
            .Where(p => !p.StartsWith(stubsPrefix, StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var sb = new StringBuilder();
        sb.Append("Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\n");
        var guids = new List<string>();
        foreach (var p in projects)
        {
            var guid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            guids.Add(guid);
            sb.Append($"Project(\"{{{TestConstants.CSharpProjectTypeGuid}}}\") = \"{Path.GetFileNameWithoutExtension(p)}\", \"{p}\", \"{guid}\"\nEndProject\n");
        }
        sb.Append("Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Any CPU = Debug|Any CPU\n\tEndGlobalSection\n");
        sb.Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n");
        foreach (var g in guids)
        {
            sb.Append($"\t\t{g}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\n\t\t{g}.Debug|Any CPU.Build.0 = Debug|Any CPU\n");
        }
        sb.Append("\tEndGlobalSection\nEndGlobal\n");
        File.WriteAllText(Path.Combine(scenarioDir, TestConstants.ScenarioSolutionFileName), sb.ToString());
    }

    static void DeleteRunRoot()
    {
        try
        {
            if (Directory.Exists(RunRoot)) { Directory.Delete(RunRoot, recursive: true); }
        }
        catch (IOException) { /* a file still held open: the temp directory is cleaned by the OS */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
    }
}
