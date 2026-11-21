using Microsoft.Build.Locator;

namespace DeadCodeCleaner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        return await Run(args);
    }

    static async Task<int> Run(string[] args)
    {
        CliOptions opts;
        try { opts = Parse(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            PrintUsage();
            return 2;
        }

        var config = new AnalyzerConfig { StrictMembers = opts.StrictMembers };
        if (opts.ConfigPath != null)
        {
            var loaded = System.Text.Json.JsonSerializer.Deserialize<AnalyzerConfig>(
                File.ReadAllText(opts.ConfigPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded != null)
            {
                loaded.StrictMembers |= opts.StrictMembers;
                config = loaded;
            }
        }

        void Log(string msg) => Console.WriteLine(msg);
        var engine = new Engine(opts, config, Log);

        try
        {
            var (report, graph) = await engine.AnalyzeAsync();

            if (opts.Mode == "find")
            {
                var needle = opts.FindSubstring ?? "";
                var matches = graph.Decl.Values
                    .Select(s => (Fqn: SymbolNames.Fqn(s), Symbol: s))
                    .Where(x => x.Fqn.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Fqn, StringComparer.Ordinal)
                    .Take(200)
                    .ToList();
                foreach (var (fqn, symbol) in matches)
                    Console.WriteLine($"{SymbolNames.Kind(symbol),-11} {fqn}");
                Console.WriteLine($"({matches.Count} match(es))");
                return 0;
            }

            ReportWriter.WriteConsole(report, opts.Verbose);
            var jsonPath = opts.JsonPath ?? Path.Combine(Environment.CurrentDirectory, "deadcode-report.json");
            ReportWriter.WriteJson(report, jsonPath);

            switch (opts.Mode)
            {
                case "mark":
                    Editor.Mark(report, Log);
                    break;
                case "delete":
                    Editor.Delete(report, Log);
                    break;
            }
            return 0;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    static CliOptions Parse(string[] args)
    {
        var opts = new CliOptions();
        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith("--"))
        {
            opts.Mode = args[0].ToLowerInvariant();
            if (opts.Mode is not ("analyze" or "mark" or "delete" or "find"))
                throw new ArgumentException($"unknown mode '{args[0]}' (expected analyze | mark | delete | find)");
            i = 1;
        }
        for (; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"missing value after {args[i - 1]}");
            switch (args[i])
            {
                case "--solution" or "-s": opts.SolutionPath = Path.GetFullPath(Next()); break;
                case "--remove" or "-r": opts.RemoveTargets.Add(Next()); break;
                case "--find": opts.FindSubstring = Next(); break;
                case "--json": opts.JsonPath = Path.GetFullPath(Next()); break;
                case "--config": opts.ConfigPath = Path.GetFullPath(Next()); break;
                case "--strict-members": opts.StrictMembers = true; break;
                case "--verbose" or "-v": opts.Verbose = true; break;
                default: throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        if (opts.SolutionPath == null) throw new ArgumentException("--solution is required");
        if (opts.Mode == "find" && opts.FindSubstring == null)
            throw new ArgumentException("find mode requires --find <substring>");
        if (opts.Mode is "mark" or "delete" && opts.RemoveTargets.Count == 0)
            throw new ArgumentException($"{opts.Mode} mode requires at least one --remove target");
        return opts;
    }

    static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              DeadCodeCleaner analyze --solution <path.sln> --remove <FQN> [--remove <FQN>]... [options]
              DeadCodeCleaner mark    --solution <path.sln> --remove <FQN> ...   (insert '// todo: to be deleted' markers)
              DeadCodeCleaner delete  --solution <path.sln> --remove <FQN> ...   (delete the code and markers)
              DeadCodeCleaner find    --solution <path.sln> --find <substring>   (look up symbol names)

            Options:
              --json <path>      where to write the JSON report (default ./deadcode-report.json)
              --config <path>    JSON config: { "keep": ["Ns.Type*", ...], "strictMembers": false }
              --strict-members   do not auto-keep public properties / enum members of in-use types
              --verbose          print full lists instead of the first 100

            The FQN format is Namespace.Type.Member without parameters, e.g.
              Roar.Infrastructure.WebBase.Controllers.AdminController.GetRoleActionList
            """);
    }
}
