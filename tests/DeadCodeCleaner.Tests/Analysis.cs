using System.Collections.Concurrent;

namespace DeadCodeCleaner.Tests;

/// <summary>One analysis of a prepared solution with a config and removal targets. The report is
/// computed once and shared by every test asserting on it; all declared analyses can be started
/// together so they run in parallel while the tests await them one by one.</summary>
public sealed class Analysis
{
    static readonly ConcurrentQueue<Analysis> Declared = new();

    readonly Func<string> _solution;
    readonly Func<AnalyzerConfig> _config;
    readonly string[] _targets;
    readonly Lazy<Task<DeletionReport>> _report;

    public Analysis(Func<string> solution, Func<AnalyzerConfig> config, params string[] targets)
    {
        _solution = solution;
        _config = config;
        _targets = targets;
        _report = new Lazy<Task<DeletionReport>>(() => Task.Run(Compute));
        Declared.Enqueue(this);
    }

    public Task<DeletionReport> Analyze()
    {
        return _report.Value;
    }

    /// <summary>Starts every declared analysis without waiting for any.</summary>
    public static void StartAll()
    {
        foreach (var analysis in Declared) { _ = analysis.Analyze(); }
    }

    async Task<DeletionReport> Compute()
    {
        var solutionPath = _solution();
        var loaded = await TestSolutions.Load(solutionPath);
        var options = new CliOptions { SolutionPath = solutionPath };
        options.RemoveTargets.AddRange(_targets);
        var (report, _) = await new Engine(options, _config(), _ => { }).Analyze(loaded);
        return report;
    }
}
