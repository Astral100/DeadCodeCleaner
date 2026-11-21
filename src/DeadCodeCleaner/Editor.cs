using System.Text;
using System.Text.RegularExpressions;

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
            var text = ReadFileText(file, out var encoding);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            if (FirstLine(text).Trim().StartsWith(Constants.Marker, StringComparison.Ordinal)) continue;
            WriteFileText(file, Constants.Marker + nl + text, encoding);
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
            var text = ReadFileText(file, out var encoding);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = SplitLines(text, out bool trailingNewline);
            int inserted = 0;
            foreach (var (lineNumber, suffix) in linesToMark.Reverse())
            {
                int idx = lineNumber - 1;
                if (idx < 0 || idx >= lines.Count) continue;
                if (idx > 0 && lines[idx - 1].Trim().StartsWith(Constants.Marker, StringComparison.Ordinal)) continue;
                var indent = lines[idx][..(lines[idx].Length - lines[idx].TrimStart().Length)];
                lines.Insert(idx, indent + Constants.Marker + suffix);
                inserted++;
            }
            if (inserted == 0) continue;
            WriteFileText(file, JoinLines(lines, nl, trailingNewline), encoding);
            files++; markers += inserted;
        }

        // csproj markers: references to remove, and fully deletable projects.
        foreach (var rr in report.PackageRefRemovals)
        {
            var n = MarkXmlElements(rr.CsprojPath, "PackageReference", el => IsPackageRefElement(el, rr.Reference));
            if (n > 0) { markers += n; files++; }
        }
        foreach (var rr in report.ProjectRefRemovals)
        {
            var n = MarkXmlElements(rr.CsprojPath, "ProjectReference", el => IsProjectRefElement(el, rr.Reference));
            if (n > 0) { markers += n; files++; }
        }
        foreach (var pd in report.DeletableProjects)
        {
            var text = ReadFileText(pd.CsprojPath, out var encoding);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = SplitLines(text, out bool trailingNewline);
            // Keep an XML declaration ("<?xml ...?>") on the first line: legacy csprojs have one,
            // and anything before it makes the file invalid XML.
            int at = lines.Count > 0 && lines[0].TrimStart().StartsWith("<?xml", StringComparison.Ordinal) ? 1 : 0;
            if (at < lines.Count && lines[at].Trim().StartsWith(Constants.XmlMarkerPrefix, StringComparison.Ordinal)) continue;
            lines.Insert(at, Constants.XmlMarker);
            WriteFileText(pd.CsprojPath, JoinLines(lines, nl, trailingNewline), encoding);
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
        void Ensure(string file)
        {
            if (report.DeletableFiles.Contains(file)) return;
            if (!perFile.ContainsKey(file)) perFile[file] = new List<(int, int)>();
        }
        foreach (var sym in report.Symbols.Where(s => !s.InDeletableFile))
            foreach (var p in sym.Parts)
                Range(p.File, p.SpanStart, p.SpanStart + p.SpanLength);
        foreach (var st in report.Statements) Ensure(st.File);
        foreach (var ur in report.UsingRemovals) Ensure(ur.File);

        // Case-insensitive lookups so a casing difference between a symbol part and a statement
        // in the same file can never skip a deletion.
        var statementsByFile = report.Statements
            .GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var usingsByFile = report.UsingRemovals
            .GroupBy(u => u.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var file in perFile.Keys.ToList())
        {
            var text = ReadFileText(file, out var encoding);
            var ranges = perFile[file];
            var starts = LineStarts(text);
            if (statementsByFile.TryGetValue(file, out var sts))
            {
                foreach (var st in sts)
                {
                    ranges.Add((st.SpanStart, st.SpanStart + st.SpanLength));
                    // A whole-statement span already covers its marker (leading trivia); a chain
                    // segment does not, so its marker line is removed explicitly.
                    if (!st.WholeStatement) AddMarkerLineAbove(st.StartLine, text, starts, ranges);
                }
            }
            if (usingsByFile.TryGetValue(file, out var us))
            {
                foreach (var ur in us) AddLineRange(ur.Line, ur.Line, text, starts, ranges);
            }
            ApplyDeletions(file, text, ranges, encoding, ref regions);
        }

        // csproj cleanup: packages, project references, whole projects.
        foreach (var rr in report.PackageRefRemovals)
        {
            var n = RemoveXmlElements(rr.CsprojPath, "PackageReference", el => IsPackageRefElement(el, rr.Reference));
            if (n > 0) { regions += n; log($"  removed PackageReference {rr.Reference} from {rr.Project}"); }
        }
        foreach (var rr in report.ProjectRefRemovals)
        {
            var n = RemoveXmlElements(rr.CsprojPath, "ProjectReference", el => IsProjectRefElement(el, rr.Reference));
            if (n > 0) { regions += n; log($"  removed ProjectReference {rr.Reference} from {rr.Project}"); }
        }

        foreach (var pd in report.DeletableProjects)
        {
            if (!RemoveProjectFromSolution(report.Solution, pd.CsprojPath, pd.Project, log))
            {
                log($"  WARNING: {pd.Project} left on disk — remove it from the solution manually, then delete {pd.CsprojPath}");
                continue;
            }
            if (File.Exists(pd.CsprojPath)) { File.Delete(pd.CsprojPath); deletedFiles++; }
            TryRemoveProjectDirectory(pd.CsprojPath, log);
        }

        log($"Deleted: {deletedFiles} file(s), {regions} code region(s)/reference(s), {report.DeletableProjects.Count} project(s).");
        log("Build the solution to verify. (Usings that would no longer compile were removed; merely-unused ones are left for IDE cleanup.)");
    }

    static bool RemoveProjectFromSolution(string solution, string csprojPath, string projectName, Action<string> log)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"sln \"{solution}\" remove \"{csprojPath}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(Constants.SlnRemoveTimeoutMs))
            {
                proc.Kill(entireProcessTree: true);
                log($"  WARNING: dotnet sln remove timed out for {projectName}");
                return false;
            }
            if (proc.ExitCode == 0)
            {
                log($"  removed {projectName} from the solution");
                return true;
            }
            log($"  WARNING: dotnet sln remove failed for {projectName} (exit code {proc.ExitCode})");
            return false;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            log($"  WARNING: dotnet sln remove failed for {projectName}: {e.Message}");
            return false;
        }
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

    static bool IsPackageRefElement(string elementText, string package)
    {
        return elementText.Contains($"Include=\"{package}\"");
    }

    static bool IsProjectRefElement(string elementText, string projectName)
    {
        // Exact filename match: a reference to "Core" must not match "Common.Core.csproj".
        var m = Regex.Match(elementText, "Include=\"([^\"]+)\"");
        if (!m.Success) return false;
        var fileName = Path.GetFileName(m.Groups[1].Value.Replace('\\', '/'));
        return StringComparer.OrdinalIgnoreCase.Equals(fileName, projectName + ".csproj");
    }

    /// <summary>Line ranges (inclusive) of every element named <paramref name="elementName"/> whose
    /// full text matches. Handles single-line, multi-line and multi-line-attribute elements.</summary>
    static List<(int From, int To)> FindXmlElements(List<string> lines, string elementName, Func<string, bool> match)
    {
        var found = new List<(int, int)>();
        var open = "<" + elementName;
        var close = "</" + elementName + ">";
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Contains(open)) continue;
            int to = i;
            if (!lines[i].Contains("/>") && !lines[i].Contains(close))
            {
                while (to < lines.Count - 1 && !lines[to].Contains("/>") && !lines[to].Contains(close)) to++;
            }
            var elementText = string.Join(" ", lines.GetRange(i, to - i + 1));
            if (match(elementText)) found.Add((i, to));
            i = to;
        }
        return found;
    }

    static int MarkXmlElements(string file, string elementName, Func<string, bool> match)
    {
        if (!File.Exists(file)) return 0;
        var text = ReadFileText(file, out var encoding);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(text, out bool trailingNewline);
        var found = FindXmlElements(lines, elementName, match);
        int inserted = 0;
        for (int fi = found.Count - 1; fi >= 0; fi--)
        {
            var (from, _) = found[fi];
            if (from > 0 && lines[from - 1].Trim().StartsWith(Constants.XmlMarkerPrefix, StringComparison.Ordinal)) continue;
            var indent = lines[from][..(lines[from].Length - lines[from].TrimStart().Length)];
            lines.Insert(from, indent + Constants.XmlMarker);
            inserted++;
        }
        if (inserted > 0) WriteFileText(file, JoinLines(lines, nl, trailingNewline), encoding);
        return inserted;
    }

    static int RemoveXmlElements(string file, string elementName, Func<string, bool> match)
    {
        if (!File.Exists(file)) return 0;
        var text = ReadFileText(file, out var encoding);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLines(text, out bool trailingNewline);
        var found = FindXmlElements(lines, elementName, match);
        int removed = 0;
        for (int fi = found.Count - 1; fi >= 0; fi--)
        {
            var (from, to) = found[fi];
            if (from > 0 && lines[from - 1].Trim().StartsWith(Constants.XmlMarkerPrefix, StringComparison.Ordinal)) from--;
            lines.RemoveRange(from, to - from + 1);
            removed++;
        }
        if (removed > 0) WriteFileText(file, JoinLines(lines, nl, trailingNewline), encoding);
        return removed;
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
            if (above.Trim().StartsWith(Constants.Marker, StringComparison.Ordinal)) s--;
        }
        int startOffset = starts[s - 1];
        int endOffset = e < lineCount ? starts[e] : text.Length;
        ranges.Add((startOffset, endOffset));
    }

    static void AddMarkerLineAbove(int line, string text, int[] starts, List<(int, int)> ranges)
    {
        if (line < 2 || line - 1 > starts.Length) return;
        int aboveStart = starts[line - 2];
        int aboveEnd = line - 1 < starts.Length ? starts[line - 1] : text.Length;
        var above = text[aboveStart..aboveEnd];
        if (above.Trim().StartsWith(Constants.Marker, StringComparison.Ordinal)) ranges.Add((aboveStart, aboveEnd));
    }

    static void ApplyDeletions(string file, string text, List<(int Start, int End)> ranges, Encoding encoding, ref int regions)
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
        WriteFileText(file, sb.ToString(), encoding);
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

    /// <summary>Reads a file preserving its encoding for the write-back: BOMs are honoured, BOM-less
    /// files are decoded as strict UTF-8, and invalid UTF-8 falls back to Latin-1 (byte-preserving)
    /// instead of silently inserting U+FFFD replacement characters.</summary>
    static string ReadFileText(string path, out Encoding encoding)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            var text = strict.GetString(bytes);
            encoding = strict;
            return text;
        }
        catch (DecoderFallbackException)
        {
            encoding = Encoding.Latin1;
            return Encoding.Latin1.GetString(bytes);
        }
    }

    static void WriteFileText(string path, string text, Encoding encoding)
    {
        File.WriteAllText(path, text, encoding);
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

    /// <summary>Splits into lines without a phantom empty trailing element; JoinLines restores the
    /// exact trailing-newline state, so split+join round-trips unchanged text byte-for-byte.</summary>
    static List<string> SplitLines(string text, out bool trailingNewline)
    {
        trailingNewline = text.EndsWith("\n");
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (trailingNewline && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    static string JoinLines(List<string> lines, string nl, bool trailingNewline)
    {
        return string.Join(nl, lines) + (trailingNewline ? nl : "");
    }

    static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n' && i + 1 < text.Length) starts.Add(i + 1);
        return starts.ToArray();
    }
}
