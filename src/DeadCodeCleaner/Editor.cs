using Microsoft.CodeAnalysis;
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
        var lengths = CanonicalKeys(report.FileTextLengths);
        var hashes = CanonicalKeys(report.FileTextHashes);
        var deletableFiles = new HashSet<string>(report.DeletableFiles.Select(CanonicalPath), Constants.PathComparer);

        foreach (var file in deletableFiles.OrderBy(x => x, Constants.PathComparer))
        {
            if (!File.Exists(file))
            {
                log($"  WARNING: {file} does not exist on disk (source-generated?) — skipped");
                continue;
            }
            var text = ReadFileText(file, out var encoding);
            if (Stale(lengths, hashes, file, text, out var why))
            {
                log($"  WARNING: {file} {why} — skipped");
                continue;
            }
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            if (FirstLine(text).Trim().StartsWith(Constants.Marker, StringComparison.Ordinal)) { continue; }
            WriteFileText(file, Constants.Marker + nl + text, encoding);
            files++;
            markers++;
        }

        // line -> marker suffix ("" for plain; review notes carry the reason)
        var perFile = new Dictionary<string, SortedDictionary<int, string>>(Constants.PathComparer);
        void Plan(string fileRaw, int line, string suffix, bool insideToken, bool midLine = false)
        {
            var file = CanonicalPath(fileRaw);
            if (deletableFiles.Contains(file)) { return; }
            if (midLine)
            {
                log($"  WARNING: {file}:{line} — the code to delete starts after other code on the same line; a marker line would label that other code, so it is skipped (the deletion itself is unaffected)");
                return;
            }
            // The line begins inside a multi-line string that ends on it: an inserted marker line
            // would become part of the string. Skipped; delete mode needs no marker.
            if (insideToken)
            {
                log($"  WARNING: {file}:{line} starts inside a multi-line string literal — marker skipped, the deletion itself is unaffected");
                return;
            }
            if (!perFile.TryGetValue(file, out var set)) { perFile[file] = set = new SortedDictionary<int, string>(); }
            if (!set.TryGetValue(line, out var existing) || existing.Length < suffix.Length) { set[line] = suffix; }
        }
        foreach (var sym in report.Symbols.Where(s => !s.InDeletableFile))
        {
            var suffix = sym.Review && sym.ReviewNote != null
                ? $" (review: {Truncate(sym.ReviewNote, Constants.ReviewNoteMaxLength)})" : "";
            foreach (var p in sym.Parts) { Plan(p.File, p.StartLine, suffix, p.MarkerLineInsideToken, p.StartsMidLine); }
        }
        foreach (var st in report.Statements) { Plan(st.File, st.StartLine, "", st.MarkerLineInsideToken); }
        foreach (var ur in report.UsingRemovals) { Plan(ur.File, ur.Line, "", false); }

        foreach (var (file, linesToMark) in perFile)
        {
            if (!File.Exists(file))
            {
                log($"  WARNING: {file} does not exist on disk (source-generated?) — skipped");
                continue;
            }
            var text = ReadFileText(file, out var encoding);
            if (Stale(lengths, hashes, file, text, out var why))
            {
                log($"  WARNING: {file} {why} — skipped");
                continue;
            }
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var starts = LineStarts(text);
            var sb = new StringBuilder(text);
            int inserted = 0;
            // Offset-based insertion (descending) leaves every untouched line byte-for-byte intact,
            // including files with mixed line endings.
            foreach (var (lineNumber, suffix) in linesToMark.Reverse())
            {
                int idx = lineNumber - 1;
                if (idx < 0 || idx >= starts.Length) { continue; }
                var lineText = LineAt(text, starts, idx);
                if (idx > 0 && LineAt(text, starts, idx - 1).Trim().StartsWith(Constants.Marker, StringComparison.Ordinal)) { continue; }
                var terminator = LineTerminator(lineText);
                if (terminator.Length == 0) { terminator = nl; }
                sb.Insert(starts[idx], Indent(lineText) + Constants.Marker + suffix + terminator);
                inserted++;
            }
            if (inserted == 0) { continue; }
            WriteFileText(file, sb.ToString(), encoding);
            files++;
            markers += inserted;
        }

        // csproj markers: references to remove, and fully deletable projects.
        // Grouped per csproj: the file is validated against the analysis once, then every marker goes
        // in with a single write (a second validation after the first write would see "changed").
        var xmlMarks = new List<(string File, string Element, Func<string, bool> Match)>();
        foreach (var rr in report.PackageRefRemovals)
        {
            xmlMarks.Add((rr.CsprojPath, "PackageReference", el => IsPackageRefElement(el, rr.Reference)));
        }
        foreach (var rr in report.ProjectRefRemovals)
        {
            xmlMarks.Add((rr.CsprojPath, "ProjectReference", el => IsProjectRefElement(el, rr.ReferenceFile, rr.Reference + ".csproj")));
        }
        foreach (var rr in report.CompileEntryRemovals)
        {
            xmlMarks.Add((rr.CsprojPath, "Compile", el => IsIncludeMatch(el, rr.Reference)));
        }
        foreach (var group in xmlMarks.GroupBy(x => CanonicalPath(x.File), Constants.PathComparer).OrderBy(g => g.Key, Constants.PathComparer))
        {
            var n = MarkXmlElements(group.Key, group.Select(x => (x.Element, x.Match)).ToList(), lengths, hashes, log);
            if (n > 0) { markers += n; files++; }
        }
        foreach (var pd in report.DeletableProjects)
        {
            if (!File.Exists(pd.CsprojPath))
            {
                log($"  WARNING: {pd.CsprojPath} does not exist on disk — skipped");
                continue;
            }
            var text = ReadFileText(pd.CsprojPath, out var encoding);
            if (Stale(lengths, hashes, CanonicalPath(pd.CsprojPath), text, out var pwhy))
            {
                log($"  WARNING: {pd.CsprojPath} {pwhy} — skipped");
                continue;
            }
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = SplitLinesKeepEnds(text);
            // Keep an XML declaration ("<?xml ...?>") on the first line: legacy csprojs have one,
            // and anything before it makes the file invalid XML.
            int at = lines.Count > 0 && lines[0].TrimStart().StartsWith("<?xml", StringComparison.Ordinal) ? 1 : 0;
            if (at < lines.Count && lines[at].Trim().StartsWith(Constants.XmlMarkerPrefix, StringComparison.Ordinal)) { continue; }
            lines.Insert(at, Constants.XmlMarker + nl);
            WriteFileText(pd.CsprojPath, string.Concat(lines), encoding);
            markers++;
            files++;
        }

        log($"Marked: {markers} marker(s) across {files} file edit(s).");
    }

    // ---------------------------------------------------------------- delete

    /// <summary>Applies the plan. Returns 0 on full success, 1 when nothing was applied (a
    /// validation abort) or a failure forced a rollback, and 3 when the source edits applied but a
    /// csproj/sln step was left for manual action — so scripts never mistake either for success.</summary>
    public static int Delete(DeletionReport report, Action<string> log)
    {
        int deletedFiles = 0, regions = 0;
        bool manualSteps = false;
        var lengths = CanonicalKeys(report.FileTextLengths);
        var hashes = CanonicalKeys(report.FileTextHashes);
        var deletableFiles = new HashSet<string>(report.DeletableFiles.Select(CanonicalPath), Constants.PathComparer);

        // Char-span deletions per file, plus the lines whose marker (inserted by a previous mark
        // run) sits above a span and may fall outside it (mid-line anchors, chain segments).
        var perFile = new Dictionary<string, List<(int Start, int End)>>(Constants.PathComparer);
        var markerLines = new Dictionary<string, List<int>>(Constants.PathComparer);
        void Range(string fileRaw, int start, int end, int markerLine)
        {
            var file = CanonicalPath(fileRaw);
            if (deletableFiles.Contains(file)) { return; }
            if (!perFile.TryGetValue(file, out var list)) { perFile[file] = list = new List<(int, int)>(); }
            if (end > start) { list.Add((start, end)); }
            if (!markerLines.TryGetValue(file, out var ml)) { markerLines[file] = ml = new List<int>(); }
            ml.Add(markerLine);
        }
        foreach (var sym in report.Symbols.Where(s => !s.InDeletableFile))
        {
            foreach (var p in sym.Parts) { Range(p.File, p.SpanStart, p.SpanStart + p.SpanLength, p.StartLine); }
        }
        foreach (var st in report.Statements) { Range(st.File, st.SpanStart, st.SpanStart + st.SpanLength, st.StartLine); }
        foreach (var ur in report.UsingRemovals) { Range(ur.File, ur.SpanStart, ur.SpanStart + ur.SpanLength, ur.Line); }

        // Every span file is read and validated BEFORE anything touches the disk. Any failure is
        // FATAL: the verifier approved the plan as a whole, so applying it minus one file could
        // over-delete what that file still uses. Nothing is written until everything checks out.
        var pending = new List<(string File, string NewText, string OldText, Encoding Encoding, int Regions)>();
        foreach (var file in perFile.Keys.OrderBy(x => x, Constants.PathComparer))
        {
            if (!File.Exists(file))
            {
                log($"error: {file} no longer exists on disk — the analysis is stale; NOTHING was deleted, re-run delete");
                return 1;
            }
            var text = ReadFileText(file, out var encoding);
            if (Stale(lengths, hashes, file, text, out var why))
            {
                log($"error: {file} {why} — NOTHING was deleted");
                return 1;
            }
            var ranges = perFile[file];
            if (ranges.Count > 0 && ranges.Max(r => r.End) > text.Length)
            {
                log($"error: {file} is shorter than the computed spans (encoding mismatch?) — NOTHING was deleted");
                return 1;
            }
            var starts = LineStarts(text);
            foreach (var line in markerLines[file]) { AddMarkerLineAbove(line, text, starts, ranges); }
            var (newText, n) = ComputeDeletions(text, ranges);
            if (n > 0) { pending.Add((file, newText, text, encoding, n)); }
        }
        // Whole files are validated too: a file that changed since the analysis may hold code the
        // plan never accounted for. Their text is kept, so a failed deletion can restore them.
        var wholeFiles = new List<(string File, string OldText, Encoding Encoding)>();
        foreach (var file in deletableFiles.OrderBy(x => x, Constants.PathComparer))
        {
            if (!File.Exists(file))
            {
                // The verifier approved the plan as a whole: a vanished file means the world moved
                // under the analysis, and the remaining edits may no longer be safe together.
                log($"error: {file} no longer exists on disk — the analysis is stale; NOTHING was deleted, re-run delete");
                return 1;
            }
            var text = ReadFileText(file, out var wenc);
            if (Stale(lengths, hashes, file, text, out var why))
            {
                log($"error: {file} {why} — NOTHING was deleted");
                return 1;
            }
            wholeFiles.Add((file, text, wenc));
        }

        // Whole projects: the csproj must be the one analyzed, and no C# file may have appeared under
        // the project since — removing the project would strand code the plan never saw.
        foreach (var pd in report.DeletableProjects)
        {
            var cpath = CanonicalPath(pd.CsprojPath);
            if (File.Exists(cpath))
            {
                var ptext = ReadFileText(cpath, out _);
                if (Stale(lengths, hashes, cpath, ptext, out var pwhy))
                {
                    log($"error: {cpath} {pwhy} — NOTHING was deleted");
                    return 1;
                }
            }
            var dir = Path.GetDirectoryName(cpath);
            if (dir == null || !Directory.Exists(dir)) { continue; }
            var known = new HashSet<string>(pd.SourceFiles.Select(CanonicalPath), Constants.PathComparer);
            try
            {
                var added = EnumerateFilesPruned(dir)
                    .Where(f => Path.GetExtension(f) == ".cs" && !known.Contains(CanonicalPath(f)))
                    .OrderBy(f => f, Constants.PathComparer).FirstOrDefault();
                if (added != null)
                {
                    log($"error: {added} appeared in project {pd.Project} after the analysis — removing the project would strand it; NOTHING was deleted, re-run delete");
                    return 1;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"error: could not list {dir} to validate project {pd.Project}: {e.Message} — NOTHING was deleted");
                return 1;
            }
        }

        // csproj edits are computed and validated BEFORE any write, exactly like source edits: a
        // stale or unreadable csproj aborts while nothing has been touched, and the new texts join
        // the same rollback set. A Compile entry that cannot be removed while its source file IS
        // deleted would leave a guaranteed CS2001 broken build, so it refuses up front; package and
        // project references merely left behind are harmless and only warn (exit code 3).
        var xmlEdits = new List<(string File, string Element, Func<string, bool> Match, string Label, bool MustMatch)>();
        foreach (var rr in report.PackageRefRemovals)
        {
            xmlEdits.Add((rr.CsprojPath, "PackageReference", el => IsPackageRefElement(el, rr.Reference), $"PackageReference {rr.Reference} from {rr.Project}", false));
        }
        foreach (var rr in report.ProjectRefRemovals)
        {
            xmlEdits.Add((rr.CsprojPath, "ProjectReference", el => IsProjectRefElement(el, rr.ReferenceFile, rr.Reference + ".csproj"), $"ProjectReference {rr.Reference} from {rr.Project}", false));
        }
        foreach (var rr in report.CompileEntryRemovals)
        {
            xmlEdits.Add((rr.CsprojPath, "Compile", el => IsIncludeMatch(el, rr.Reference), $"Compile entry {rr.Reference} from {rr.Project}", true));
        }
        var xmlPending = new List<(string File, string NewText, string OldText, Encoding Encoding, int Removed)>();
        var xmlMessages = new List<string>();
        foreach (var fileGroup in xmlEdits.GroupBy(x => CanonicalPath(x.File), Constants.PathComparer).OrderBy(g => g.Key, Constants.PathComparer))
        {
            var xfile = fileGroup.Key;
            if (!File.Exists(xfile))
            {
                log($"error: {xfile} no longer exists on disk — the analysis is stale; NOTHING was deleted, re-run delete");
                return 1;
            }
            var xtext = ReadFileText(xfile, out var xenc);
            if (Stale(lengths, hashes, xfile, xtext, out var xwhy))
            {
                log($"error: {xfile} {xwhy} — NOTHING was deleted");
                return 1;
            }
            var xlines = SplitLinesKeepEnds(xtext);
            int removedInFile = 0;
            foreach (var (_, element, match, label, mustMatch) in fileGroup)
            {
                var found = FindXmlElements(xlines, element, match);
                for (int fi = found.Count - 1; fi >= 0; fi--)
                {
                    var (from, to) = found[fi];
                    if (from > 0 && xlines[from - 1].Trim().StartsWith(Constants.XmlMarkerPrefix, StringComparison.Ordinal)) { from--; }
                    xlines.RemoveRange(from, to - from + 1);
                }
                if (found.Count > 0)
                {
                    removedInFile += found.Count;
                    xmlMessages.Add($"  removed {label}");
                }
                else if (mustMatch)
                {
                    log($"error: {label} could not be matched (shares a line with other content, or the csproj changed) — its source file is planned for deletion, and a dangling entry breaks the build (CS2001); edit {xfile} first; NOTHING was deleted");
                    return 1;
                }
                else
                {
                    xmlMessages.Add($"  WARNING: {label} not removed (shares a line with other content, or not found) — edit {xfile} manually");
                    manualSteps = true;
                }
            }
            if (removedInFile > 0) { xmlPending.Add((xfile, string.Concat(xlines), xtext, xenc, removedInFile)); }
        }

        // Writes restore the already-written files on failure, so a half-applied plan never sticks.
        int RestoreFiles(List<(string File, string OldText, Encoding Encoding)> files)
        {
            int restored = 0;
            foreach (var (rf, old, enc) in files)
            {
                try
                {
                    WriteFileText(rf, old, enc);
                    restored++;
                }
                catch (Exception re) when (re is IOException or UnauthorizedAccessException)
                {
                    log($"  WARNING: could not restore {rf}: {re.Message}");
                }
            }
            return restored;
        }
        var written = new List<(string File, string OldText, Encoding Encoding)>();
        for (int wi = 0; wi < pending.Count; wi++)
        {
            var (file, newText, oldText, encoding, n) = pending[wi];
            try
            {
                WriteFileText(file, newText, encoding);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // WriteAllText truncates before writing, so the failed file itself may be mangled:
                // it is restored along with the files already edited.
                var toRestore = new List<(string File, string OldText, Encoding Encoding)>(written) { (file, oldText, encoding) };
                log($"error: could not write {file}: {e.Message} — restoring it and the {written.Count} file(s) already edited");
                int restored = RestoreFiles(toRestore);
                log($"  restored {restored}/{toRestore.Count}; nothing else was changed");
                return 1;
            }
            written.Add((file, oldText, encoding));
            regions += n;
        }

        // Whole files go only after every edit is in place. A file that cannot be deleted (locked,
        // or a read-only source-control checkout) rolls back EVERYTHING: the files already deleted
        // and every span edit, so the plan is never left half-applied.
        var deletedWhole = new List<(string File, string OldText, Encoding Encoding)>();
        foreach (var (file, oldText, encoding) in wholeFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var toRestore = deletedWhole.Concat(written).ToList();
                log($"error: could not delete {file}: {e.Message} — restoring the {deletedWhole.Count} file(s) already deleted and the {written.Count} edit(s)");
                int restored = RestoreFiles(toRestore);
                log($"  restored {restored}/{toRestore.Count}; the plan was rolled back");
                return 1;
            }
            deletedWhole.Add((file, oldText, encoding));
            deletedFiles++;
        }

        // csproj cleanup: the texts computed and validated up front join the same rollback set.
        foreach (var (xfile, newText, oldText, xenc, n) in xmlPending)
        {
            try
            {
                WriteFileText(xfile, newText, xenc);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var toRestore = deletedWhole.Concat(written).ToList();
                toRestore.Add((xfile, oldText, xenc));
                log($"error: could not write {xfile}: {e.Message} — restoring the {deletedWhole.Count} deleted file(s) and the {written.Count} edit(s)");
                int restored = RestoreFiles(toRestore);
                log($"  restored {restored}/{toRestore.Count}; the plan was rolled back");
                return 1;
            }
            written.Add((xfile, oldText, xenc));
            regions += n;
        }
        foreach (var m in xmlMessages) { log(m); }

        int deletedProjects = 0;
        foreach (var pd in report.DeletableProjects)
        {
            if (!RemoveProjectFromSolution(report.Solution, pd.CsprojPath, pd.Project, log))
            {
                log($"  WARNING: {pd.Project} left on disk — remove it from the solution manually, then delete {pd.CsprojPath}");
                manualSteps = true;
                continue;
            }
            if (File.Exists(pd.CsprojPath))
            {
                try
                {
                    File.Delete(pd.CsprojPath);
                    deletedFiles++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log($"  WARNING: {pd.Project} was removed from the solution but {pd.CsprojPath} could not be deleted: {e.Message} — delete it manually");
                    manualSteps = true;
                    continue;
                }
            }
            deletedProjects++;
            if (!TryRemoveProjectDirectory(pd.CsprojPath, log)) { manualSteps = true; }
        }

        log($"Deleted: {deletedFiles} file(s), {regions} code region(s)/reference(s), {deletedProjects}/{report.DeletableProjects.Count} project(s).");
        log("Build the solution to verify. (Usings that would no longer compile were removed; merely-unused ones are left for IDE cleanup.)");
        if (manualSteps)
        {
            log("Some csproj/sln steps could not be completed (see the WARNINGs above) — finish them manually; exit code 3.");
            return 3;
        }
        return 0;
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
            if (proc == null) { return false; }
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

    static bool TryRemoveProjectDirectory(string csprojPath, Action<string> log)
    {
        var dir = Path.GetDirectoryName(csprojPath);
        if (dir == null || !Directory.Exists(dir)) { return true; }
        try
        {
            // Check exclusivity BEFORE deleting anything: a directory shared with another project
            // (or holding non-code files) is left alone, build output included. Directories that
            // hold no files at all (folders emptied by the whole-file deletions) do not count.
            var leftovers = Directory.EnumerateFileSystemEntries(dir)
                .Where(e => !(Directory.Exists(e) && (Path.GetFileName(e) is "bin" or "obj" || IsRecursivelyEmpty(e)))).ToList();
            if (leftovers.Count > 0)
            {
                log($"  NOTE: {dir} kept — it still contains non-code files");
                return true;
            }
            foreach (var e in Directory.EnumerateFileSystemEntries(dir).ToList())
            {
                if (Directory.Exists(e)) { Directory.Delete(e, recursive: true); }
            }
            Directory.Delete(dir);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"  WARNING: could not remove {dir}: {e.Message}");
            return false;
        }
    }

    static bool IsRecursivelyEmpty(string dir)
    {
        return !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
    }

    /// <summary>Every file under <paramref name="dir"/>, skipping build-output and tooling
    /// directories (bin, obj, node_modules, ...).</summary>
    internal static IEnumerable<string> EnumerateFilesPruned(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(cur))
            {
                if (!Constants.SkippedScanDirectories.Contains(Path.GetFileName(sub))) { stack.Push(sub); }
            }
            foreach (var f in Directory.EnumerateFiles(cur)) { yield return f; }
        }
    }

    /// <summary>True when the given (0-based) line begins INSIDE a token — a verbatim or raw string
    /// literal that spans lines and ends on it. A line inserted there would land inside the string.</summary>
    internal static bool LineStartsInsideToken(SyntaxTree tree, int line)
    {
        var lineStart = tree.GetText().Lines[line].Start;
        var token = tree.GetRoot().FindToken(lineStart);
        return token.Span.Start < lineStart && lineStart < token.Span.End;
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
        if (!m.Success) { return false; }
        var value = m.Groups[1].Value.Replace('\\', '/');
        // Full Include path match: two references can share a file name in different folders.
        if (includeValue != null) { return StringComparer.OrdinalIgnoreCase.Equals(value, includeValue.Replace('\\', '/')); }
        // Fallback (older reports): exact file name — "Core.csproj" must not match "Common.Core.csproj".
        return StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(value), fallbackFileName);
    }

    static bool IsIncludeMatch(string elementText, string includeValue)
    {
        var m = Regex.Match(elementText, "Include=[\"']([^\"']+)[\"']");
        if (!m.Success) { return false; }
        return StringComparer.OrdinalIgnoreCase.Equals(m.Groups[1].Value.Replace('\\', '/'), includeValue.Replace('\\', '/'));
    }

    /// <summary>Line ranges (inclusive) of every element named <paramref name="elementName"/> whose
    /// full text matches. The end of the OPENING tag is located first (its own '>'), so a
    /// self-closing child element never truncates a multi-line parent. Only elements that own their
    /// lines EXCLUSIVELY are returned: edits are line-granular, so an element sharing a line with
    /// anything else (a sibling element, the ItemGroup tags) must not be touched.</summary>
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
            if (at < 0) { continue; }
            int line = i, col = at + open.Length, gt = -1;
            char quote = '\0';
            while (line < lines.Count)
            {
                gt = IndexOfTagEnd(lines[line], col, ref quote);
                if (gt >= 0) { break; }
                line++;
                col = 0;
            }
            if (gt < 0) { break; } // malformed: the opening tag never closes
            bool selfClosing = gt > 0 && lines[line][gt - 1] == '/';
            int to = line;
            int endCol = gt + 1;
            if (!selfClosing)
            {
                int cline = line, ccol = gt;
                to = -1;
                while (cline < lines.Count)
                {
                    var ci = lines[cline].IndexOf(close, ccol, StringComparison.Ordinal);
                    if (ci >= 0)
                    {
                        to = cline;
                        endCol = ci + close.Length;
                        break;
                    }
                    cline++;
                    ccol = 0;
                }
                if (to < 0) { break; } // malformed: no close tag
            }
            bool exclusive = lines[i][..at].Trim().Length == 0 && lines[to][endCol..].Trim().Length == 0;
            if (exclusive)
            {
                var elementText = string.Join(" ", lines.GetRange(i, to - i + 1).Select(l => l.TrimEnd('\r', '\n')));
                if (match(elementText)) { found.Add((i, to)); }
            }
            i = to;
        }
        return found;
    }

    /// <summary>Index of the '>' ending an opening tag, skipping any '>' inside a quoted attribute
    /// value (XML allows them raw there, e.g. Condition="'$(V)' &gt; '1'"). Quote state carries
    /// across lines via <paramref name="quote"/> for multi-line opening tags.</summary>
    static int IndexOfTagEnd(string lineText, int from, ref char quote)
    {
        for (int i = from; i < lineText.Length; i++)
        {
            var c = lineText[i];
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; }
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '>') { return i; }
        }
        return -1;
    }

    static int FindOpenTag(string lineText, string open, bool startsInComment)
    {
        var at = lineText.IndexOf(open, StringComparison.Ordinal);
        if (at < 0) { return -1; }
        // Reject prefixes of longer element names, and commented-out elements.
        int after = at + open.Length;
        if (after < lineText.Length && lineText[after] is not (' ' or '\t' or '>' or '/' or '\r' or '\n')) { return -1; }
        if (InXmlComment(lineText, at, startsInComment)) { return -1; }
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
            if (next < 0 || next >= pos) { break; }
            inComment = !inComment;
            i = next + token.Length;
        }
        return inComment;
    }

    static bool XmlCommentStateAtEnd(string lineText, bool startsInComment)
    {
        return InXmlComment(lineText, lineText.Length, startsInComment);
    }

    static int MarkXmlElements(string file, List<(string Element, Func<string, bool> Match)> edits, Dictionary<string, int> lengths, Dictionary<string, string> hashes, Action<string> log)
    {
        if (!File.Exists(file)) { return 0; }
        try
        {
            var text = ReadFileText(file, out var encoding);
            if (Stale(lengths, hashes, file, text, out var why))
            {
                log($"  WARNING: {file} {why} — skipped");
                return 0;
            }
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = SplitLinesKeepEnds(text);
            // Every element's start line is found on the UNMODIFIED lines first, then markers go in
            // bottom-up, so an insertion never shifts a line still to be marked.
            var starts = new SortedSet<int>();
            foreach (var (element, match) in edits)
            {
                foreach (var (from, _) in FindXmlElements(lines, element, match)) { starts.Add(from); }
            }
            int inserted = 0;
            foreach (var from in starts.Reverse())
            {
                if (from > 0 && lines[from - 1].Trim().StartsWith(Constants.XmlMarkerPrefix, StringComparison.Ordinal)) { continue; }
                var terminator = LineTerminator(lines[from]);
                if (terminator.Length == 0) { terminator = nl; }
                lines.Insert(from, Indent(lines[from]) + Constants.XmlMarker + terminator);
                inserted++;
            }
            if (inserted > 0) { WriteFileText(file, string.Concat(lines), encoding); }
            return inserted;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"  WARNING: could not edit {file}: {e.Message}");
            return 0;
        }
    }

    // ---------------------------------------------------------- text helpers

    static void AddMarkerLineAbove(int line, string text, int[] starts, List<(int, int)> ranges)
    {
        if (line < 2 || line - 1 > starts.Length) { return; }
        int aboveStart = starts[line - 2];
        int aboveEnd = line - 1 < starts.Length ? starts[line - 1] : text.Length;
        var above = text[aboveStart..aboveEnd];
        if (above.Trim().StartsWith(Constants.Marker, StringComparison.Ordinal)) { ranges.Add((aboveStart, aboveEnd)); }
    }

    static (string Text, int Regions) ComputeDeletions(string text, List<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0) { return (text, 0); }
        var merged = MergeRanges(ranges);
        var sb = new StringBuilder(text);
        int regions = 0;
        foreach (var (start, end) in merged.OrderByDescending(r => r.Start))
        {
            var s = Math.Clamp(start, 0, sb.Length);
            var e = Math.Clamp(end, s, sb.Length);
            sb.Remove(s, e - s);
            regions++;
        }
        return (sb.ToString(), regions);
    }

    /// <summary>Sorted, overlap-merged copy of the ranges. Shared with the verifier so both apply
    /// the same deletions.</summary>
    internal static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var result = new List<(int Start, int End)>();
        foreach (var r in sorted)
        {
            if (result.Count > 0 && r.Start <= result[^1].End)
            {
                var last = result[^1];
                result[^1] = (last.Start, Math.Max(last.End, r.End));
            }
            else
            {
                result.Add(r);
            }
        }
        return result;
    }

    /// <summary>Reads a file preserving its encoding for the write-back: BOMs are honoured, BOM-less
    /// files are decoded as strict UTF-8, and invalid UTF-8 falls back to Latin-1 (byte-preserving)
    /// instead of silently inserting U+FFFD replacement characters.</summary>
    internal static string ReadFileText(string path, out Encoding encoding)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            // Strict after the BOM too: silently replacing invalid bytes with U+FFFD would corrupt
            // the untouched parts on write-back. Invalid content falls back to Latin-1 over ALL
            // bytes (BOM included), which round-trips exactly — and then differs from the analyzed
            // text, so the length guard skips the file instead of mangling it.
            try
            {
                var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes, 3, bytes.Length - 3);
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                return text;
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.Latin1;
                return Encoding.Latin1.GetString(bytes);
            }
        }
        // UTF-32 LE shares its first two bytes with UTF-16 LE, so it must be checked first.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
            return new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetString(bytes, 4, bytes.Length - 4);
        }
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(bytes, 4, bytes.Length - 4);
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
            // Not UTF-8: the compiler decodes such a file with the system ANSI code page (Windows-1252
            // on Western systems), so the editor must too, or "’" and "“" (0x80-0x9F) read differently
            // from the analyzed text. Used only when it round-trips the bytes EXACTLY; otherwise
            // Latin-1 keeps the bytes and the hash guard skips the file instead of mangling it.
            var ansi = LegacyAnsiEncoding();
            if (ansi != null)
            {
                var ansiText = ansi.GetString(bytes);
                bool roundTrips;
                try
                {
                    roundTrips = ansi.GetBytes(ansiText).AsSpan().SequenceEqual(bytes);
                }
                catch (EncoderFallbackException)
                {
                    roundTrips = false;
                }
                if (roundTrips)
                {
                    encoding = ansi;
                    return ansiText;
                }
            }
            encoding = Encoding.Latin1;
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>The fallback encoding the C# compiler uses for non-UTF-8 files: the system ANSI code
    /// page, or Windows-1252. Null when code pages are unavailable.</summary>
    static Encoding LegacyAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var ansi = Encoding.GetEncoding(0);
            // A UTF-8 "ANSI" page (Linux) is not a legacy code page: the compiler then uses 1252.
            if (ansi.CodePage == Encoding.UTF8.CodePage) { ansi = Encoding.GetEncoding(Constants.WindowsWesternCodePage); }
            return Encoding.GetEncoding(ansi.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Writes atomically: the text goes to a temp file in the same directory first, then
    /// replaces the target in one move — a crash mid-write can never leave a truncated source file.</summary>
    static void WriteFileText(string path, string text, Encoding encoding)
    {
        var tmp = path + ".dcc-" + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, encoding);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* leftover temp file only */ }
            }
        }
    }

    /// <summary>SHA-256 (hex) of a text, computed identically from the analyzed tree and from the
    /// decoded on-disk file, so same-length concurrent edits are caught before any write.</summary>
    internal static string TextHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    /// <summary>True when the on-disk text no longer matches what the analysis saw.</summary>
    static bool Stale(Dictionary<string, int> lengths, Dictionary<string, string> hashes, string file, string text, out string why)
    {
        if (lengths.TryGetValue(file, out var expected) && expected != text.Length)
        {
            why = $"decodes to {text.Length} chars but the analysis saw {expected} (changed since the analysis, or encoding mismatch)";
            return true;
        }
        if (hashes.TryGetValue(file, out var h) && h != TextHash(text))
        {
            // U+FFFD means the on-disk bytes were not valid for the decoded encoding: the usual
            // culprit is a legacy single-byte file (CP1252), which Roslyn and this editor decode
            // differently — a conversion problem, not a concurrent edit.
            why = text.Contains('�')
                ? "decodes with replacement characters — the file is not valid UTF-8; convert it to UTF-8 first"
                : "its content changed since the analysis (same length, different text)";
            return true;
        }
        why = null;
        return false;
    }

    static readonly Dictionary<string, string> _canonicalCache = new(StringComparer.Ordinal);

    /// <summary>Collapses case-variant spellings of one path to the single on-disk spelling. On a
    /// case-insensitive filesystem mounted under case-sensitive path rules (WSL /mnt), linked files
    /// can appear under two casings: editing them as two files makes the second write clobber the
    /// first. Two genuinely distinct entries (a truly case-sensitive filesystem) stay distinct.</summary>
    internal static string CanonicalPath(string path)
    {
        if (OperatingSystem.IsWindows()) { return path; } // PathComparer is already case-insensitive
        if (_canonicalCache.TryGetValue(path, out var cached)) { return cached; }
        var result = path;
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(dir) && name.Length > 0)
        {
            var cdir = CanonicalPath(dir);
            result = Path.Combine(cdir, name);
            try
            {
                if (Directory.Exists(cdir))
                {
                    string exact = null, insensitive = null;
                    int insensitiveCount = 0;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(cdir))
                    {
                        var en = Path.GetFileName(entry);
                        if (en == name) { exact = entry; }
                        if (StringComparer.OrdinalIgnoreCase.Equals(en, name))
                        {
                            insensitive = entry;
                            insensitiveCount++;
                        }
                    }
                    if (exact != null) { result = exact; }
                    else if (insensitiveCount == 1) { result = insensitive; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* unreadable directory: keep the given spelling */ }
        }
        _canonicalCache[path] = result;
        return result;
    }

    static Dictionary<string, T> CanonicalKeys<T>(Dictionary<string, T> source)
    {
        var result = new Dictionary<string, T>(Constants.PathComparer);
        foreach (var (k, v) in source) { result[CanonicalPath(k)] = v; }
        return result;
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
            if (IsLineBreak(text[i])) { return text[..i]; }
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
            if (!IsLineBreak(text[i])) { continue; }
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { i++; }
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) { lines.Add(text[start..]); }
        return lines;
    }

    static string LineTerminator(string lineText)
    {
        if (lineText.EndsWith("\r\n", StringComparison.Ordinal)) { return "\r\n"; }
        if (lineText.Length > 0 && IsLineBreak(lineText[^1])) { return lineText[^1..]; }
        return "";
    }

    static string Indent(string lineText)
    {
        int w = 0;
        while (w < lineText.Length && (lineText[w] == ' ' || lineText[w] == '\t')) { w++; }
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
            if (!IsLineBreak(text[i])) { continue; }
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { i++; }
            if (i + 1 < text.Length) { starts.Add(i + 1); }
        }
        return starts.ToArray();
    }
}
