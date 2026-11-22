using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadCodeCleaner;

public static class ReportWriter
{
    public static void WriteConsole(DeletionReport report, bool verbose)
    {
        int limit = verbose ? int.MaxValue : Constants.ConsoleListLimit;
        Console.WriteLine();
        Console.WriteLine("================ DeadCodeCleaner report ================");
        Console.WriteLine($"Solution : {report.Solution}");
        Console.WriteLine($"Removing : {string.Join(", ", report.RemoveTargets)}");
        var s = report.Stats;
        Console.WriteLine($"Graph    : {s.Nodes} symbols, {s.Edges} references, {s.Rules} rules; {s.ProductionRoots} production roots, {s.TestRoots} test roots");
        Console.WriteLine($"In use   : {s.InUseBaseline} baseline -> {s.InUseAfterRemoval} after removal ({s.AlreadyOrphanedBefore} were already orphaned before and are left untouched)");

        Section("Files deletable in full", report.DeletableFiles.Count);
        foreach (var f in report.DeletableFiles.Take(limit)) { Console.WriteLine($"  {f}"); }
        More(report.DeletableFiles.Count, limit);

        PrintCategory(report, Constants.CategoryRemoved, "Requested removals", limit);
        PrintCategory(report, Constants.CategoryOrphaned, "Newly orphaned production code", limit);
        PrintCategory(report, Constants.CategoryBrokenOrphan, "Pre-existing orphans deleted (they reference deleted code)", limit);
        PrintCategory(report, Constants.CategoryBrokenTest, "Tests referencing deleted code (deleted too)", limit);
        PrintCategory(report, Constants.CategoryOrphanedTestHelper, "Test helpers left without callers", limit);

        Section("Statements to remove (DI registrations, mappings)", report.Statements.Count);
        foreach (var st in report.Statements.Take(limit))
        {
            Console.WriteLine($"  {st.File}:{st.StartLine}  [{st.Kind}]  {st.Snippet}");
        }
        More(report.Statements.Count, limit);

        if (report.DeletableProjects.Count > 0)
        {
            Section("Projects deletable in full (removed from solution)", report.DeletableProjects.Count);
            foreach (var p in report.DeletableProjects) { Console.WriteLine($"  {p.Project}  ({p.CsprojPath})"); }
        }
        if (report.PackageRefRemovals.Count > 0)
        {
            Section("NuGet package references to remove", report.PackageRefRemovals.Count);
            foreach (var r in report.PackageRefRemovals) { Console.WriteLine($"  {r.Project}: {r.Reference}"); }
        }
        if (report.ProjectRefRemovals.Count > 0)
        {
            Section("Project references to remove", report.ProjectRefRemovals.Count);
            foreach (var r in report.ProjectRefRemovals) { Console.WriteLine($"  {r.Project}: -> {r.Reference}"); }
        }
        if (report.CompileEntryRemovals.Count > 0)
        {
            Section("Old-style <Compile> entries to remove (their files are deleted)", report.CompileEntryRemovals.Count);
            foreach (var r in report.CompileEntryRemovals) { Console.WriteLine($"  {r.Project}: {r.Reference}"); }
        }
        if (report.UsingRemovals.Count > 0)
        {
            Section("Using directives to remove (their assemblies are going away)", report.UsingRemovals.Count);
            foreach (var u in report.UsingRemovals.Take(limit))
            {
                Console.WriteLine($"  {u.File}:{u.Line}  using {u.Namespace};");
            }
            More(report.UsingRemovals.Count, limit);
        }

        if (report.Warnings.Count > 0)
        {
            Section("Warnings", report.Warnings.Count);
            bool Critical(string w)
            {
                return w.StartsWith("VERIFY", StringComparison.Ordinal) || w.StartsWith("STILL REFERENCED", StringComparison.Ordinal);
            }
            foreach (var w in report.Warnings.Take(limit)) { Console.WriteLine($"  {w}"); }
            // VERIFY and STILL REFERENCED lines are the ones that make a plan unsafe: they are
            // printed even past the cap, so the cap can never hide them.
            var rest = report.Warnings.Skip(limit).ToList();
            foreach (var w in rest.Where(Critical)) { Console.WriteLine($"  {w}"); }
            int hidden = rest.Count(w => !Critical(w));
            if (hidden > 0) { Console.WriteLine($"  ... and {hidden} more (use --verbose or the JSON report)"); }
        }
        Console.WriteLine("=======================================================");
    }

    static void PrintCategory(DeletionReport report, string category, string title, int limit)
    {
        var items = report.Symbols.Where(x => x.Category == category).ToList();
        if (items.Count == 0 && category != Constants.CategoryOrphaned) { return; }
        Section(title, items.Count);
        foreach (var item in items.Take(limit))
        {
            var p = item.Parts.FirstOrDefault();
            var where = p == null ? "" : $"  ({p.File}:{p.StartLine})";
            var review = item.Review ? $"  [REVIEW: {item.ReviewNote}]" : "";
            var whole = item.InDeletableFile ? "  [whole file]" : "";
            Console.WriteLine($"  {item.Kind,-11} {item.Fqn}{where}{whole}{review}");
        }
        More(items.Count, limit);
    }

    static void Section(string title, int count)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title}: {count}");
    }

    static void More(int total, int limit)
    {
        if (total > limit) { Console.WriteLine($"  ... and {total - limit} more (use --verbose or the JSON report)"); }
    }

    public static void WriteJson(DeletionReport report, string path)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, options));
        Console.WriteLine($"JSON report written to {path}");
    }
}
