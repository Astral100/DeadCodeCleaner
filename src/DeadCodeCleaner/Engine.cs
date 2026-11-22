using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using System.Text.RegularExpressions;

namespace DeadCodeCleaner;

public sealed class Engine
{
    readonly CliOptions _opts;
    readonly AnalyzerConfig _config;
    readonly Action<string> _log;

    public Engine(CliOptions opts, AnalyzerConfig config, Action<string> log)
    {
        _opts = opts;
        _config = config;
        _log = log;
    }

    public async Task<(DeletionReport Report, Graph Graph)> Analyze()
    {
        using var workspace = MSBuildWorkspace.Create(Constants.NeutralizedBuildProperties);
        var loadWarnings = new List<string>();
        workspace.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) { loadWarnings.Add(e.Diagnostic.Message); }
        };
        _log($"Loading solution {_opts.SolutionPath} ...");
        var solution = await workspace.OpenSolutionAsync(_opts.SolutionPath);
        _log($"Building dependency graph for {solution.Projects.Count()} projects ...");
        var graph = await new GraphBuilder(_config, _log).Build(solution);
        foreach (var w in loadWarnings.Take(Constants.WorkspaceWarningLimit))
        {
            graph.Warnings.Add("workspace: " + w);
        }
        if (loadWarnings.Count > Constants.WorkspaceWarningLimit)
        {
            graph.Warnings.Add($"workspace: ... and {loadWarnings.Count - Constants.WorkspaceWarningLimit} more load diagnostics");
        }

        var (report, lostAssemblies) = ComputeReport(graph, solution);
        if (_opts.Mode != "find" && !_opts.NoVerify && _opts.RemoveTargets.Count > 0)
        {
            await Verify(solution, graph, report, lostAssemblies);
        }
        return (report, graph);
    }

    /// <summary>The compiler as referee on the OUTPUT: applies the whole deletion plan to the
    /// sources in memory, rebuilds the affected compilations, and reports every compile error that
    /// was not there before. Any hit means the plan is unsafe as-is.</summary>
    async Task Verify(Solution solution, Graph graph, DeletionReport report, Dictionary<string, HashSet<string>> lostAssemblies)
    {
        _log("Verifying the deletion plan (in-memory rebuild) ...");
        var spansByFile = new Dictionary<string, List<(int Start, int End)>>(Constants.PathComparer);
        void AddSpan(string file, int start, int length)
        {
            if (!spansByFile.TryGetValue(file, out var list)) { spansByFile[file] = list = new List<(int, int)>(); }
            if (length > 0) { list.Add((start, start + length)); }
        }
        foreach (var sym in report.Symbols.Where(s => !s.InDeletableFile))
        {
            foreach (var p in sym.Parts) { AddSpan(p.File, p.SpanStart, p.SpanLength); }
        }
        foreach (var st in report.Statements) { AddSpan(st.File, st.SpanStart, st.SpanLength); }
        foreach (var ur in report.UsingRemovals) { AddSpan(ur.File, ur.SpanStart, ur.SpanLength); }
        var deletableFiles = new HashSet<string>(report.DeletableFiles, Constants.PathComparer);

        string PathOfProject(string name)
        {
            return graph.Projects.TryGetValue(name, out var pi) ? Path.GetFullPath(pi.FilePath) : null;
        }
        var sol2 = solution;
        var changed = new HashSet<ProjectId>();
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) { continue; }
                if (deletableFiles.Contains(doc.FilePath))
                {
                    sol2 = sol2.RemoveDocument(doc.Id);
                    changed.Add(project.Id);
                    continue;
                }
                if (!spansByFile.TryGetValue(doc.FilePath, out var spans)) { continue; }
                var text = await doc.GetTextAsync();
                var merged = MergeSpans(spans);
                var inRange = merged.Where(r => r.End <= text.Length).ToList();
                if (inRange.Count < merged.Count)
                {
                    report.Warnings.Add($"VERIFY: {doc.FilePath}: {merged.Count - inRange.Count} deletion span(s) lie beyond the end of the file — verification is incomplete for it");
                }
                if (inRange.Count == 0) { continue; }
                var edits = inRange.Select(r => new Microsoft.CodeAnalysis.Text.TextChange(new Microsoft.CodeAnalysis.Text.TextSpan(r.Start, r.End - r.Start), "")).ToList();
                sol2 = sol2.WithDocumentText(doc.Id, text.WithChanges(edits));
                changed.Add(project.Id);
            }
        }
        foreach (var rr in report.ProjectRefRemovals)
        {
            var fromPath = PathOfProject(rr.Project);
            var toPath = PathOfProject(rr.Reference);
            if (fromPath == null || toPath == null) { continue; }
            foreach (var from in sol2.Projects.Where(p => p.FilePath != null && Path.GetFullPath(p.FilePath) == fromPath).ToList())
            {
                var target = sol2.Projects.FirstOrDefault(p => p.FilePath != null && Path.GetFullPath(p.FilePath) == toPath);
                if (target == null) { continue; }
                var kept = from.ProjectReferences.Where(x => x.ProjectId != target.Id).ToList();
                if (kept.Count == from.ProjectReferences.Count()) { continue; }
                sol2 = sol2.WithProjectReferences(from.Id, kept);
                changed.Add(from.Id);
            }
        }
        // Package removals have no document to edit, but their assemblies disappear from the
        // compilation: drop the matching metadata references, so a package removed while still
        // needed surfaces as a compile error here instead of after the real cleanup.
        foreach (var (pname, gone) in lostAssemblies.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ppath = PathOfProject(pname);
            if (ppath == null || gone.Count == 0) { continue; }
            foreach (var proj in sol2.Projects.Where(p => p.FilePath != null && Path.GetFullPath(p.FilePath) == ppath).ToList())
            {
                var drop = proj.MetadataReferences
                    .Where(mr => mr is PortableExecutableReference pe && pe.FilePath != null && gone.Contains(Path.GetFileNameWithoutExtension(pe.FilePath)))
                    .ToList();
                if (drop.Count == 0) { continue; }
                foreach (var mr in drop) { sol2 = sol2.RemoveMetadataReference(proj.Id, mr); }
                changed.Add(proj.Id);
            }
        }
        foreach (var pd in report.DeletableProjects)
        {
            var path = Path.GetFullPath(pd.CsprojPath);
            foreach (var proj in sol2.Projects.Where(p => p.FilePath != null && Path.GetFullPath(p.FilePath) == path).ToList())
            {
                sol2 = sol2.RemoveProject(proj.Id);
            }
        }

        var depGraph = sol2.GetProjectDependencyGraph();
        var toCheck = new HashSet<ProjectId>();
        foreach (var id in changed)
        {
            if (sol2.GetProject(id) == null) { continue; }
            toCheck.Add(id);
            toCheck.UnionWith(depGraph.GetProjectsThatTransitivelyDependOnThisProject(id));
        }

        int newErrors = 0;
        foreach (var pid in toCheck.OrderBy(p => sol2.GetProject(p)?.Name, StringComparer.OrdinalIgnoreCase))
        {
            var newProj = sol2.GetProject(pid);
            var oldProj = solution.GetProject(pid);
            if (newProj == null || oldProj == null) { continue; }
            var oldComp = await oldProj.GetCompilationAsync();
            var newComp = await newProj.GetCompilationAsync();
            if (oldComp == null || newComp == null) { continue; }
            string Key(Diagnostic d)
            {
                return d.Id + "|" + (d.Location.SourceTree?.FilePath ?? "") + "|" + d.GetMessage();
            }
            // Multiset, not set: a NEW instance of an error identical in message to a pre-existing
            // one (same missing type again, in the same file) must still count as new.
            var baseline = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in oldComp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var k = Key(d);
                baseline.TryGetValue(k, out var c);
                baseline[k] = c + 1;
            }
            foreach (var d in newComp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var k = Key(d);
                if (baseline.TryGetValue(k, out var c) && c > 0)
                {
                    baseline[k] = c - 1;
                    continue;
                }
                newErrors++;
                if (newErrors > Constants.VerifyShownErrors) { continue; }
                var pos = d.Location.GetLineSpan();
                var where = pos.Path != null ? $"{pos.Path}:{pos.StartLinePosition.Line + 1}" : newProj.Name;
                report.Warnings.Add($"VERIFY: {where}: {d.Id} {d.GetMessage()} (line refers to the text after deletion)");
            }
        }
        if (newErrors > Constants.VerifyShownErrors)
        {
            report.Warnings.Add($"VERIFY: ... and {newErrors - Constants.VerifyShownErrors} more new compile errors");
        }
        if (newErrors > 0)
        {
            report.Warnings.Add($"VERIFY: the plan introduces {newErrors} new compile error(s) — the deletion is UNSAFE as planned; fix manually (or --no-verify to override in delete mode)");
        }
        else
        {
            _log("  verification clean: the plan introduces no new compile errors");
        }
        report.VerificationErrors = newErrors;
    }

    List<(int Start, int End)> MergeSpans(List<(int Start, int End)> ranges)
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
            else
            {
                result.Add(r);
            }
        }
        return result;
    }

    (DeletionReport Report, Dictionary<string, HashSet<string>> LostAssemblies) ComputeReport(Graph graph, Solution solution)
    {
        var report = new DeletionReport
        {
            Solution = _opts.SolutionPath,
            RemoveTargets = _opts.RemoveTargets.ToArray(),
        };
        report.Warnings.AddRange(graph.Warnings);

        var removedSeeds = ResolveRemovals(graph);
        var removed = ExpandRemovals(graph, removedSeeds);

        _log("Computing reachability (baseline and after removal) ...");
        var none = new HashSet<string>(StringComparer.Ordinal);
        var r1 = Sweeper.Run(graph, graph.Roots, none);
        var r2 = Sweeper.Run(graph, graph.Roots, removed);
        var rt1 = Sweeper.Run(graph, graph.TestRoots, none);

        var orphanProd = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (removed.Contains(n) || graph.TestNodes.Contains(n)) { continue; }
            if (r1.Contains(n) && !r2.Contains(n)) { orphanProd.Add(n); }
        }

        // Surviving code that references a removal target cannot compile after the deletion: warn
        // loudly, because the cascade only covers code that DIES with the targets.
        var stillReferenced = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (src, outs) in graph.Edges)
        {
            if (!r2.Contains(src)) { continue; }
            foreach (var t in outs)
            {
                if (!removed.Contains(t)) { continue; }
                if (!stillReferenced.TryGetValue(t, out var list)) { stillReferenced[t] = list = new List<string>(); }
                list.Add(src);
            }
        }
        foreach (var (target, sources) in stillReferenced.OrderBy(kv => FqnOf(graph, kv.Key), StringComparer.Ordinal))
        {
            var shown = sources.Select(s => FqnOf(graph, s)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var more = shown.Count > Constants.ShownSurvivingRefs ? $" (+{shown.Count - Constants.ShownSurvivingRefs} more)" : "";
            report.Warnings.Add($"STILL REFERENCED: removing {FqnOf(graph, target)} breaks surviving code — the build will fail: {string.Join(", ", shown.Take(Constants.ShownSurvivingRefs))}{more}");
        }

        // Poison: any code that is not in use after the removal (tests, and pre-existing orphans)
        // but references deleted code can no longer compile — it is deleted too, transitively.
        _log("Propagating into tests and pre-existing orphans ...");
        var poisonSeeds = new HashSet<string>(removed.Concat(orphanProd), StringComparer.Ordinal);
        var membersOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (member, container) in graph.ContainerOf)
        {
            if (!membersOf.TryGetValue(container, out var list)) { membersOf[container] = list = new List<string>(); }
            list.Add(member);
        }
        var poisoned = Poison(graph, poisonSeeds, r2, membersOf);

        var brokenTests = new HashSet<string>(
            poisoned.Where(p => graph.TestNodes.Contains(p) && rt1.Contains(p)), StringComparer.Ordinal);
        var brokenOrphans = new HashSet<string>(
            poisoned.Where(p => !brokenTests.Contains(p)), StringComparer.Ordinal);

        // Cascade inside the test world: helpers only used by now-deleted tests.
        var removedForTests = new HashSet<string>(poisonSeeds.Concat(poisoned), StringComparer.Ordinal);
        var rt2 = Sweeper.Run(graph, graph.TestRoots.Where(t => !poisoned.Contains(t)), removedForTests);
        var orphanTestHelpers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.TestNodes)
        {
            if (poisoned.Contains(n) || removed.Contains(n)) { continue; }
            // !r2: a node in a project the heuristics classify as test can still be used by
            // production code (e.g. "TestCommon" shared helpers) — production liveness wins.
            if (rt1.Contains(n) && !rt2.Contains(n) && !r2.Contains(n)) { orphanTestHelpers.Add(n); }
        }

        var allDead = new HashSet<string>(
            removed.Concat(orphanProd).Concat(poisoned).Concat(orphanTestHelpers),
            StringComparer.Ordinal);

        // Statements that lost their reason to exist. Test-world death only counts for statements
        // production does not keep alive: integration tests may reach a production registration,
        // and losing those tests must never delete it.
        var deadStmts = new HashSet<StatementNode>();
        foreach (var stmt in graph.Statements)
        {
            bool prodDead = r1.Contains(stmt) && !r2.Contains(stmt);
            bool testDead = rt1.Contains(stmt) && !rt2.Contains(stmt) && !r2.Contains(stmt);
            if (prodDead || testDead) { deadStmts.Add(stmt); }
        }

        // A statement whose text references deleted code cannot compile, whatever its own liveness:
        // removing an implementation type must take its registration along even while the service
        // interface stays in use elsewhere. Re-run after every pass that grows allDead.
        void KillInvolvedStatements()
        {
            foreach (var stmt in graph.Statements)
            {
                if (deadStmts.Contains(stmt)) { continue; }
                if (!stmt.InvolvedNodes.Any(allDead.Contains)) { continue; }
                deadStmts.Add(stmt);
                if (r2.Contains(stmt))
                {
                    var gone = stmt.InvolvedNodes.Where(allDead.Contains).Select(n => FqnOf(graph, n)).Distinct().ToList();
                    report.Warnings.Add($"registration deleted although still in use: {stmt.FilePath}:{stmt.StartLine} references deleted {string.Join(", ", gone)} — check for a replacement: {stmt.Snippet}");
                }
            }
        }
        KillInvolvedStatements();

        // Empty containers: an AutoMapper profile / Autofac module whose every mapping or
        // registration died has no reason to exist — delete the class too, provided nothing
        // surviving references it and its code references nothing beyond its own base chain.
        var stmtsByMember = graph.Statements
            .GroupBy(s => s.ContainingMember, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var (typeId, reason) in graph.RootReasons)
        {
            if (reason is not (Constants.ReasonAutoMapperProfile or Constants.ReasonAutofacModule)) { continue; }
            if (allDead.Contains(typeId)) { continue; }
            if (!graph.Decl.TryGetValue(typeId, out var containerSym) || containerSym is not INamedTypeSymbol nts) { continue; }
            if (!membersOf.TryGetValue(typeId, out var containerMembers)) { continue; }
            var allowed = new HashSet<string>(StringComparer.Ordinal) { typeId };
            allowed.UnionWith(containerMembers);
            var allowedAsms = new HashSet<string>(Constants.EmptyContainerBenignAssemblies, StringComparer.OrdinalIgnoreCase);
            for (var b = nts.BaseType; b != null; b = b.BaseType)
            {
                if (Graph.Id(b) is { } bid) { allowed.Add(bid); }
                if (b.ContainingAssembly?.Name is { } ban) { allowedAsms.Add(ban); }
            }
            bool eligible = true;
            int stmtsTotal = 0;
            foreach (var m in containerMembers.Append(typeId))
            {
                if (graph.Edges.TryGetValue(m, out var outs))
                {
                    foreach (var t in outs)
                    {
                        bool ok = allowed.Contains(t)
                            || (graph.ContainerOf.TryGetValue(t, out var tc) && allowed.Contains(tc));
                        if (!ok)
                        {
                            eligible = false;
                            break;
                        }
                    }
                }
                if (!eligible) { break; }
                // External calls beyond the mapping/registration framework (a log call, say) are
                // side effects the deletion would silently drop: keep the container.
                if (graph.ExternalRefs.TryGetValue(m, out var exts) && exts.Any(a => !allowedAsms.Contains(a)))
                {
                    eligible = false;
                    break;
                }
                if (stmtsByMember.TryGetValue(m, out var ss))
                {
                    stmtsTotal += ss.Count;
                    if (ss.Any(s => !deadStmts.Contains(s)))
                    {
                        eligible = false;
                        break;
                    }
                }
            }
            if (!eligible || stmtsTotal == 0) { continue; }
            var targetSet = new HashSet<string>(containerMembers, StringComparer.Ordinal) { typeId };
            bool referenced = false;
            foreach (var (src, outs) in graph.Edges)
            {
                if (allDead.Contains(src) || targetSet.Contains(src)) { continue; }
                if (HasDeadContainer(graph, src, allDead)) { continue; }
                if (outs.Overlaps(targetSet))
                {
                    referenced = true;
                    break;
                }
            }
            if (referenced) { continue; }
            orphanProd.Add(typeId);
            allDead.Add(typeId);
            foreach (var m in containerMembers)
            {
                orphanProd.Add(m);
                allDead.Add(m);
            }
        }
        // The empty-container pass can grow allDead: statements referencing a container deleted by
        // it must die too.
        KillInvolvedStatements();

        // Fields that share a declaration with surviving fields are kept (they still count as
        // orphaned logically, but cannot be deleted alone). Tracked so cleanup passes treat them
        // as surviving.
        var keptInPlace = new HashSet<string>(StringComparer.Ordinal);

        // Symbol entries. Skip members whose containing type is itself deleted (the type entry covers them).
        foreach (var id in allDead.OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal))
        {
            if (HasDeadContainer(graph, id, allDead)) { continue; }
            if (!graph.Decl.TryGetValue(id, out var symbol)) { continue; }
            var category = removed.Contains(id) ? Constants.CategoryRemoved
                : orphanProd.Contains(id) ? Constants.CategoryOrphaned
                : brokenTests.Contains(id) ? Constants.CategoryBrokenTest
                : brokenOrphans.Contains(id) ? Constants.CategoryBrokenOrphan
                : Constants.CategoryOrphanedTestHelper;
            bool review = false;
            string note = null;
            if (brokenTests.Contains(id))
            {
                var carriers = new List<string> { id };
                if (membersOf.TryGetValue(id, out var ms)) { carriers.AddRange(ms); }
                var surviving = carriers
                    .SelectMany(c => graph.Edges.TryGetValue(c, out var refs) ? refs : Enumerable.Empty<string>())
                    .Where(t => r2.Contains(t) && !graph.TestNodes.Contains(t))
                    .Select(t => FqnOf(graph, t)).Distinct().Take(Constants.ShownSurvivingRefs).ToList();
                if (surviving.Count > 0)
                {
                    review = true;
                    note = "also references surviving code: " + string.Join(", ", surviving);
                }
            }
            var parts = DeclarationParts(graph, symbol, id, allDead, keptInPlace, report.Warnings);
            if (parts.Count == 0) { continue; }
            report.Symbols.Add(new SymbolDeletion
            {
                Fqn = SymbolNames.Fqn(symbol),
                Kind = SymbolNames.Kind(symbol),
                Category = category,
                Parts = parts,
                Review = review,
                ReviewNote = note,
            });
        }

        // Deleted symbols whose names still appear inside inactive #if regions: the compiler (and
        // the verifier) cannot see that code, so another build configuration may break silently.
        if (graph.DisabledTexts.Count > 0)
        {
            int mentions = 0;
            foreach (var symd in report.Symbols)
            {
                var dot = symd.Fqn.LastIndexOf('.');
                var name = dot >= 0 ? symd.Fqn[(dot + 1)..] : symd.Fqn;
                if (name.Length < 3 || name == "ctor" || name == "cctor") { continue; }
                foreach (var (file, disabledText) in graph.DisabledTexts.OrderBy(kv => kv.Key, Constants.PathComparer))
                {
                    if (!Regex.IsMatch(disabledText, @"\b" + Regex.Escape(name) + @"\b")) { continue; }
                    mentions++;
                    if (mentions <= Constants.ShownInactiveMentions)
                    {
                        report.Warnings.Add($"deleted {symd.Fqn} is mentioned in inactive #if code in {file} — another build configuration may still use it");
                    }
                    break;
                }
            }
            if (mentions > Constants.ShownInactiveMentions)
            {
                report.Warnings.Add($"... and {mentions - Constants.ShownInactiveMentions} more deleted symbols are mentioned in inactive #if code");
            }
        }

        // A kept-in-place field survives physically: anything it references must not be deleted
        // silently, or the surviving text no longer compiles.
        foreach (var id in keptInPlace.OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal))
        {
            if (!graph.Edges.TryGetValue(id, out var outs)) { continue; }
            var broken = outs.Where(t => allDead.Contains(t) && !keptInPlace.Contains(t))
                .Select(t => FqnOf(graph, t)).Distinct().OrderBy(x => x, StringComparer.Ordinal)
                .Take(Constants.ShownSurvivingRefs).ToList();
            if (broken.Count > 0)
            {
                report.Warnings.Add($"STILL REFERENCED: kept-in-place {FqnOf(graph, id)} references deleted {string.Join(", ", broken)} — adjust manually or the build will fail");
            }
        }

        // Statement entries (only when their containing member survives). Statements sharing one
        // anchor (fluent chains) delete only their own segment when a sibling survives.
        var stmtsByAnchor = graph.Statements
            .GroupBy(s => s.FilePath + "#" + s.AnchorStart, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var emittedWholeSpans = new HashSet<string>(StringComparer.Ordinal);
        void WarnOnce(string msg)
        {
            if (!report.Warnings.Contains(msg)) { report.Warnings.Add(msg); }
        }
        foreach (var stmt in deadStmts.OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.AnchorStart).ThenBy(s => s.SegmentStart))
        {
            if (allDead.Contains(stmt.ContainingMember) || HasDeadContainer(graph, stmt.ContainingMember, allDead)) { continue; }
            if (stmt.ManualReason != null)
            {
                WarnOnce($"manual: {stmt.FilePath}:{stmt.StartLine} — {stmt.ManualReason}; delete it manually: {stmt.Snippet}");
                continue;
            }
            var siblings = stmtsByAnchor[stmt.FilePath + "#" + stmt.AnchorStart];
            bool whole = stmt.WholeAnchorAllowed && siblings.All(deadStmts.Contains);
            int spanStart, spanLength;
            if (whole)
            {
                if (!emittedWholeSpans.Add(stmt.FilePath + "#" + stmt.AnchorStart)) { continue; }
                spanStart = stmt.AnchorStart;
                spanLength = stmt.AnchorLength;
            }
            else if (stmt.SegmentStart >= 0)
            {
                spanStart = stmt.SegmentStart;
                spanLength = stmt.SegmentLength;
            }
            else
            {
                WarnOnce($"manual: {stmt.FilePath}:{stmt.StartLine} — the containing statement must partially survive; delete this call manually: {stmt.Snippet}");
                continue;
            }
            report.Statements.Add(new StatementDeletion
            {
                Kind = stmt.Kind,
                File = stmt.FilePath,
                StartLine = stmt.StartLine,
                EndLine = stmt.EndLine,
                Snippet = stmt.Snippet,
                SpanStart = spanStart,
                SpanLength = spanLength,
                WholeStatement = whole,
            });
        }
        report.Statements.Sort((a, b) => string.CompareOrdinal(a.File, b.File) is var c && c != 0 ? c : a.StartLine.CompareTo(b.StartLine));

        // Whole files: deletable when every node declared in the file is deleted.
        var nodesByFile = new Dictionary<string, List<string>>(Constants.PathComparer);
        foreach (var (id, symbol) in graph.Decl)
        {
            foreach (var r in symbol.DeclaringSyntaxReferences)
            {
                var f = r.SyntaxTree.FilePath;
                if (string.IsNullOrEmpty(f)) { continue; }
                if (!nodesByFile.TryGetValue(f, out var list)) { nodesByFile[f] = list = new List<string>(); }
                list.Add(id);
            }
        }
        var filesTouched = new HashSet<string>(
            report.Symbols.SelectMany(s => s.Parts).Select(p => p.File),
            Constants.PathComparer);
        foreach (var f in filesTouched.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!nodesByFile.TryGetValue(f, out var declared) || !declared.All(allDead.Contains)) { continue; }
            if (graph.FilesWithTopLevelContent.Contains(f))
            {
                report.Warnings.Add($"file kept (has global usings / assembly attributes): {f} — its dead declarations are deleted individually");
                continue;
            }
            if (graph.FilesWithInactiveCode.Contains(f))
            {
                report.Warnings.Add($"file kept (contains inactive #if regions the compiler cannot see): {f} — its dead declarations are deleted individually");
                continue;
            }
            report.DeletableFiles.Add(f);
        }
        // Membership checks use the platform's path casing rule: linked files can carry different
        // path casings on Windows, while Linux paths are case-sensitive.
        var deletableFileSet = new HashSet<string>(report.DeletableFiles, Constants.PathComparer);
        foreach (var sym in report.Symbols)
        {
            sym.InDeletableFile = sym.Parts.All(p => deletableFileSet.Contains(p.File));
        }

        var lostAssemblies = ComputeProjectCleanup(graph, report, allDead, keptInPlace, deletableFileSet);

        report.Stats = new ReportStats
        {
            Projects = solution.Projects.Count(),
            Nodes = graph.Nodes.Count,
            Edges = graph.EdgeCount,
            Rules = graph.Rules.Count,
            ProductionRoots = graph.Roots.Count,
            TestRoots = graph.TestRoots.Count,
            InUseBaseline = graph.Nodes.Count(n => r1.Contains(n)),
            InUseAfterRemoval = graph.Nodes.Count(n => r2.Contains(n)),
            AlreadyOrphanedBefore = graph.Nodes.Count(n =>
                !graph.TestNodes.Contains(n) && !r1.Contains(n) && !rt1.Contains(n)),
        };
        return (report, lostAssemblies);
    }

    string FqnOf(Graph graph, string id)
    {
        return graph.Decl.TryGetValue(id, out var s) ? SymbolNames.Fqn(s) : id;
    }

    bool HasDeadContainer(Graph graph, string id, HashSet<string> dead)
    {
        var cur = id;
        while (graph.ContainerOf.TryGetValue(cur, out var container))
        {
            if (dead.Contains(container)) { return true; }
            cur = container;
        }
        return false;
    }

    List<SourcePart> DeclarationParts(Graph graph, ISymbol s, string id, HashSet<string> allDead, HashSet<string> keptInPlace, List<string> warnings)
    {
        var parts = new List<SourcePart>();
        // A partial method/property has a second declaration under a separate symbol: deleting only
        // one part cannot compile, so both parts are collected.
        var declSymbols = new List<ISymbol> { s };
        if (s is IMethodSymbol pm)
        {
            if (pm.PartialImplementationPart != null) { declSymbols.Add(pm.PartialImplementationPart); }
            if (pm.PartialDefinitionPart != null) { declSymbols.Add(pm.PartialDefinitionPart); }
        }
        else if (s is IPropertySymbol pp)
        {
            if (pp.PartialImplementationPart != null) { declSymbols.Add(pp.PartialImplementationPart); }
            if (pp.PartialDefinitionPart != null) { declSymbols.Add(pp.PartialDefinitionPart); }
        }
        foreach (var r in declSymbols.SelectMany(ds => ds.DeclaringSyntaxReferences))
        {
            var node = r.GetSyntax();
            // A member whose declaring syntax IS the type declaration (a primary constructor):
            // deleting that span would delete the whole type. Never emit it.
            if (node is TypeDeclarationSyntax && s is not INamedTypeSymbol) { continue; }
            // A positional record parameter cannot be deleted by span (the separating comma and the
            // other parameters remain): keep it in place.
            if (node is ParameterSyntax)
            {
                keptInPlace.Add(id);
                var pmsg = $"kept in place: {SymbolNames.Fqn(s)} is declared by a positional parameter — remove it manually";
                if (!warnings.Contains(pmsg)) { warnings.Add(pmsg); }
                continue;
            }
            // Fields/events: the declarator sits inside a declaration that may hold several variables.
            if (node is VariableDeclaratorSyntax v)
            {
                var container = v.FirstAncestorOrSelf<MemberDeclarationSyntax>();
                var variables = (container as BaseFieldDeclarationSyntax)?.Declaration.Variables;
                if (variables is { Count: 1 })
                {
                    node = container;
                }
                else if (variables != null)
                {
                    // Whole line goes only when every sibling is deleted too; otherwise this
                    // field stays in place (it cannot be deleted alone).
                    bool allSiblingsDead = true;
                    foreach (var sib in variables)
                    {
                        var sibId = SiblingFieldId(graph, id, sib.Identifier.Text);
                        if (sibId == null || !allDead.Contains(sibId))
                        {
                            allSiblingsDead = false;
                            break;
                        }
                    }
                    if (!allSiblingsDead)
                    {
                        keptInPlace.Add(id);
                        var msg = $"kept in place: {SymbolNames.Fqn(s)} shares a declaration with in-use field(s)";
                        if (!warnings.Contains(msg)) { warnings.Add(msg); }
                        continue;
                    }
                    // The first sibling's entry carries the whole-declaration span.
                    if (variables.Value[0].Identifier.Text != s.Name) { continue; }
                    node = container;
                }
            }
            var tree = node.SyntaxTree;
            if (string.IsNullOrEmpty(tree.FilePath)) { continue; }
            // FullSpan includes leading trivia; exclude leading #directives (#region, #if) whose
            // counterpart lies outside the deleted span, or deletion breaks compilation.
            var span = node.FullSpan;
            int spanStart = span.Start;
            int spanEnd = span.End;
            var leading = node.GetLeadingTrivia();
            for (int ti = leading.Count - 1; ti >= 0; ti--)
            {
                if (leading[ti].IsDirective)
                {
                    spanStart = leading[ti].FullSpan.End;
                    break;
                }
            }
            // An enum member's separator comma belongs to the list, not the member: take the
            // following comma along, or the deletion leaves "A,\n,\nC" behind.
            if (node is EnumMemberDeclarationSyntax emd && emd.Parent is EnumDeclarationSyntax ed)
            {
                var idx = ed.Members.IndexOf(emd);
                if (idx >= 0 && idx < ed.Members.SeparatorCount) { spanEnd = ed.Members.GetSeparator(idx).FullSpan.End; }
            }
            var lines = tree.GetLineSpan(node.Span);
            parts.Add(new SourcePart
            {
                File = tree.FilePath,
                StartLine = lines.StartLinePosition.Line + 1,
                EndLine = lines.EndLinePosition.Line + 1,
                SpanStart = spanStart,
                SpanLength = spanEnd - spanStart,
            });
        }
        return parts;
    }

    string SiblingFieldId(Graph graph, string memberId, string siblingName)
    {
        if (!graph.ContainerOf.TryGetValue(memberId, out var typeId)) { return null; }
        foreach (var (nid, sym) in graph.Decl)
        {
            if (sym.Name != siblingName) { continue; }
            if (sym.Kind is not (SymbolKind.Field or SymbolKind.Event)) { continue; }
            if (graph.ContainerOf.TryGetValue(nid, out var ct) && ct == typeId) { return nid; }
        }
        return null;
    }

    /// <summary>Packages, project references and whole projects that lose their last surviving
    /// user. "Already unused before" stays, matching the symbol-level policy: a reference is only
    /// removed when deleted code used it and no surviving code does. SDK-style references are
    /// transitive, so a removal in one project must not strand a DEPENDENT project either: a global
    /// repair loop restores candidates until every surviving need stays reachable in the final
    /// reference graph. Returns, per project, the assembly names no longer provided after cleanup
    /// (for the verifier to simulate).</summary>
    Dictionary<string, HashSet<string>> ComputeProjectCleanup(Graph graph, DeletionReport report, HashSet<string> allDead, HashSet<string> keptInPlace, HashSet<string> deletableFiles)
    {
        var nodesByProject = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (node, projects) in graph.ProjectsOf)
        {
            foreach (var p in projects)
            {
                if (!nodesByProject.TryGetValue(p, out var list)) { nodesByProject[p] = list = new List<string>(); }
                list.Add(node);
            }
        }

        bool Stays(string n)
        {
            return !allDead.Contains(n) || keptInPlace.Contains(n);
        }

        HashSet<string> PackageClosure(ProjectInfo p, IEnumerable<string> roots)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(roots);
            while (queue.Count > 0)
            {
                var pkg = queue.Dequeue();
                if (!seen.Add(pkg)) { continue; }
                if (p.PackageDependencies.TryGetValue(pkg, out var deps))
                {
                    foreach (var dep in deps) { queue.Enqueue(dep); }
                }
            }
            return seen;
        }

        HashSet<string> PackageClosureAssemblies(ProjectInfo p, IEnumerable<string> roots)
        {
            var asms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pkg in PackageClosure(p, roots))
            {
                if (p.PackageAssemblies.TryGetValue(pkg, out var list)) { asms.UnionWith(list); }
            }
            return asms;
        }

        var deletableProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pname, nodeIds) in nodesByProject)
        {
            if (nodeIds.Count == 0 || nodeIds.Any(Stays)) { continue; }
            if (!graph.Projects.TryGetValue(pname, out var pi)) { continue; }
            deletableProjects.Add(pname);
            report.DeletableProjects.Add(new ProjectDeletion { Project = pname, CsprojPath = pi.FilePath });
        }

        // Per-project usage facts. A "need" is a group of projects that declare one referenced node
        // (several for linked files): it is satisfied while ANY of them stays reachable.
        var survivingExt = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var deadExt = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var survivingProjRefs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var deadProjRefs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var survivingNeeds = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pname in graph.Projects.Keys)
        {
            var se = survivingExt[pname] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var de = deadExt[pname] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Assembly-attribute references survive by definition.
            if (graph.ProjectExternalRefs.TryGetValue(pname, out var always)) { se.UnionWith(always); }
            var sp = survivingProjRefs[pname] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dp = deadProjRefs[pname] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var needs = survivingNeeds[pname] = new List<string[]>();
            var needKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nodeIds = nodesByProject.TryGetValue(pname, out var ids) ? ids : new List<string>();
            foreach (var n in nodeIds)
            {
                bool stays = Stays(n);
                if (graph.ExternalRefs.TryGetValue(n, out var ext)) { (stays ? se : de).UnionWith(ext); }
                if (graph.Edges.TryGetValue(n, out var outs))
                {
                    foreach (var t in outs)
                    {
                        if (!graph.ProjectsOf.TryGetValue(t, out var tps) || tps.Contains(pname)) { continue; }
                        if (stays)
                        {
                            var group = tps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                            if (needKeys.Add(string.Join("|", group))) { needs.Add(group); }
                            sp.UnionWith(group);
                        }
                        else { dp.UnionWith(tps); }
                    }
                }
            }
        }

        // Initial removal candidates from each project's own usage.
        var pkgCandidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var refCandidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pname, pi) in graph.Projects)
        {
            var pc = pkgCandidates[pname] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rc = refCandidates[pname] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (deletableProjects.Contains(pname)) { continue; } // the whole project goes
            foreach (var pkg in pi.PackageReferences)
            {
                if (!pi.PackageAssemblies.TryGetValue(pkg, out var asms) || asms.Count == 0) { continue; } // unknown: keep
                bool usedBySurviving = asms.Any(survivingExt[pname].Contains);
                bool usedByDead = asms.Any(deadExt[pname].Contains);
                if (usedBySurviving || !usedByDead) { continue; }
                // Only the first build configuration was analyzed: a conditional entry may serve
                // another one, so it is never removed automatically.
                if (pi.ConditionalPackages.Contains(pkg))
                {
                    report.Warnings.Add($"{pname}: PackageReference {pkg} lost its last user but is conditional (build-configuration dependent) — left in place, remove manually if appropriate");
                    continue;
                }
                pc.Add(pkg);
            }
            foreach (var (refName, include) in pi.ProjectReferences)
            {
                bool targetDeleted = deletableProjects.Contains(refName);
                bool usedBySurviving = survivingProjRefs[pname].Contains(refName);
                bool usedByDead = deadProjRefs[pname].Contains(refName);
                if (!targetDeleted && (usedBySurviving || !usedByDead)) { continue; }
                if (!targetDeleted && pi.ConditionalProjectRefIncludes.Contains(include.Replace('\\', '/')))
                {
                    report.Warnings.Add($"{pname}: ProjectReference {refName} lost its last user but is conditional (build-configuration dependent) — left in place, remove manually if appropriate");
                    continue;
                }
                rc.Add(refName);
            }
        }

        IEnumerable<string> OriginalRefs(string p)
        {
            return graph.Projects.TryGetValue(p, out var pi) ? pi.ProjectReferences.Keys : Enumerable.Empty<string>();
        }
        IEnumerable<string> FinalRefs(string p)
        {
            var rc = refCandidates.TryGetValue(p, out var c) ? c : null;
            return OriginalRefs(p).Where(r => rc == null || !rc.Contains(r));
        }
        HashSet<string> Reach(string root, Func<string, IEnumerable<string>> refsOf, bool excludeDeletable)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                if (excludeDeletable && deletableProjects.Contains(p)) { continue; }
                if (!seen.Add(p)) { continue; }
                foreach (var r in refsOf(p)) { queue.Enqueue(r); }
            }
            return seen;
        }
        HashSet<string> ProvidedAssemblies(IEnumerable<string> projects, bool final)
        {
            var asms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in projects)
            {
                if (!graph.Projects.TryGetValue(q, out var qi)) { continue; }
                var pc = final && pkgCandidates.TryGetValue(q, out var c) ? c : null;
                asms.UnionWith(PackageClosureAssemblies(qi, qi.PackageReferences.Where(x => pc == null || !pc.Contains(x))));
            }
            return asms;
        }
        bool RestoreRefToward(HashSet<string> fromReach, string needed)
        {
            foreach (var q in fromReach.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!refCandidates.TryGetValue(q, out var rc)) { continue; }
                foreach (var c in rc.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (Reach(c, OriginalRefs, excludeDeletable: true).Contains(needed))
                    {
                        rc.Remove(c);
                        return true;
                    }
                }
            }
            return false;
        }
        bool RestoreForAssembly(HashSet<string> fromReach, string asm)
        {
            foreach (var q in fromReach.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!graph.Projects.TryGetValue(q, out var qi) || !pkgCandidates.TryGetValue(q, out var pc)) { continue; }
                foreach (var pkg in pc.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (PackageClosureAssemblies(qi, new[] { pkg }).Contains(asm))
                    {
                        pc.Remove(pkg);
                        return true;
                    }
                }
            }
            // A removed project reference may have carried the needed package transitively.
            foreach (var q in fromReach.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!refCandidates.TryGetValue(q, out var rc)) { continue; }
                foreach (var c in rc.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var subtree = Reach(c, OriginalRefs, excludeDeletable: true);
                    if (ProvidedAssemblies(subtree, final: false).Contains(asm))
                    {
                        rc.Remove(c);
                        return true;
                    }
                }
            }
            return false;
        }

        // Global repair: restore candidates (one per pass) until every project's surviving needs —
        // referenced projects and package-provided assemblies — stay reachable in the final graph.
        var unresolvable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool restored = true;
        while (restored)
        {
            restored = false;
            foreach (var pname in graph.Projects.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (deletableProjects.Contains(pname)) { continue; }
                var reach = Reach(pname, FinalRefs, excludeDeletable: true);
                foreach (var group in survivingNeeds[pname])
                {
                    var alive = group.Where(g => !deletableProjects.Contains(g)).ToList();
                    if (alive.Count == 0 || alive.Any(reach.Contains)) { continue; }
                    var key = pname + " -> " + string.Join("|", group);
                    if (unresolvable.Contains(key)) { continue; }
                    if (alive.Any(t => RestoreRefToward(reach, t)))
                    {
                        restored = true;
                        break;
                    }
                    unresolvable.Add(key);
                    report.Warnings.Add($"{pname}: surviving code uses project {alive[0]}, which is no longer reachable after cleanup — add a direct ProjectReference manually");
                }
                if (restored) { break; }
                var providedBefore = ProvidedAssemblies(Reach(pname, OriginalRefs, excludeDeletable: false), final: false);
                var providedAfter = ProvidedAssemblies(reach, final: true);
                foreach (var a in survivingExt[pname].OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!providedBefore.Contains(a) || providedAfter.Contains(a)) { continue; }
                    var key = pname + " !asm " + a;
                    if (unresolvable.Contains(key)) { continue; }
                    if (RestoreForAssembly(reach, a))
                    {
                        restored = true;
                        break;
                    }
                    unresolvable.Add(key);
                    report.Warnings.Add($"{pname}: surviving code uses assembly {a}, which no remaining package provides after cleanup — check manually");
                }
                if (restored) { break; }
            }
        }

        foreach (var (pname, pi) in graph.Projects.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (deletableProjects.Contains(pname)) { continue; }
            foreach (var pkg in pi.PackageReferences.Where(pkgCandidates[pname].Contains))
            {
                report.PackageRefRemovals.Add(new ReferenceRemoval { Project = pname, CsprojPath = pi.FilePath, Reference = pkg });
            }
            foreach (var c in refCandidates[pname].OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var file = pi.ProjectReferences.TryGetValue(c, out var include) ? include : null;
                report.ProjectRefRemovals.Add(new ReferenceRemoval { Project = pname, CsprojPath = pi.FilePath, Reference = c, ReferenceFile = file });
            }
        }

        // Old-style csprojs list files explicitly: a deleted file's <Compile> entry must go too.
        foreach (var file in report.DeletableFiles.OrderBy(x => x, StringComparer.Ordinal))
        {
            foreach (var (pname, pi) in graph.Projects.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (deletableProjects.Contains(pname) || pi.CompileItems.Count == 0) { continue; }
                var rel = Path.GetRelativePath(Path.GetDirectoryName(pi.FilePath) ?? "", file).Replace('\\', '/');
                var match = pi.CompileItems.FirstOrDefault(ci => StringComparer.OrdinalIgnoreCase.Equals(ci.Replace('\\', '/'), rel));
                if (match != null)
                {
                    report.CompileEntryRemovals.Add(new ReferenceRemoval { Project = pname, CsprojPath = pi.FilePath, Reference = match });
                }
            }
        }

        // Using directives that lose every assembly able to satisfy them become compile errors:
        // remove them with the references. Every project is checked — a closure can shrink because
        // a DOWNSTREAM project's reference was removed, not only its own.
        var goneByProject = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pname, pi) in graph.Projects)
        {
            if (deletableProjects.Contains(pname)) { continue; }
            var originalProjects = Reach(pname, OriginalRefs, excludeDeletable: false);
            var finalProjects = Reach(pname, FinalRefs, excludeDeletable: true);
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lost in originalProjects.Where(o => !finalProjects.Contains(o)))
            {
                if (graph.Projects.TryGetValue(lost, out var lp) && lp.AssemblyName.Length > 0) { gone.Add(lp.AssemblyName); }
            }
            var before = ProvidedAssemblies(originalProjects, final: false);
            var after = ProvidedAssemblies(finalProjects, final: true);
            gone.UnionWith(before.Where(a => !after.Contains(a)));
            if (gone.Count > 0) { goneByProject[pname] = gone; }
        }
        // A namespace (or using-static type) can die while its assembly survives: when the deletion
        // takes the last type under a name, every "using" of that name becomes CS0234. Track, per
        // namespace prefix and type FQN, whether anything under it stays.
        var solutionAssemblies = new HashSet<string>(
            graph.Projects.Values.Select(p => p.AssemblyName).Where(a => a.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var nameAlive = new Dictionary<string, bool>(StringComparer.Ordinal);
        void MarkName(string name, bool alive)
        {
            nameAlive[name] = nameAlive.TryGetValue(name, out var a) ? a || alive : alive;
        }
        foreach (var (id, sym) in graph.Decl)
        {
            if (sym is not INamedTypeSymbol t) { continue; }
            bool alive = Stays(id);
            MarkName(SymbolNames.Fqn(t), alive);
            if (t.ContainingType != null) { continue; }
            var ns = t.ContainingNamespace?.IsGlobalNamespace == false ? t.ContainingNamespace.ToDisplayString() : "";
            for (var cur = ns; cur.Length > 0;)
            {
                MarkName(cur, alive);
                var dot = cur.LastIndexOf('.');
                cur = dot < 0 ? "" : cur[..dot];
            }
        }

        // A linked file is compiled into several projects, one UsingRef entry each: the directive
        // goes only when EVERY surviving project that compiles the file has lost all its providers.
        foreach (var g in graph.UsingRefs.GroupBy(u => u.File + "#" + u.SpanStart, StringComparer.OrdinalIgnoreCase))
        {
            var relevant = g.Where(u => !deletableProjects.Contains(u.Project)).ToList();
            if (relevant.Count == 0) { continue; }
            var u0 = relevant[0];
            if (deletableFiles.Contains(u0.File)) { continue; }
            bool allGone = relevant.All(u => u.Providers.Count > 0
                && goneByProject.TryGetValue(u.Project, out var gone) && u.Providers.All(gone.Contains));
            bool nameDies = nameAlive.TryGetValue(u0.ResolvedName, out var stillAlive) && !stillAlive
                && relevant.All(u => u.Providers.Count > 0 && u.Providers.All(solutionAssemblies.Contains));
            if (allGone || nameDies)
            {
                report.UsingRemovals.Add(new UsingRemoval { File = u0.File, Line = u0.Line, Namespace = u0.Namespace, SpanStart = u0.SpanStart, SpanLength = u0.SpanLength });
            }
        }
        report.UsingRemovals.Sort((a, b) => string.CompareOrdinal(a.File, b.File) is var c && c != 0 ? c : a.Line.CompareTo(b.Line));
        report.DeletableProjects.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project));
        report.PackageRefRemovals.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project) is var c && c != 0 ? c : string.CompareOrdinal(a.Reference, b.Reference));
        report.ProjectRefRemovals.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project) is var c && c != 0 ? c : string.CompareOrdinal(a.Reference, b.Reference));
        return goneByProject;
    }

    HashSet<string> ResolveRemovals(Graph graph)
    {
        var seeds = new HashSet<string>(StringComparer.Ordinal);
        if (_opts.RemoveTargets.Count == 0) { return seeds; }

        var byFqn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Register(string key, string id)
        {
            if (!byFqn.TryGetValue(key, out var list)) { byFqn[key] = list = new List<string>(); }
            list.Add(id);
        }
        foreach (var (id, symbol) in graph.Decl)
        {
            var fqn = SymbolNames.Fqn(symbol);
            Register(fqn, id);
            // Generic types also register with an arity suffix ("Ns.Result`1") for disambiguation.
            if (symbol is INamedTypeSymbol { Arity: > 0 } generic) { Register(fqn + "`" + generic.Arity, id); }
        }

        foreach (var target in _opts.RemoveTargets)
        {
            var key = target.Trim();
            string signature = null;
            var paren = key.IndexOf('(');
            if (paren >= 0)
            {
                // "Ns.Type.Method(Invoice, int)" selects that overload only.
                signature = key[paren..].Replace(" ", "");
                key = key[..paren];
            }
            if (!byFqn.TryGetValue(key, out var matches) || matches.Count == 0)
            {
                var suggestions = byFqn.Keys
                    .Where(k => k.Contains(key, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k.Length).Take(Constants.SuggestionLimit).ToList();
                throw new InvalidOperationException(
                    $"Removal target not found: '{target}'." +
                    (suggestions.Count > 0 ? " Did you mean: " + string.Join(" | ", suggestions) : ""));
            }
            if (signature != null)
            {
                var narrowed = matches.Where(m => graph.Decl[m] is IMethodSymbol ms
                    && ShortSignature(ms) == signature).ToList();
                if (narrowed.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"No overload of '{key}' matches '{signature}'. Available:\n  " +
                        string.Join("\n  ", matches
                            .Select(m => graph.Decl[m])
                            .OfType<IMethodSymbol>()
                            .Select(ms => key + ShortSignature(ms))));
                }
                matches = narrowed;
            }
            var containers = matches
                .Select(m => graph.ContainerOf.TryGetValue(m, out var c) ? c : "")
                .Distinct().ToList();
            if (matches.Count > 1)
            {
                // Multiple matches are only fine when they are overloads of one method.
                bool allOverloads = containers.Count == 1
                    && matches.All(m => graph.Decl.TryGetValue(m, out var d) && d is IMethodSymbol);
                if (!allOverloads)
                {
                    throw new InvalidOperationException(
                        $"Removal target '{target}' is ambiguous:\n  " +
                        string.Join("\n  ", matches.Select(m => SymbolNames.Detailed(graph.Decl[m]))) +
                        "\n(A generic type can be selected with an arity suffix, e.g. 'Ns.Result`2' for Result<T1,T2>.)");
                }
            }
            foreach (var m in matches) { seeds.Add(m); } // without a signature: all overloads
        }
        return seeds;
    }

    /// <summary>"(Invoice,int)" — parameter types with minimal qualification, no spaces.</summary>
    string ShortSignature(IMethodSymbol ms)
    {
        // Nullable reference annotations are dropped ("string?" matches "string"); Nullable<T> value types keep their "?".
        return "(" + string.Join(",", ms.Parameters.Select(p => p.Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(" ", ""))) + ")";
    }

    HashSet<string> ExpandRemovals(Graph graph, HashSet<string> seeds)
    {
        var removed = new HashSet<string>(seeds, StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            var cur = n;
            while (graph.ContainerOf.TryGetValue(cur, out var container))
            {
                if (removed.Contains(container))
                {
                    removed.Add(n);
                    break;
                }
                cur = container;
            }
        }
        return removed;
    }

    /// <summary>Forward-breakage propagation: code that is not in use after the removal and
    /// references deleted code cannot compile, so it is deleted too. A dead member of a dead type
    /// escalates to the whole type (partial deletion could break interface contracts).</summary>
    HashSet<string> Poison(Graph graph, HashSet<string> seeds, HashSet<object> r2, Dictionary<string, List<string>> membersOf)
    {
        // Reverse adjacency restricted to sources that are not in use after removal.
        var reverse = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (src, targets) in graph.Edges)
        {
            if (r2.Contains(src)) { continue; }
            foreach (var t in targets)
            {
                if (!reverse.TryGetValue(t, out var list)) { reverse[t] = list = new List<string>(); }
                list.Add(src);
            }
        }

        var dead = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(seeds);

        void Kill(string id)
        {
            if (seeds.Contains(id) || !dead.Add(id)) { return; }
            queue.Enqueue(id);
            // A dead member of a type that is itself out of use takes the whole type down — except
            // in test classes: one broken test must not delete its healthy siblings. A test class
            // still dies when EVERY member died (empty-shell pass below).
            if (graph.ContainerOf.TryGetValue(id, out var container)
                && !r2.Contains(container) && !seeds.Contains(container)
                && !graph.TestNodes.Contains(container))
            {
                Kill(container);
            }
            // A dead type takes all its members down.
            if (membersOf.TryGetValue(id, out var members))
            {
                foreach (var m in members) { Kill(m); }
            }
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                if (reverse.TryGetValue(p, out var referrers))
                {
                    foreach (var s in referrers) { Kill(s); }
                }
                // Members of a deleted type that were already out of use die with the type
                // (poisoned types propagate to members inside Kill).
                if (seeds.Contains(p) && membersOf.TryGetValue(p, out var members))
                {
                    foreach (var m in members)
                    {
                        if (!r2.Contains(m)) { Kill(m); }
                    }
                }
            }
            // A class left without any members (all died) is an empty shell: delete it too,
            // provided it is itself out of use.
            foreach (var (type, members) in membersOf)
            {
                if (dead.Contains(type) || seeds.Contains(type) || r2.Contains(type)) { continue; }
                if (!graph.Nodes.Contains(type)) { continue; }
                if (members.Count > 0 && members.All(m => dead.Contains(m) || seeds.Contains(m)))
                {
                    Kill(type);
                    changed = true;
                }
            }
        }
        return dead;
    }
}
