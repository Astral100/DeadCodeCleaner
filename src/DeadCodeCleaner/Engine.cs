using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

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
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                loadWarnings.Add(e.Diagnostic.Message);
        };
        _log($"Loading solution {_opts.SolutionPath} ...");
        var solution = await workspace.OpenSolutionAsync(_opts.SolutionPath);
        _log($"Building dependency graph for {solution.Projects.Count()} projects ...");
        var graph = await new GraphBuilder(_config, _log).Build(solution);
        foreach (var w in loadWarnings.Take(Constants.WorkspaceWarningLimit))
            graph.Warnings.Add("workspace: " + w);
        if (loadWarnings.Count > Constants.WorkspaceWarningLimit)
            graph.Warnings.Add($"workspace: ... and {loadWarnings.Count - Constants.WorkspaceWarningLimit} more load diagnostics");

        var report = ComputeReport(graph, solution);
        return (report, graph);
    }

    DeletionReport ComputeReport(Graph graph, Solution solution)
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
            if (removed.Contains(n) || graph.TestNodes.Contains(n)) continue;
            if (r1.Contains(n) && !r2.Contains(n)) orphanProd.Add(n);
        }

        // Poison: any code that is not in use after the removal (tests, and pre-existing orphans)
        // but references deleted code can no longer compile — it is deleted too, transitively.
        _log("Propagating into tests and pre-existing orphans ...");
        var poisonSeeds = new HashSet<string>(removed.Concat(orphanProd), StringComparer.Ordinal);
        var membersOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (member, container) in graph.ContainerOf)
        {
            if (!membersOf.TryGetValue(container, out var list)) membersOf[container] = list = new List<string>();
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
            if (poisoned.Contains(n) || removed.Contains(n)) continue;
            if (rt1.Contains(n) && !rt2.Contains(n)) orphanTestHelpers.Add(n);
        }

        var allDead = new HashSet<string>(
            removed.Concat(orphanProd).Concat(poisoned).Concat(orphanTestHelpers),
            StringComparer.Ordinal);

        // Statements that lost their reason to exist.
        var deadStmts = new HashSet<StatementNode>();
        foreach (var stmt in graph.Statements)
        {
            bool prodDead = r1.Contains(stmt) && !r2.Contains(stmt);
            bool testDead = rt1.Contains(stmt) && !rt2.Contains(stmt);
            if (prodDead || testDead) deadStmts.Add(stmt);
        }

        // Empty containers: an AutoMapper profile / Autofac module whose every mapping or
        // registration died has no reason to exist — delete the class too, provided nothing
        // surviving references it and its code references nothing beyond its own base chain.
        var stmtsByMember = graph.Statements
            .GroupBy(s => s.ContainingMember, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var (typeId, reason) in graph.RootReasons)
        {
            if (reason is not ("AutoMapper profile" or "Autofac module")) continue;
            if (allDead.Contains(typeId)) continue;
            if (!graph.Decl.TryGetValue(typeId, out var containerSym) || containerSym is not INamedTypeSymbol nts) continue;
            if (!membersOf.TryGetValue(typeId, out var containerMembers)) continue;
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            for (var b = nts.BaseType; b != null; b = b.BaseType)
                if (Graph.Id(b) is { } bid) allowed.Add(bid);
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
                        if (!ok) { eligible = false; break; }
                    }
                }
                if (!eligible) break;
                if (stmtsByMember.TryGetValue(m, out var ss))
                {
                    stmtsTotal += ss.Count;
                    if (ss.Any(s => !deadStmts.Contains(s))) { eligible = false; break; }
                }
            }
            if (!eligible || stmtsTotal == 0) continue;
            var targetSet = new HashSet<string>(containerMembers, StringComparer.Ordinal) { typeId };
            bool referenced = false;
            foreach (var (src, outs) in graph.Edges)
            {
                if (allDead.Contains(src) || targetSet.Contains(src)) continue;
                if (HasDeadContainer(graph, src, allDead)) continue;
                if (outs.Overlaps(targetSet)) { referenced = true; break; }
            }
            if (referenced) continue;
            orphanProd.Add(typeId);
            allDead.Add(typeId);
            foreach (var m in containerMembers) { orphanProd.Add(m); allDead.Add(m); }
        }

        // Fields that share a declaration with surviving fields are kept (they still count as
        // orphaned logically, but cannot be deleted alone). Tracked so cleanup passes treat them
        // as surviving.
        var keptInPlace = new HashSet<string>(StringComparer.Ordinal);

        // Symbol entries. Skip members whose containing type is itself deleted (the type entry covers them).
        foreach (var id in allDead.OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal))
        {
            if (HasDeadContainer(graph, id, allDead)) continue;
            if (!graph.Decl.TryGetValue(id, out var symbol)) continue;
            var category = removed.Contains(id) ? "removed"
                : orphanProd.Contains(id) ? "orphaned"
                : brokenTests.Contains(id) ? "broken-test"
                : brokenOrphans.Contains(id) ? "broken-orphan"
                : "orphaned-test-helper";
            bool review = false;
            string note = null;
            if (brokenTests.Contains(id))
            {
                var carriers = new List<string> { id };
                if (membersOf.TryGetValue(id, out var ms)) carriers.AddRange(ms);
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
            if (parts.Count == 0) continue;
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

        // Statement entries (only when their containing member survives).
        foreach (var stmt in deadStmts)
        {
            if (allDead.Contains(stmt.ContainingMember) || HasDeadContainer(graph, stmt.ContainingMember, allDead)) continue;
            report.Statements.Add(new StatementDeletion
            {
                Kind = stmt.Kind,
                File = stmt.FilePath,
                StartLine = stmt.StartLine,
                EndLine = stmt.EndLine,
                Snippet = stmt.Snippet,
            });
        }
        report.Statements.Sort((a, b) => string.CompareOrdinal(a.File, b.File) is var c && c != 0 ? c : a.StartLine.CompareTo(b.StartLine));

        // Whole files: deletable when every node declared in the file is deleted.
        var nodesByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, symbol) in graph.Decl)
        {
            foreach (var r in symbol.DeclaringSyntaxReferences)
            {
                var f = r.SyntaxTree.FilePath;
                if (string.IsNullOrEmpty(f)) continue;
                if (!nodesByFile.TryGetValue(f, out var list)) nodesByFile[f] = list = new List<string>();
                list.Add(id);
            }
        }
        var filesTouched = new HashSet<string>(
            report.Symbols.SelectMany(s => s.Parts).Select(p => p.File),
            StringComparer.OrdinalIgnoreCase);
        foreach (var f in filesTouched.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (nodesByFile.TryGetValue(f, out var declared) && declared.All(allDead.Contains))
                report.DeletableFiles.Add(f);
        }
        foreach (var sym in report.Symbols)
            sym.InDeletableFile = sym.Parts.All(p => report.DeletableFiles.Contains(p.File));

        ComputeProjectCleanup(graph, report, allDead, keptInPlace);

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
        return report;
    }

    HashSet<string> ProjectClosure(Graph graph, IEnumerable<string> roots, HashSet<string> excluded)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(roots.Where(r => !excluded.Contains(r)));
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (!seen.Add(p)) continue;
            if (graph.Projects.TryGetValue(p, out var pi))
                foreach (var r in pi.ProjectReferences.Keys)
                    if (!excluded.Contains(r)) queue.Enqueue(r);
        }
        return seen;
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
            if (dead.Contains(container)) return true;
            cur = container;
        }
        return false;
    }

    List<SourcePart> DeclarationParts(Graph graph, ISymbol s, string id, HashSet<string> allDead, HashSet<string> keptInPlace, List<string> warnings)
    {
        var parts = new List<SourcePart>();
        foreach (var r in s.DeclaringSyntaxReferences)
        {
            var node = r.GetSyntax();
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
                        if (sibId == null || !allDead.Contains(sibId)) { allSiblingsDead = false; break; }
                    }
                    if (!allSiblingsDead)
                    {
                        keptInPlace.Add(id);
                        var msg = $"kept in place: {SymbolNames.Fqn(s)} shares a declaration with in-use field(s)";
                        if (!warnings.Contains(msg)) warnings.Add(msg);
                        continue;
                    }
                    // The first sibling's entry carries the whole-declaration span.
                    if (variables.Value[0].Identifier.Text != s.Name) continue;
                    node = container;
                }
            }
            var tree = node.SyntaxTree;
            if (string.IsNullOrEmpty(tree.FilePath)) continue;
            var span = node.FullSpan;
            var lines = tree.GetLineSpan(node.Span);
            parts.Add(new SourcePart
            {
                File = tree.FilePath,
                StartLine = lines.StartLinePosition.Line + 1,
                EndLine = lines.EndLinePosition.Line + 1,
                SpanStart = span.Start,
                SpanLength = span.Length,
            });
        }
        return parts;
    }

    string SiblingFieldId(Graph graph, string memberId, string siblingName)
    {
        if (!graph.ContainerOf.TryGetValue(memberId, out var typeId)) return null;
        foreach (var (nid, sym) in graph.Decl)
        {
            if (sym.Name != siblingName) continue;
            if (sym.Kind is not (SymbolKind.Field or SymbolKind.Event)) continue;
            if (graph.ContainerOf.TryGetValue(nid, out var ct) && ct == typeId) return nid;
        }
        return null;
    }

    /// <summary>Packages, project references and whole projects that lose their last surviving
    /// user. "Already unused before" stays, matching the symbol-level policy: a reference is only
    /// removed when deleted code used it and no surviving code does.</summary>
    void ComputeProjectCleanup(Graph graph, DeletionReport report, HashSet<string> allDead, HashSet<string> keptInPlace)
    {
        var nodesByProject = graph.ProjectOf
            .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.OrdinalIgnoreCase);

        bool Stays(string n)
        {
            return !allDead.Contains(n) || keptInPlace.Contains(n);
        }

        var deletableProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pname, nodeIds) in nodesByProject)
        {
            if (nodeIds.Count == 0 || nodeIds.Any(Stays)) continue;
            if (!graph.Projects.TryGetValue(pname, out var pi)) continue;
            deletableProjects.Add(pname);
            report.DeletableProjects.Add(new ProjectDeletion { Project = pname, CsprojPath = pi.FilePath });
        }

        foreach (var (pname, pi) in graph.Projects)
        {
            if (deletableProjects.Contains(pname)) continue; // the whole project goes
            var nodeIds = nodesByProject.TryGetValue(pname, out var ids) ? ids : new List<string>();
            var survivingExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deadExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var survivingProjRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deadProjRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodeIds)
            {
                bool stays = Stays(n);
                if (graph.ExternalRefs.TryGetValue(n, out var ext))
                    (stays ? survivingExt : deadExt).UnionWith(ext);
                if (graph.Edges.TryGetValue(n, out var outs))
                {
                    foreach (var t in outs)
                        if (graph.ProjectOf.TryGetValue(t, out var tp) && tp != pname)
                            (stays ? survivingProjRefs : deadProjRefs).Add(tp);
                }
            }
            foreach (var pkg in pi.PackageReferences)
            {
                if (!pi.PackageAssemblies.TryGetValue(pkg, out var asms) || asms.Count == 0) continue; // unknown: keep
                bool usedBySurviving = asms.Any(survivingExt.Contains);
                bool usedByDead = asms.Any(deadExt.Contains);
                if (!usedBySurviving && usedByDead)
                    report.PackageRefRemovals.Add(new ReferenceRemoval { Project = pname, CsprojPath = pi.FilePath, Reference = pkg });
            }
            // Project references: candidates by direct usage, then repaired so that projects the
            // surviving code reaches only TRANSITIVELY (through a removed reference) stay reachable.
            var direct = pi.ProjectReferences.Keys.ToList();
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var refName in direct)
            {
                bool targetDeleted = deletableProjects.Contains(refName);
                bool usedBySurviving = survivingProjRefs.Contains(refName);
                bool usedByDead = deadProjRefs.Contains(refName);
                if (targetDeleted || (!usedBySurviving && usedByDead)) candidates.Add(refName);
            }
            while (candidates.Count > 0)
            {
                var reachable = ProjectClosure(graph, direct.Where(d => !candidates.Contains(d)), deletableProjects);
                var missing = survivingProjRefs
                    .Where(n => !reachable.Contains(n) && !deletableProjects.Contains(n)).ToList();
                if (missing.Count == 0) break;
                bool repaired = false;
                foreach (var c in candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var cClosure = ProjectClosure(graph, new[] { c }, deletableProjects);
                    if (missing.Any(cClosure.Contains)) { candidates.Remove(c); repaired = true; break; }
                }
                if (!repaired) break;
            }
            foreach (var c in candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                report.ProjectRefRemovals.Add(new ReferenceRemoval { Project = pname, CsprojPath = pi.FilePath, Reference = c });
        }

        // Using directives that lose every assembly able to satisfy them become compile errors:
        // remove them with the references.
        var goneByProject = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var removedRefsByProject = report.ProjectRefRemovals
            .GroupBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var removedPkgsByProject = report.PackageRefRemovals
            .GroupBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Reference).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var pname in removedRefsByProject.Keys.Concat(removedPkgsByProject.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!graph.Projects.TryGetValue(pname, out var pi)) continue;
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var direct = pi.ProjectReferences.Keys.ToList();
            var removedR = removedRefsByProject.TryGetValue(pname, out var rr) ? rr : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var original = ProjectClosure(graph, direct, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var final = ProjectClosure(graph, direct.Where(d => !removedR.Contains(d)), deletableProjects);
            foreach (var lostProject in original.Where(o => !final.Contains(o)))
                if (graph.Projects.TryGetValue(lostProject, out var lp) && lp.AssemblyName.Length > 0)
                    gone.Add(lp.AssemblyName);
            if (removedPkgsByProject.TryGetValue(pname, out var pkgs))
                foreach (var pkg in pkgs)
                    if (pi.PackageAssemblies.TryGetValue(pkg, out var asms)) gone.UnionWith(asms);
            if (gone.Count > 0) goneByProject[pname] = gone;
        }
        foreach (var u in graph.UsingRefs)
        {
            if (!goneByProject.TryGetValue(u.Project, out var gone)) continue;
            if (report.DeletableFiles.Contains(u.File)) continue;
            if (u.Providers.Count == 0) continue;
            if (u.Providers.All(gone.Contains))
                report.UsingRemovals.Add(new UsingRemoval { File = u.File, Line = u.Line, Namespace = u.Namespace });
        }
        report.UsingRemovals.Sort((a, b) => string.CompareOrdinal(a.File, b.File) is var c && c != 0 ? c : a.Line.CompareTo(b.Line));
        report.DeletableProjects.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project));
        report.PackageRefRemovals.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project) is var c && c != 0 ? c : string.CompareOrdinal(a.Reference, b.Reference));
        report.ProjectRefRemovals.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project) is var c && c != 0 ? c : string.CompareOrdinal(a.Reference, b.Reference));
    }

    HashSet<string> ResolveRemovals(Graph graph)
    {
        var seeds = new HashSet<string>(StringComparer.Ordinal);
        if (_opts.RemoveTargets.Count == 0) return seeds;

        var byFqn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, symbol) in graph.Decl)
        {
            var fqn = SymbolNames.Fqn(symbol);
            if (!byFqn.TryGetValue(fqn, out var list)) byFqn[fqn] = list = new List<string>();
            list.Add(id);
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
            if (matches.Count > 1 && containers.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Removal target '{target}' is ambiguous:\n  " +
                    string.Join("\n  ", matches.Select(m => SymbolNames.Detailed(graph.Decl[m]))));
            }
            foreach (var m in matches) seeds.Add(m); // without a signature: all overloads
        }
        return seeds;
    }

    /// <summary>"(Invoice,int)" — parameter types with minimal qualification, no spaces.</summary>
    string ShortSignature(IMethodSymbol ms)
    {
        return "(" + string.Join(",", ms.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(" ", ""))) + ")";
    }

    HashSet<string> ExpandRemovals(Graph graph, HashSet<string> seeds)
    {
        var removed = new HashSet<string>(seeds, StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            var cur = n;
            while (graph.ContainerOf.TryGetValue(cur, out var container))
            {
                if (removed.Contains(container)) { removed.Add(n); break; }
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
            if (r2.Contains(src)) continue;
            foreach (var t in targets)
            {
                if (!reverse.TryGetValue(t, out var list)) reverse[t] = list = new List<string>();
                list.Add(src);
            }
        }

        var dead = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(seeds);

        void Kill(string id)
        {
            if (seeds.Contains(id) || !dead.Add(id)) return;
            queue.Enqueue(id);
            // A dead member of a type that is itself out of use takes the whole type down.
            if (graph.ContainerOf.TryGetValue(id, out var container)
                && !r2.Contains(container) && !seeds.Contains(container))
                Kill(container);
            // A dead type takes all its members down.
            if (membersOf.TryGetValue(id, out var members))
                foreach (var m in members) Kill(m);
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                if (reverse.TryGetValue(p, out var referrers))
                    foreach (var s in referrers) Kill(s);
                // Members of a deleted type that were already out of use die with the type
                // (poisoned types propagate to members inside Kill).
                if (seeds.Contains(p) && membersOf.TryGetValue(p, out var members))
                    foreach (var m in members)
                        if (!r2.Contains(m)) Kill(m);
            }
            // A class left without any members (all died) is an empty shell: delete it too,
            // provided it is itself out of use.
            foreach (var (type, members) in membersOf)
            {
                if (dead.Contains(type) || seeds.Contains(type) || r2.Contains(type)) continue;
                if (!graph.Nodes.Contains(type)) continue;
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
