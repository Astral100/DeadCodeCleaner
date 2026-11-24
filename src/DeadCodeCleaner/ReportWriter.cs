using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadCodeCleaner;

public static class ReportWriter
{
    public static void WriteConsole(DeletionReport report, bool verbose, TextWriter output)
    {
        int limit = verbose ? int.MaxValue : Constants.ConsoleListLimit;
        output.WriteLine();
        output.WriteLine("================ DeadCodeCleaner report ================");
        output.WriteLine($"Solution : {report.Solution}");
        output.WriteLine($"Removing : {string.Join(", ", report.RemoveTargets)}");
        var s = report.Stats;
        output.WriteLine($"Graph    : {s.Nodes} symbols, {s.Edges} references, {s.Rules} rules; {s.ProductionRoots} production roots, {s.TestRoots} test roots");
        output.WriteLine($"In use   : {s.InUseBaseline} baseline -> {s.InUseAfterRemoval} after removal ({s.AlreadyOrphanedBefore} were already orphaned before and are left untouched)");

        Section(output, "Files deletable in full", report.DeletableFiles.Count);
        foreach (var f in report.DeletableFiles.Take(limit)) { output.WriteLine($"  {f}"); }
        More(output, report.DeletableFiles.Count, limit);

        PrintCategory(output, report, Constants.CategoryRemoved, "Requested removals", limit);
        PrintCategory(output, report, Constants.CategoryOrphaned, "Newly orphaned production code", limit);
        PrintCategory(output, report, Constants.CategoryBrokenOrphan, "Pre-existing orphans deleted (they reference deleted code)", limit);
        PrintCategory(output, report, Constants.CategoryBrokenTest, "Tests referencing deleted code (deleted too)", limit);
        PrintCategory(output, report, Constants.CategoryOrphanedTestHelper, "Test helpers left without callers", limit);

        Section(output, "Statements to remove (DI registrations, mappings)", report.Statements.Count);
        foreach (var st in report.Statements.Take(limit))
        {
            output.WriteLine($"  {st.File}:{st.StartLine}  [{st.Kind}]  {st.Snippet}");
        }
        More(output, report.Statements.Count, limit);

        if (report.DeletableProjects.Count > 0)
        {
            Section(output, "Projects deletable in full (removed from solution)", report.DeletableProjects.Count);
            foreach (var p in report.DeletableProjects) { output.WriteLine($"  {p.Project}  ({p.CsprojPath})"); }
        }
        if (report.PackageRefRemovals.Count > 0)
        {
            Section(output, "NuGet package references to remove", report.PackageRefRemovals.Count);
            foreach (var r in report.PackageRefRemovals) { output.WriteLine($"  {r.Project}: {r.Reference}"); }
        }
        if (report.ProjectRefRemovals.Count > 0)
        {
            Section(output, "Project references to remove", report.ProjectRefRemovals.Count);
            foreach (var r in report.ProjectRefRemovals) { output.WriteLine($"  {r.Project}: -> {r.Reference}"); }
        }
        if (report.CompileEntryRemovals.Count > 0)
        {
            Section(output, "Old-style <Compile> entries to remove (their files are deleted)", report.CompileEntryRemovals.Count);
            foreach (var r in report.CompileEntryRemovals) { output.WriteLine($"  {r.Project}: {r.Reference}"); }
        }
        if (report.UsingRemovals.Count > 0)
        {
            Section(output, "Using directives to remove (their assemblies are going away)", report.UsingRemovals.Count);
            foreach (var u in report.UsingRemovals.Take(limit))
            {
                output.WriteLine($"  {u.File}:{u.Line}  using {u.Namespace};");
            }
            More(output, report.UsingRemovals.Count, limit);
        }

        if (report.Warnings.Count > 0)
        {
            Section(output, "Warnings", report.Warnings.Count);
            bool Critical(string w)
            {
                return w.StartsWith("VERIFY", StringComparison.Ordinal) || w.StartsWith("STILL REFERENCED", StringComparison.Ordinal)
                    || w.StartsWith("registration deleted although still in use", StringComparison.Ordinal)
                    || w.StartsWith(Constants.ServiceLeftUnregisteredPrefix, StringComparison.Ordinal);
            }
            foreach (var w in report.Warnings.Take(limit)) { output.WriteLine($"  {w}"); }
            // VERIFY and STILL REFERENCED lines are the ones that make a plan unsafe: they are
            // printed even past the cap, so the cap can never hide them.
            var rest = report.Warnings.Skip(limit).ToList();
            foreach (var w in rest.Where(Critical)) { output.WriteLine($"  {w}"); }
            int hidden = rest.Count(w => !Critical(w));
            if (hidden > 0) { output.WriteLine($"  ... and {hidden} more (use --verbose or the JSON report)"); }
        }
        output.WriteLine("=======================================================");
    }

    static void PrintCategory(TextWriter output, DeletionReport report, string category, string title, int limit)
    {
        var items = report.Symbols.Where(x => x.Category == category).ToList();
        if (items.Count == 0 && category != Constants.CategoryOrphaned) { return; }
        Section(output, title, items.Count);
        foreach (var item in items.Take(limit))
        {
            var p = item.Parts.FirstOrDefault();
            var where = p != null ? $"  ({p.File}:{p.StartLine})" : item.CoveredBy != null ? $"  (deleted with {item.CoveredBy})" : "";
            var review = item.Review ? $"  [REVIEW: {item.ReviewNote}]" : "";
            var whole = item.InDeletableFile ? "  [whole file]" : "";
            output.WriteLine($"  {item.Kind,-11} {item.Fqn}{where}{whole}{review}");
        }
        More(output, items.Count, limit);
    }

    static void Section(TextWriter output, string title, int count)
    {
        output.WriteLine();
        output.WriteLine($"--- {title}: {count}");
    }

    static void More(TextWriter output, int total, int limit)
    {
        if (total > limit) { output.WriteLine($"  ... and {total - limit} more (use --verbose or the JSON report)"); }
    }

    public static void WriteJson(DeletionReport report, string path, TextWriter output)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, options));
        output.WriteLine($"JSON report written to {path}");
    }
}
