namespace DeadCodeCleaner.Tests;

/// <summary>The result of editing a private copy of a solution: its directory, the report of the
/// analysis that drove the last edit, content snapshots taken along the way, and the editor's exit code.</summary>
public sealed class EditRun
{
    public required string Directory { get; init; }
    public required DeletionReport Report { get; init; }
    public Dictionary<string, string> AfterFirstMark { get; init; }
    /// <summary>Files (relative to Directory) holding a marker after the first mark.</summary>
    public List<string> MarkedFiles { get; init; }
    public Dictionary<string, string> AfterSecondMark { get; init; }
    public int DeleteExitCode { get; init; }
    public List<string> CompileErrorsAfter { get; init; }

    public string ReadFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(Directory, relativePath));
    }

    public bool FileExists(string relativePath)
    {
        return File.Exists(Path.Combine(Directory, relativePath));
    }
}

/// <summary>Mark/delete runs on private copies, each performed once and shared by the tests that
/// assert on its outcome.</summary>
public static class EditRuns
{
    /// <summary>tests/Fixture: mark WidgetsController twice, then delete it.</summary>
    public static readonly Lazy<Task<EditRun>> MarkTwiceThenDeleteWidgetsController = new(() => Task.Run(() => MarkTwiceThenDelete(TestSolutions.FreshFixture(), TestSolutions.FixtureConfig(), "Fixture.Web.WidgetsController")));

    /// <summary>tests/Fixture: delete an implementation (and an enum member) whose service stays.</summary>
    public static readonly Lazy<Task<EditRun>> DeleteWidgetServiceAndEnumMember = new(() => Task.Run(() => Delete(TestSolutions.FreshFixture(), TestSolutions.FixtureConfig(), "Fixture.Domain.WidgetService", "Fixture.Domain.WidgetKind.Fancy")));

    /// <summary>tests/Fixture: delete a mapped member, leaving the rest of its mapping chain.</summary>
    public static readonly Lazy<Task<EditRun>> DeleteAuditNote = new(() => Task.Run(() => Delete(TestSolutions.FreshFixture(), TestSolutions.FixtureConfig(), "Fixture.Domain.GadgetDto.AuditNote")));

    /// <summary>Scenario ShopProfiles: mark BetaController, whose dead helper starts inside a multi-line string.</summary>
    public static readonly Lazy<Task<EditRun>> MarkShopBetaController = new(() => Task.Run(() => Mark(TestSolutions.FreshScenario("ShopProfiles"), TestSolutions.FixtureConfig(), "Shop.BetaController")));

    /// <summary>Analyzes a solution loaded afresh from disk (its files may have changed).</summary>
    public static async Task<DeletionReport> AnalyzeFresh(string solutionPath, AnalyzerConfig config, params string[] targets)
    {
        using var loaded = await TestSolutions.LoadFresh(solutionPath);
        var options = new CliOptions { SolutionPath = solutionPath };
        options.RemoveTargets.AddRange(targets);
        var (report, _) = await new Engine(options, config, _ => { }).Analyze(loaded);
        return report;
    }

    static async Task<EditRun> MarkTwiceThenDelete(string solutionPath, AnalyzerConfig config, params string[] targets)
    {
        var dir = Path.GetDirectoryName(solutionPath);
        Editor.Mark(await AnalyzeFresh(solutionPath, config, targets), _ => { });
        var first = TestSolutions.Snapshot(dir);
        var marked = first.Keys.Where(f => File.ReadAllText(f).Contains(TestConstants.Marker)).Select(f => Path.GetRelativePath(dir, f)).ToList();
        Editor.Mark(await AnalyzeFresh(solutionPath, config, targets), _ => { });
        var second = TestSolutions.Snapshot(dir);
        var report = await AnalyzeFresh(solutionPath, config, targets);
        var exit = Editor.Delete(report, _ => { });
        var errors = await TestSolutions.CompileErrors(solutionPath);
        return new EditRun { Directory = dir, Report = report, AfterFirstMark = first, MarkedFiles = marked, AfterSecondMark = second, DeleteExitCode = exit, CompileErrorsAfter = errors };
    }

    static async Task<EditRun> Delete(string solutionPath, AnalyzerConfig config, params string[] targets)
    {
        var report = await AnalyzeFresh(solutionPath, config, targets);
        var exit = Editor.Delete(report, _ => { });
        var errors = await TestSolutions.CompileErrors(solutionPath);
        return new EditRun { Directory = Path.GetDirectoryName(solutionPath), Report = report, DeleteExitCode = exit, CompileErrorsAfter = errors };
    }

    static async Task<EditRun> Mark(string solutionPath, AnalyzerConfig config, params string[] targets)
    {
        var report = await AnalyzeFresh(solutionPath, config, targets);
        Editor.Mark(report, _ => { });
        return new EditRun { Directory = Path.GetDirectoryName(solutionPath), Report = report };
    }
}
