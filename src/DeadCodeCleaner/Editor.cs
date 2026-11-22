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
        var deletableFiles = new HashSet<string>(report.DeletableFiles, StringComparer.OrdinalIgnoreCase);

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
            if (deletableFiles.Contains(file)) return;
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
            var starts = LineStarts(text);
            var sb = new StringBuilder(text);
            int inserted = 0;
            // Offset-based insertion (descending) leaves every untouched line byte-for-byte intact,
            // including files with mixed line endings.
            foreach (var (lineNumber, suffix) in linesToMark.Reverse())
            {
                int idx = lineNumber - 1;
                if (idx < 0 || idx >= starts.Length) continue;
                var lineText = LineAt(text, starts, idx);
                if (idx > 0 && LineAt(text, starts, idx - 1).Trim().StartsWith(Constants.Marker, StringComparison.Ordinal)) continue;
                var terminator = LineTerminator(lineText);
                if (terminator.Length == 0) terminator = nl;
                sb.Insert(starts[idx], Indent(lineText) + Constants.Marker + suffix + terminator);
                inserted++;
            }
            if (inserted == 0) continue;
            WriteFileText(file, sb.ToString(), encoding);
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
            var n = MarkXmlElements(rr.CsprojPath, "ProjectReference", el => IsProjectRefElement(el, rr.ReferenceFile, rr.Reference + ".csproj"));
            if (n > 0) { markers += n; files++; }
        }
        foreach (var rr in report.CompileEntryRemovals)
        {
            var n = MarkXmlElements(rr.CsprojPath, "Compile", el => IsIncludeMatch(el, rr.Reference));
            if (n > 0) { markers += n; files++; }
        }
        foreach (var pd in report.DeletableProjects)
        {
            var text = ReadFileText(pd.CsprojPath, out var encoding);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = SplitLinesKeepEnds(text);
            // Keep an XML declaration ("<?xml ...?>") on the first line: legacy csprojs have one,
            // and anything before it makes the file invalid XML.
            int at = lines.Count > 0 && lines[0].TrimStart().StartsWith("<?xml", StringComparison.Ordinal) ? 1 : 0;
            if (at < lines.Count && lines[at].Trim().StartsWith(Constants.XmlMarkerPrefix, StringComparison.Ordinal)) continue;
            lines.Insert(at, Constants.XmlMarker + nl);
            WriteFileText(pd.CsprojPath, string.Concat(lines), encoding);
            markers++; files++;
        }

        log($"Marked: {markers} marker(s) across {files} file edit(s).");
    }

    // ---------------------------------------------------------------- delete

    public static void Delete(DeletionReport report, Action<string> log)
    {
        int deletedFiles = 0, regions = 0;
        var deletableFiles = new HashSet<string>(report.DeletableFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var file in report.DeletableFiles)
        {
            if (File.Exists(file)) { File.Delete(file); deletedFiles++; }
        }

        // Char-span deletions per file, plus the lines whose marker (inserted by a previous mark
        // run) sits above a span and may fall outside it (mid-line anchors, chain segments).
        var perFile = new Dictionary<string, List<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        var markerLines = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        void Range(string file, int start, int end, int markerLine)
        {
            if (deletableFiles.Contains(file)) return;
            if (!perFile.TryGetValue(file, out var list)) perFile[file] = list = new List<(int, int)>();
            if (end > start) list.Add((start, end));
            if (!markerLines.TryGetValue(file, out var ml)) markerLines[file] = ml = new List<int>();
            ml.Add(markerLine);
        }
        foreach (var sym in report.Symbols.Where(s => !s.InDeletableFile))
            foreach (var p in sym.Parts)
                Range(p.File, p.SpanStart, p.SpanStart + p.SpanLength, p.StartLine);
        foreach (var st in report.Statements) Range(st.File, st.SpanStart, st.SpanStart + st.SpanLength, st.StartLine);
        foreach (var ur in report.UsingRemovals) Range(ur.File, ur.SpanStart, ur.SpanStart + ur.SpanLength, ur.Line);

        foreach (var file in perFile.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var text = ReadFileText(file, out var encoding);
            var ranges = perFile[file];
            if (ranges.Count > 0 && ranges.Max(r => r.End) > text.Length)
            {
                log($"  WARNING: {file} is shorter than the computed spans (encoding mismatch?) — skipped, delete manually");
                continue;
            }
            var starts = LineStarts(text);
            foreach (var line in markerLines[file]) AddMarkerLineAbove(line, text, starts, ranges);
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
            var n = RemoveXmlElements(rr.CsprojPath, "ProjectReference", el => IsProjectRefElement(el, rr.ReferenceFile, rr.Reference + ".csproj"));
            if (n > 0) { regions += n; log($"  removed ProjectReference {rr.Reference} from {rr.Project}"); }
        }
        foreach (var rr in report.CompileEntryRemovals)
        {
            var n = RemoveXmlElements(rr.CsprojPath, "Compile", el => IsIncludeMatch(el, rr.Reference));
            if (n > 0) { regions += n; log($"  removed Compile entry {rr.Reference} from {rr.Project}"); }
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
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("sln");
            psi.ArgumentList.Add(solution);
            psi.ArgumentList.Add("remove");
            psi.ArgumentList.Add(csprojPath);
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
        try
        {
            // Check exclusivity BEFORE deleting anything: a directory shared with another project
            // (or holding non-code files) is left alone, build output included.
            var leftovers = Directory.EnumerateFileSystemEntries(dir)
                .Where(e => !(Directory.Exists(e) && Path.GetFileName(e) is "bin" or "obj")).ToList();
            if (leftovers.Count > 0)
            {
                log($"  NOTE: {dir} kept — it still contains non-code files");
                return;
            }
            foreach (var junk in new[] { "bin", "obj" })
            {
                var p = Path.Combine(dir, junk);
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
            }
            Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"  WARNING: could not remove {dir}: {e.Message}");
        }
    }

    // ----------------------------------------------------------- xml helpers

    static bool IsPackageRefElement(string elementText, string package)
    {
        // NuGet ids are case-insensitive; attributes may use either quote style.
        return elementText.Contains($"Include=\"{package}\"", StringComparison.OrdinalIgnoreCase)
            || elementText.Contains($"Include='{package}'", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsProjectRefElement(string elementText, string includeValue, string fallbackFileName)
    {
        var m = Regex.Match(elementText, "Include=[\"']([^\"']+)[\"']");
        if (!m.Success) return false;
        var value = m.Groups[1].Value.Replace('\\', '/');
        // Full Include path match: two references can share a file name in different folders.
        if (includeValue != null) return StringComparer.OrdinalIgnoreCase.Equals(value, includeValue.Replace('\\', '/'));
        // Fallback (older reports): exact file name — "Core.csproj" must not match "Common.Core.csproj".
        return StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(value), fallbackFileName);
    }

    static bool IsIncludeMatch(string elementText, string includeValue)
    {
        var m = Regex.Match(elementText, "Include=[\"']([^\"']+)[\"']");
        if (!m.Success) return false;
        return StringComparer.OrdinalIgnoreCase.Equals(m.Groups[1].Value.Replace('\\', '/'), includeValue.Replace('\\', '/'));
    }

    /// <summary>Line ranges (inclusive) of every element named <paramref name="elementName"/> whose
    /// full text matches. The end of the OPENING tag is located first (its own '>'), so a
    /// self-closing child element never truncates a multi-line parent.</summary>
    static List<(int From, int To)> FindXmlElements(List<string> lines, string elementName, Func<string, bool> match)
    {
        var found = new List<(int, int)>();
        var open = "<" + elementName;
        var close = "</" + elementName + ">";
        // Comment state at the START of each line, so elements inside multi-line comments are skipped.
        var startsInComment = new bool[lines.Count];
        bool state = false;
        for (int i = 0; i < lines.Count; i++)
        {
            startsInComment[i] = state;
            state = XmlCommentStateAtEnd(lines[i], state);
        }
        for (int i = 0; i < lines.Count; i++)
        {
            int at = FindOpenTag(lines[i], open, startsInComment[i]);
            if (at < 0) continue;
            int line = i, col = at + open.Length, gt = -1;
            while (line < lines.Count)
            {
                gt = lines[line].IndexOf('>', col);
                if (gt >= 0) break;
                line++; col = 0;
            }
            if (gt < 0) break; // malformed: the opening tag never closes
            bool selfClosing = gt > 0 && lines[line][gt - 1] == '/';
            int to = line;
            if (!selfClosing)
            {
                int cline = line, ccol = gt;
                to = -1;
                while (cline < lines.Count)
                {
                    var ci = lines[cline].IndexOf(close, ccol, StringComparison.Ordinal);
                    if (ci >= 0) { to = cline; break; }
                    cline++; ccol = 0;
                }
                if (to < 0) break; // malformed: no close tag
            }
            var elementText = string.Join(" ", lines.GetRange(i, to - i + 1).Select(l => l.TrimEnd('\r', '\n')));
            if (match(elementText)) found.Add((i, to));
            i = to;
        }
        return found;
    }

    static int FindOpenTag(string lineText, string open, bool startsInComment)
    {
        var at = lineText.IndexOf(open, StringComparison.Ordinal);
        if (at < 0) return -1;
        // Reject prefixes of longer element names, and commented-out elements.
        int after = at + open.Length;
        if (after < lineText.Length && lineText[after] is not (' ' or '\t' or '>' or '/' or '\r' or '\n')) return -1;
        if (InXmlComment(lineText, at, startsInComment)) return -1;
        return at;
    }

    static bool InXmlComment(string lineText, int pos, bool startsInComment)
    {
        bool inComment = startsInComment;
        int i = 0;
        while (i < pos)
        {
            var token = inComment ? "-->" : "<!--";
            var next = lineText.IndexOf(token, i, StringComparison.Ordinal);
            if (next < 0 || next >= pos) break;
            inComment = !inComment;
            i = next + token.Length;
        }
        return inComment;
    }

    static bool XmlCommentStateAtEnd(string lineText, bool startsInComment)
    {
        return InXmlComment(lineText, lineText.Length, startsInComment);
    }

    static int MarkXmlElements(string file, string elementName, Func<string, bool> match)
    {
        if (!File.Exists(file)) return 0;
        var text = ReadFileText(file, out var encoding);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = SplitLinesKeepEnds(text);
        var found = FindXmlElements(lines, elementName, match);
        int inserted = 0;
        for (int fi = found.Count - 1; fi >= 0; fi--)
        {
            var (from, _) = found[fi];
            if (from > 0 && lines[from - 1].Trim().StartsWith(Constants.XmlMarkerPrefix, StringComparison.Ordinal)) continue;
            var terminator = LineTerminator(lines[from]);
            if (terminator.Length == 0) terminator = nl;
            lines.Insert(from, Indent(lines[from]) + Constants.XmlMarker + terminator);
            inserted++;
        }
        if (inserted > 0) WriteFileText(file, string.Concat(lines), encoding);
        return inserted;
    }

    static int RemoveXmlElements(string file, string elementName, Func<string, bool> match)
    {
        if (!File.Exists(file)) return 0;
        var text = ReadFileText(file, out var encoding);
        var lines = SplitLinesKeepEnds(text);
        var found = FindXmlElements(lines, elementName, match);
        int removed = 0;
        for (int fi = found.Count - 1; fi >= 0; fi--)
        {
            var (from, to) = found[fi];
            if (from > 0 && lines[from - 1].Trim().StartsWith(Constants.XmlMarkerPrefix, StringComparison.Ordinal)) from--;
            lines.RemoveRange(from, to - from + 1);
            removed++;
        }
        if (removed > 0) WriteFileText(file, string.Concat(lines), encoding);
        return removed;
    }

    // ---------------------------------------------------------- text helpers

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

    /// <summary>The line breaks Roslyn recognizes: analysis line numbers must map to the same lines.</summary>
    static bool IsLineBreak(char c)
    {
        return c is '\n' or '\r' or '\u0085' or '\u2028' or '\u2029';
    }

    static string FirstLine(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (IsLineBreak(text[i])) return text[..i];
        }
        return text;
    }

    /// <summary>Splits into lines, each KEEPING its own terminator, so that string.Concat
    /// round-trips the text byte-for-byte (mixed line endings included).</summary>
    static List<string> SplitLinesKeepEnds(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsLineBreak(text[i])) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    static string LineTerminator(string lineText)
    {
        if (lineText.EndsWith("\r\n")) return "\r\n";
        if (lineText.Length > 0 && IsLineBreak(lineText[^1])) return lineText[^1..];
        return "";
    }

    static string Indent(string lineText)
    {
        int w = 0;
        while (w < lineText.Length && (lineText[w] == ' ' || lineText[w] == '\t')) w++;
        return lineText[..w];
    }

    static string LineAt(string text, int[] starts, int idx)
    {
        int end = idx + 1 < starts.Length ? starts[idx + 1] : text.Length;
        return text[starts[idx]..end];
    }

    static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsLineBreak(text[i])) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (i + 1 < text.Length) starts.Add(i + 1);
        }
        return starts.ToArray();
    }
}
