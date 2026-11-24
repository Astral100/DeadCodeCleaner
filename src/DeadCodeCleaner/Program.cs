using Microsoft.Build.Locator;
using System.Text.Json;

namespace DeadCodeCleaner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!MSBuildLocator.IsRegistered) { MSBuildLocator.RegisterDefaults(); }
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine("error: no MSBuild / .NET SDK installation was found: " + e.Message);
            return 2;
        }
        var exitCode = await Run(args, Console.Out, Console.Error);
        return exitCode;
    }

    /// <summary>The whole command line run, writing to the given streams instead of the console.
    /// MSBuild must already be registered.</summary>
    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error)
    {
        CliOptions opts;
        try { opts = Parse(args); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            error.WriteLine("error: " + e.Message);
            PrintUsage(output);
            return 2;
        }

        var config = new AnalyzerConfig { StrictMembers = opts.StrictMembers };
        if (opts.ConfigPath != null)
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AnalyzerConfig>(File.ReadAllText(opts.ConfigPath), Constants.CaseInsensitiveJson);
                if (loaded != null)
                {
                    loaded.Keep ??= new List<string>();      // an explicit JSON null overrides the initializer
                    loaded.Roots ??= new List<string>();
                    loaded.StrictMembers |= opts.StrictMembers;
                    config = loaded;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                error.WriteLine($"error: could not read config {opts.ConfigPath}: {e.Message}");
                return 2;
            }
        }

        Action<string> log = output.WriteLine;
        var engine = new Engine(opts, config, log);

        try
        {
            var (report, graph) = await engine.Analyze();

            if (opts.Mode == "find")
            {
                var needle = opts.FindSubstring ?? "";
                var matches = graph.Decl.Values
                    .Select(s => (Fqn: SymbolNames.Fqn(s), Symbol: s))
                    .Where(x => x.Fqn.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Fqn, StringComparer.Ordinal)
                    .Take(Constants.FindMatchLimit)
                    .ToList();
                foreach (var (fqn, symbol) in matches)
                {
                    output.WriteLine($"{SymbolNames.Kind(symbol),-11} {fqn}");
                }
                output.WriteLine($"({matches.Count} match(es))");
                return 0;
            }

            ReportWriter.WriteConsole(report, opts.Verbose, output);
            var jsonPath = opts.JsonPath ?? Path.Combine(Environment.CurrentDirectory, Constants.DefaultReportFileName);
            ReportWriter.WriteJson(report, jsonPath, output);

            switch (opts.Mode)
            {
                case "mark":
                    Editor.Mark(report, log);
                    break;
                case "delete":
                    if (report.ProjectsFailedToLoad > 0 && !opts.AllowCompileErrors)
                    {
                        error.WriteLine($"error: {report.ProjectsFailedToLoad} project(s)/load failure(s) left parts of the solution INVISIBLE to the analysis (see the workspace warnings) — references in them cannot be seen and the cascade may over-delete; fix the load problems first, or pass --allow-compile-errors; nothing was deleted");
                        return 1;
                    }
                    if (report.ProjectsWithCompileErrors > 0 && !opts.AllowCompileErrors)
                    {
                        error.WriteLine($"error: {report.ProjectsWithCompileErrors} project(s) had compile errors during analysis — references in them are invisible and the cascade may over-delete; fix the build first, or pass --allow-compile-errors; nothing was deleted");
                        return 1;
                    }
                    if (report.WebsiteProjects > 0 && !opts.AllowWebsiteProjects)
                    {
                        error.WriteLine($"error: the solution contains {report.WebsiteProjects} ASP.NET Web Site project(s) — they are excluded from the analysis, so code they use in other projects looks unused and could be deleted; pass --allow-website-projects to delete anyway; nothing was deleted");
                        return 1;
                    }
                    if (report.Stats.ProductionRoots == 0 && !opts.AllowNoRoots)
                    {
                        error.WriteLine("error: no production entry points were detected — nothing counts as in use, so every caller of a removal target would be deleted with it; declare roots via --config keep/roots, or pass --allow-no-roots; nothing was deleted");
                        return 1;
                    }
                    // Deliberately NOT tied to --no-verify: skipping the expensive rebuild for speed
                    // must never also waive this independent, already-computed refusal.
                    if (report.StillReferencedTargets > 0 && !opts.AllowStillReferenced)
                    {
                        error.WriteLine($"error: {report.StillReferencedTargets} removal target(s) are still referenced by surviving code (see the STILL REFERENCED warnings) — deleting them breaks the callers, or silently rebinds them to another overload; fix the callers first (--allow-still-referenced overrides); nothing was deleted");
                        return 1;
                    }
                    if (report.ServicesLeftUnregistered > 0 && !opts.AllowUnregisteredServices)
                    {
                        error.WriteLine($"error: {report.ServicesLeftUnregistered} service(s) still in use would lose their only registration (see the SERVICE LEFT UNREGISTERED warnings) — the build passes but the container fails at runtime; register a replacement first, or pass --allow-unregistered-services; nothing was deleted");
                        return 1;
                    }
                    var reviewTests = report.Symbols.Count(s => s.Review && s.Category == Constants.CategoryBrokenTest);
                    if (reviewTests > 0 && !opts.DeleteReviewTests)
                    {
                        error.WriteLine($"error: {reviewTests} test(s) to be deleted also reference surviving code (see the [REVIEW] entries) — adjust or remove them first, or pass --delete-review-tests to delete them as-is; nothing was deleted");
                        return 1;
                    }
                    var reviewOrphans = report.Symbols.Count(s => s.Review && s.Category != Constants.CategoryBrokenTest);
                    if (reviewOrphans > 0 && !opts.DeleteReviewOrphans)
                    {
                        error.WriteLine($"error: {reviewOrphans} pre-existing orphan(s) to be deleted may be created or called by a framework at runtime (see the [REVIEW] entries) — confirm they are really unused (add a keep pattern if not), or pass --delete-review-orphans to delete them as-is; nothing was deleted");
                        return 1;
                    }
                    if (report.VerificationErrors > 0)
                    {
                        error.WriteLine($"error: verification found {report.VerificationErrors} new compile error(s) in the deletion plan — nothing was deleted (see the VERIFY warnings; --no-verify overrides)");
                        return 1;
                    }
                    var deleteExit = Editor.Delete(report, log);
                    if (deleteExit != 0) { return deleteExit; }
                    break;
            }
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException
            or Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    static CliOptions Parse(string[] args)
    {
        var opts = new CliOptions();
        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            opts.Mode = args[0].ToLowerInvariant();
            if (opts.Mode is not ("analyze" or "mark" or "delete" or "find"))
            {
                throw new ArgumentException($"unknown mode '{args[0]}' (expected analyze | mark | delete | find)");
            }
            i = 1;
        }
        for (; i < args.Length; i++)
        {
            string Next()
            {
                if (++i < args.Length) { return args[i]; }
                throw new ArgumentException($"missing value after {args[i - 1]}");
            }
            switch (args[i])
            {
                case "--solution" or "-s": opts.SolutionPath = Path.GetFullPath(Next()); break;
                case "--remove" or "-r": opts.RemoveTargets.Add(Next()); break;
                case "--find": opts.FindSubstring = Next(); break;
                case "--json": opts.JsonPath = Path.GetFullPath(Next()); break;
                case "--config": opts.ConfigPath = Path.GetFullPath(Next()); break;
                case "--strict-members": opts.StrictMembers = true; break;
                case "--no-verify": opts.NoVerify = true; break;
                case "--allow-still-referenced": opts.AllowStillReferenced = true; break;
                case "--allow-unregistered-services": opts.AllowUnregisteredServices = true; break;
                case "--allow-compile-errors": opts.AllowCompileErrors = true; break;
                case "--delete-review-tests": opts.DeleteReviewTests = true; break;
                case "--allow-no-roots": opts.AllowNoRoots = true; break;
                case "--delete-review-orphans": opts.DeleteReviewOrphans = true; break;
                case "--allow-website-projects": opts.AllowWebsiteProjects = true; break;
                case "--verbose" or "-v": opts.Verbose = true; break;
                default: throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        if (opts.SolutionPath == null) { throw new ArgumentException("--solution is required"); }
        if (opts.Mode == "find" && opts.FindSubstring == null)
        {
            throw new ArgumentException("find mode requires --find <substring>");
        }
        if (opts.Mode is "mark" or "delete" && opts.RemoveTargets.Count == 0)
        {
            throw new ArgumentException($"{opts.Mode} mode requires at least one --remove target");
        }
        return opts;
    }

    static void PrintUsage(TextWriter output)
    {
        output.WriteLine("""
            Usage:
              DeadCodeCleaner analyze --solution <path.sln> --remove <FQN> [--remove <FQN>]... [options]
              DeadCodeCleaner mark    --solution <path.sln> --remove <FQN> ...   (insert '// todo: to be deleted' markers)
              DeadCodeCleaner delete  --solution <path.sln> --remove <FQN> ...   (delete the code and markers)
              DeadCodeCleaner find    --solution <path.sln> --find <substring>   (look up symbol names)

            Options:
              --json <path>           where to write the JSON report (default ./deadcode-report.json)
              --config <path>         JSON config: { "keep": ["Ns.Type*", ...], "strictMembers": false }
              --strict-members        do not auto-keep public properties / enum members of in-use types
              --no-verify             skip the in-memory rebuild that checks the plan compiles
              --allow-still-referenced  let delete run although surviving code still references a removal target
              --allow-unregistered-services  let delete run although a still-used service loses its only DI registration
              --allow-compile-errors  let delete run although some projects failed to compile during analysis
              --delete-review-tests   delete review-flagged tests too (default: delete refuses while any exist)
              --allow-no-roots        let delete run although no production entry points were detected
              --delete-review-orphans delete review-flagged pre-existing orphans (possibly framework-created) too
              --allow-website-projects  let delete run although the solution holds ASP.NET Web Site projects
              --verbose               print full lists instead of the first 100

            The FQN format is Namespace.Type.Member without parameters, e.g.
              Roar.Infrastructure.WebBase.Controllers.AdminController.GetRoleActionList

            Exit codes: 0 success; 1 refused/rolled back (nothing half-applied); 2 bad usage/environment;
              3 delete applied, but some csproj/sln steps need manual completion (see WARNINGs).
            """);
    }
}
