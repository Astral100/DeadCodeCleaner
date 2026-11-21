using System.Text;

namespace DeadCodeCleaner;

/// <summary>Applies a DeletionReport to the source tree: inserts "to be deleted" markers, or
/// performs the deletions (removing markers along with the code).</summary>
public static class Editor
{
    // ------------------------------------------------------------------ mark

    public static void Mark(DeletionReport report, Action<string> log)
    {
        int files = 0, markers = 0;

        foreach (var file in report.DeletableFiles)
        {
            var text = File.ReadAllText(file);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            if (FirstLine(text).Trim().StartsWith(Constants.Marker)) continue;
            File.WriteAllText(file, Constants.Marker + nl + text);
            files++; markers++;
        }

        // line -> marker suffix ("" for plain; review notes carry the reason)
        var perFile = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        void Plan(string file, int line, string suffix)
        {
            if (report.DeletableFiles.Contains(file)) return;
            if (!perFile.TryGetValue(file, out var set)) perFile[file] = set = new SortedDictionary<int, string>();
            if (!set.TryGetValue(line, out var existing) || existing.Length < suffix.Length)
                set[line] = suffix;
        }
        foreach (var sym in report.Symbols.Where(s => !s.InDeletableFile))
        {
            var suffix = sym.Review && sym.ReviewNote != null
                ? $" (review: {Truncate(sym.ReviewNote, Constants.ReviewNoteMaxLength)})" : "";
            foreach (var p in sym.Parts) Plan(p.File, p.StartLine, suffix);
        }
        foreach (var st in report.Statements) Plan(st.File, st.StartLine, "");
        foreach (var ur in report.UsingRemovals) Plan(ur.File, ur.Line, "");

        foreach (var (file, linesToMark) in perFile)
        {
            var text = File.ReadAllText(file);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = SplitLines(text);
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (linesToMark.TryGetValue(i + 1, out var suffix)
                    && (i == 0 || !lines[i - 1].Trim().StartsWith(Constants.Marker)))
                {
                    var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                    sb.Append(indent).Append(Constants.Marker).Append(suffix).Append(nl);
                    markers++;
                }
                sb.Append(lines[i]);
                if (i < lines.Count - 1 || text.EndsWith("\n")) sb.Append(nl);
            }
            File.WriteAllText(file, sb.ToString());
            files++;
        }

        // csproj markers: references to remove, and fully deletable projects.
        foreach (var rr in report.PackageRefRemovals)
            if (MarkXmlLine(rr.CsprojPath, l => IsPackageRefLine(l, rr.Reference))) { markers++; files++; }
        foreach (var rr in report.ProjectRefRemovals)
            if (MarkXmlLine(rr.CsprojPath, l => IsProjectRefLine(l, rr.Reference))) { markers++; files++; }
        foreach (var pd in report.DeletableProjects)
        {
            var text = File.ReadAllText(pd.CsprojPath);
            if (FirstLine(text).Trim().StartsWith(Constants.XmlMarkerPrefix)) continue;
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            File.WriteAllText(pd.CsprojPath, Constants.XmlMarker + nl + text);
            markers++; files++;
        }

        log($"Marked: {markers} marker(s) across {files} file edit(s).");
    }

    // ---------------------------------------------------------------- delete

    public static void Delete(DeletionReport report, Action<string> log)
    {
        int deletedFiles = 0, regions = 0;

        foreach (var file in report.DeletableFiles)
        {
            if (File.Exists(file)) { File.Delete(file); deletedFiles++; }
        }

        var perFile = new Dictionary<string, List<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        void Range(string file, int start, int end)
        {
            if (report.DeletableFiles.Contains(file)) return;
            if (!perFile.TryGetValue(file, out var list)) perFile[file] = list = new List<(int, int)>();
            list.Add((start, end));
        }
        foreach (var sym in report.Symbols.Where(s => !s.InDeletableFile))
            foreach (var p in sym.Parts)
                Range(p.File, p.SpanStart, p.SpanStart + p.SpanLength);
        foreach (var st in report.Statements)
            if (!report.DeletableFiles.Contains(st.File) && !perFile.ContainsKey(st.File))
                perFile[st.File] = new List<(int, int)>();
        foreach (var ur in report.UsingRemovals)
            if (!report.DeletableFiles.Contains(ur.File) && !perFile.ContainsKey(ur.File))
                perFile[ur.File] = new List<(int, int)>();

        foreach (var file in perFile.Keys.ToList())
        {
            var text = File.ReadAllText(file);
            var ranges = perFile[file];
            var starts = LineStarts(text);
            foreach (var st in report.Statements.Where(s => s.File == file))
                AddLineRange(st.StartLine, st.EndLine, text, starts, ranges);
            foreach (var ur in report.UsingRemovals.Where(u => u.File == file))
                AddLineRange(ur.Line, ur.Line, text, starts, ranges);
            ApplyDeletions(file, text, ranges, ref regions);
        }

        // csproj cleanup: packages, project references, whole projects.
        foreach (var rr in report.PackageRefRemovals)
            if (RemoveXmlElement(rr.CsprojPath, l => IsPackageRefLine(l, rr.Reference), "</PackageReference>"))
            { regions++; log($"  removed PackageReference {rr.Reference} from {rr.Project}"); }
        foreach (var rr in report.ProjectRefRemovals)
            if (RemoveXmlElement(rr.CsprojPath, l => IsProjectRefLine(l, rr.Reference), "</ProjectReference>"))
            { regions++; log($"  removed ProjectReference {rr.Reference} from {rr.Project}"); }

        foreach (var pd in report.DeletableProjects)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("dotnet",
                    $"sln \"{report.Solution}\" remove \"{pd.CsprojPath}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(Constants.SlnRemoveTimeoutMs);
                if (proc is { ExitCode: 0 }) log($"  removed {pd.Project} from the solution");
                else log($"  WARNING: could not remove {pd.Project} from the solution automatically");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                log($"  WARNING: dotnet sln remove failed for {pd.Project}: {e.Message}");
            }
            if (File.Exists(pd.CsprojPath)) { File.Delete(pd.CsprojPath); deletedFiles++; }
            TryRemoveProjectDirectory(pd.CsprojPath, log);
        }

        log($"Deleted: {deletedFiles} file(s), {regions} code region(s)/reference(s), {report.DeletableProjects.Count} project(s).");
        log("Build the solution to verify. (Usings that would no longer compile were removed; merely-unused ones are left for IDE cleanup.)");
    }

    static void TryRemoveProjectDirectory(string csprojPath, Action<string> log)
    {
        var dir = Path.GetDirectoryName(csprojPath);
        if (dir == null || !Directory.Exists(dir)) return;
        foreach (var junk in new[] { "bin", "obj" })
        {
            var p = Path.Combine(dir, junk);
            if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
        }
        if (Directory.EnumerateFileSystemEntries(dir).Any())
        {
            log($"  NOTE: {dir} kept — it still contains non-code files");
            return;
        }
        Directory.Delete(dir);
    }

    // ----------------------------------------------------------- xml helpers

    static bool IsPackageRefLine(string line, string package)
    {
        return line.Contains("<PackageReference") && line.Contains($"Include=\"{package}\"");
    }

    static bool IsProjectRefLine(string line, string projectName)
    {
        return line.Contains("<ProjectReference") && line.Contains(projectName + ".csproj", StringComparison.OrdinalIgnoreCase);
    }

    static bool MarkXmlLine(string file, Func<string, bool> match)
    {
        if (!File.Exists(file)) return false;
        var text = File.ReadAllText(file);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(text);
        for (int i = 0; i < lines.Count; i++)
        {
            if (!match(lines[i])) continue;
            if (i > 0 && lines[i - 1].Trim().StartsWith(Constants.XmlMarkerPrefix)) return false;
            var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
            lines.Insert(i, indent + Constants.XmlMarker);
            File.WriteAllText(file, string.Join(nl, lines) + (text.EndsWith("\n") ? nl : ""));
            return true;
        }
        return false;
    }

    static bool RemoveXmlElement(string file, Func<string, bool> match, string closingTag)
    {
        if (!File.Exists(file)) return false;
        var text = File.ReadAllText(file);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(text);
        for (int i = 0; i < lines.Count; i++)
        {
            if (!match(lines[i])) continue;
            int from = i, to = i;
            if (!lines[i].Contains("/>"))
            {
                while (to < lines.Count - 1 && !lines[to].Contains(closingTag)) to++;
            }
            if (from > 0 && lines[from - 1].Trim().StartsWith(Constants.XmlMarkerPrefix)) from--;
            lines.RemoveRange(from, to - from + 1);
            File.WriteAllText(file, string.Join(nl, lines) + (text.EndsWith("\n") ? nl : ""));
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------- text helpers

    static void AddLineRange(int startLine, int endLine, string text, int[] starts, List<(int, int)> ranges)
    {
        int lineCount = starts.Length;
        int s = Math.Clamp(startLine, 1, lineCount);
        int e = Math.Clamp(endLine, 1, lineCount);
        if (s >= 2)
        {
            var above = text[starts[s - 2]..starts[s - 1]].TrimEnd('\r', '\n');
            if (above.Trim().StartsWith(Constants.Marker)) s--;
        }
        int startOffset = starts[s - 1];
        int endOffset = e < lineCount ? starts[e] : text.Length;
        ranges.Add((startOffset, endOffset));
    }

    static void ApplyDeletions(string file, string text, List<(int Start, int End)> ranges, ref int regions)
    {
        if (ranges.Count == 0) return;
        var merged = Merge(ranges);
        var sb = new StringBuilder(text);
        foreach (var (start, end) in merged.OrderByDescending(r => r.Start))
        {
            var s = Math.Clamp(start, 0, sb.Length);
            var e = Math.Clamp(end, s, sb.Length);
            sb.Remove(s, e - s);
            regions++;
        }
        File.WriteAllText(file, sb.ToString());
    }

    static List<(int Start, int End)> Merge(List<(int Start, int End)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var result = new List<(int, int)>();
        foreach (var r in sorted)
        {
            if (result.Count > 0 && r.Start <= result[^1].Item2)
            {
                var last = result[^1];
                result[^1] = (last.Item1, Math.Max(last.Item2, r.End));
            }
            else result.Add(r);
        }
        return result;
    }

    static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s[..(max - 3)] + "...";
    }

    static string FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        return idx < 0 ? text : text[..idx];
    }

    static List<string> SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Split('\n').ToList();
    }

    static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n' && i + 1 < text.Length) starts.Add(i + 1);
        return starts.ToArray();
    }
}
