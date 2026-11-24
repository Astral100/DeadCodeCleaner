using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.MSBuild;
using System.Text.RegularExpressions;

namespace DeadCodeCleaner;

public sealed class Engine
{
    readonly CliOptions _opts;
    readonly AnalyzerConfig _config;
    readonly Action<string> _log;
    readonly Dictionary<SyntaxTree, string> _treeHashes = new();

    public Engine(CliOptions opts, AnalyzerConfig config, Action<string> log)
    {
        _opts = opts;
        _config = config;
        _log = log;
    }

    public async Task<(DeletionReport Report, Graph Graph)> Analyze()
    {
        _log($"Loading solution {_opts.SolutionPath} ...");
        using var loaded = await LoadSolution(_opts.SolutionPath);
        var result = await Analyze(loaded);
        return result;
    }

    /// <summary>Opens a solution with MSBuild, collecting its load failures. The result can be analyzed
    /// any number of times (each analysis builds its own graph), and owns the workspace.</summary>
    public static async Task<LoadedSolution> LoadSolution(string solutionPath)
    {
        var workspace = MSBuildWorkspace.Create(Constants.NeutralizedBuildProperties);
        var loadWarnings = new List<string>();
        workspace.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) { loadWarnings.Add(e.Diagnostic.Message); }
        };
        var solution = await workspace.OpenSolutionAsync(solutionPath);
        return new LoadedSolution(workspace, solution, loadWarnings);
    }

    /// <summary>Analyzes an already loaded solution (the options' solution path must name it).</summary>
    public async Task<(DeletionReport Report, Graph Graph)> Analyze(LoadedSolution loaded)
    {
        var solution = loaded.Solution;
        var loadWarnings = loaded.LoadWarnings;
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

        // A workspace diagnostic alone does not mean a project is missing (analyzer-version
        // complaints fire for projects that load fine). What gates delete is a project that is IN
        // the solution file but absent from the loaded set: everything it references is invisible.
        int dropped = 0;
        int websites = 0;
        var loadedPaths = new HashSet<string>(
            solution.Projects.Where(p => p.FilePath != null).Select(p => Path.GetFullPath(p.FilePath)),
            Constants.PathComparer);
        try
        {
            var slnDir = Path.GetDirectoryName(_opts.SolutionPath) ?? "";
            var slnText = File.ReadAllText(_opts.SolutionPath);
            // ASP.NET Web Site projects (no csproj, App_Code + markup): excluded from the analysis
            // entirely, and what they use in other projects is invisible — delete refuses.
            foreach (Match m in Regex.Matches(slnText, "Project\\(\"\\{" + Constants.WebSiteProjectTypeGuid + "\\}\"\\)\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase))
            {
                graph.Warnings.Add($"{m.Groups[1].Value}: ASP.NET Web Site project — excluded from the analysis; code it uses in other projects looks unused, so delete refuses (--allow-website-projects overrides)");
                websites++;
            }
            foreach (Match m in Regex.Matches(slnText, "\"([^\"]+\\.(?:cs|vb|fs)proj)\""))
            {
                var p = Path.GetFullPath(Path.Combine(slnDir, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)));
                if (!loadedPaths.Contains(p))
                {
                    graph.Warnings.Add($"{Path.GetFileNameWithoutExtension(p)}: in the solution but NOT loaded — the project and every reference from it are INVISIBLE to the analysis");
                    dropped++;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* unreadable sln: the load itself would have failed */ }

        var (report, lostAssemblies) = ComputeReport(graph, solution);
        report.ProjectsFailedToLoad = dropped + graph.ProjectsNotAnalyzed.Count;
        report.WebsiteProjects = websites;
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
        int incomplete = 0;
        var spansByFile = new Dictionary<string, List<(int Start, int End)>>(Constants.PathComparer);
        void AddSpan(string fileRaw, int start, int length)
        {
            // Canonicalized like the Editor's plan: a case-variant linked file must land on ONE key,
            // or its spans in the second project escape verification.
            var file = Editor.CanonicalPath(fileRaw);
            if (!spansByFile.TryGetValue(file, out var list)) { spansByFile[file] = list = new List<(int, int)>(); }
            if (length > 0) { list.Add((start, start + length)); }
        }
        foreach (var sym in report.Symbols.Where(s => !s.InDeletableFile))
        {
            foreach (var p in sym.Parts) { AddSpan(p.File, p.SpanStart, p.SpanLength); }
        }
        foreach (var st in report.Statements) { AddSpan(st.File, st.SpanStart, st.SpanLength); }
        foreach (var ur in report.UsingRemovals) { AddSpan(ur.File, ur.SpanStart, ur.SpanLength); }
        var deletableFiles = new HashSet<string>(report.DeletableFiles.Select(Editor.CanonicalPath), Constants.PathComparer);

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
                if (deletableFiles.Contains(Editor.CanonicalPath(doc.FilePath)))
                {
                    sol2 = sol2.RemoveDocument(doc.Id);
                    changed.Add(project.Id);
                    continue;
                }
                if (!spansByFile.TryGetValue(Editor.CanonicalPath(doc.FilePath), out var spans)) { continue; }
                var text = await doc.GetTextAsync();
                var merged = Editor.MergeRanges(spans);
                var inRange = merged.Where(r => r.End <= text.Length).ToList();
                if (inRange.Count < merged.Count)
                {
                    report.Warnings.Add($"VERIFY: {doc.FilePath}: {merged.Count - inRange.Count} deletion span(s) lie beyond the end of the file — verification is incomplete for it");
                    incomplete++;
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
            if (oldComp == null || newComp == null)
            {
                report.Warnings.Add($"VERIFY: {newProj.Name}: the project could not be rebuilt in memory — verification is incomplete for it");
                incomplete++;
                continue;
            }
            string Key(Diagnostic d)
            {
                return d.Id + "|" + (d.Location.SourceTree?.FilePath ?? "") + "|" + d.GetMessage();
            }
            // The analysis neutralized warnings-as-errors to load the solution: the project's REAL
            // settings decide which warnings fail its build.
            var pinfo = graph.Projects.TryGetValue(newProj.Name, out var pi0) ? pi0 : null;
            bool FailsBuild(Diagnostic d)
            {
                if (d.Severity == DiagnosticSeverity.Error) { return true; }
                if (d.Severity != DiagnosticSeverity.Warning || pinfo == null) { return false; }
                if (pinfo.WarningsAsErrors.Contains(d.Id)) { return true; }
                return pinfo.TreatWarningsAsErrors && !pinfo.WarningsNotAsErrors.Contains(d.Id);
            }
            // Multiset, not set: a NEW instance of an error identical in message to a pre-existing
            // one (same missing type again, in the same file) must still count as new.
            var baseline = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in oldComp.GetDiagnostics().Where(FailsBuild))
            {
                var k = Key(d);
                baseline.TryGetValue(k, out var c);
                baseline[k] = c + 1;
            }
            foreach (var d in newComp.GetDiagnostics().Where(FailsBuild))
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
        if (incomplete > 0)
        {
            // Incomplete verification must not read as "clean": whatever could not be rebuilt or
            // applied counts against the plan exactly like a found error.
            report.Warnings.Add($"VERIFY: verification was incomplete in {incomplete} place(s) — treated as failed; delete refuses (--no-verify overrides)");
        }
        if (newErrors == 0 && incomplete == 0)
        {
            _log("  verification clean: the plan introduces no new compile errors");
        }
        report.VerificationErrors = newErrors + incomplete;
    }

    (DeletionReport Report, Dictionary<string, HashSet<string>> LostAssemblies) ComputeReport(Graph graph, Solution solution)
    {
        var report = new DeletionReport
        {
            Solution = _opts.SolutionPath,
            RemoveTargets = _opts.RemoveTargets.ToArray(),
        };
        report.Warnings.AddRange(graph.Warnings);
        report.ProjectsWithCompileErrors = graph.ProjectsWithCompileErrors.Count;

        if (graph.Roots.Count == 0)
        {
            report.Warnings.Add("no production entry points detected — nothing counts as in use, so every removal cascades through ALL of its transitive referrers; declare roots via --config keep/roots and re-check before trusting this report");
        }

        var removedSeeds = ResolveRemovals(graph);
        var removed = ExpandRemovals(graph, removedSeeds);

        _log("Computing reachability (baseline and after removal) ...");
        var none = new HashSet<string>(StringComparer.Ordinal);
        var r1 = Sweeper.Run(graph, graph.Roots, none);
        var r2 = Sweeper.Run(graph, graph.Roots, removed);
        var rt1 = Sweeper.Run(graph, graph.TestRoots, none);

        // The exclusion list kept code alive purely because it is NAMED somewhere the analysis cannot
        // follow (views, inactive #if code, dynamic, config). Say so where that alone saved it.
        if (graph.MentionRoots.Count > 0)
        {
            var hardRoots = graph.Roots.Where(r => !graph.MentionRoots.ContainsKey(r)).ToList();
            var r1Hard = Sweeper.Run(graph, hardRoots, none);
            var r2Hard = Sweeper.Run(graph, hardRoots, removed);
            int keptByMention = 0;
            foreach (var (id, where) in graph.MentionRoots.OrderBy(kv => FqnOf(graph, kv.Key), StringComparer.Ordinal))
            {                if (removed.Contains(id) || !r1Hard.Contains(id) || r2Hard.Contains(id)) { continue; }
                keptByMention++;
                if (keptByMention <= Constants.ShownMentionKeeps)
                {
                    report.Warnings.Add($"kept (not deleted): {FqnOf(graph, id)} — no code uses it after the removal, but it is {where}, a use the analysis cannot follow; delete it manually if it is really unused");
                }
            }
            if (keptByMention > Constants.ShownMentionKeeps)
            {
                report.Warnings.Add($"... and {keptByMention - Constants.ShownMentionKeeps} more kept for the same reason");
            }
        }

        // An explicitly removed class an assembly scan registers: the container may have been
        // resolving it (Autofac: the LAST registration of a service wins, so even an "old" class may
        // be the one running).
        foreach (var id in removedSeeds.Where(graph.ScanMatchedTypes.ContainsKey).OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal))
        {
            report.Warnings.Add($"removing {FqnOf(graph, id)}: it is registered by the assembly scan at {graph.ScanMatchedTypes[id]} — the container may have been resolving it at runtime; check before deleting");
        }

        // Removal targets named in hand-written JavaScript: URLs and calls there ("/Admin/GetRoles")
        // are invisible to the compiler and the verifier.
        if (graph.ScriptTexts.Count > 0)
        {
            var scriptWords = new Dictionary<string, string>(StringComparer.Ordinal);
            var wordRegex = new Regex(Constants.IdentifierWordPattern);
            foreach (var (file, text) in graph.ScriptTexts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                foreach (Match m in wordRegex.Matches(text)) { scriptWords.TryAdd(m.Value, file); }
            }
            var warnedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in removed.OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal))
            {
                if (!graph.Decl.TryGetValue(id, out var rs) || rs is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) { continue; }
                if (rs.Name.Length < Constants.MinMentionNameLength || !warnedNames.Add(rs.Name)) { continue; }
                if (scriptWords.TryGetValue(rs.Name, out var sf))
                {
                    report.Warnings.Add($"removal target {FqnOf(graph, id)} is named in {sf} — calls and URLs in JavaScript are invisible to the analysis; check before deleting");
                }
            }
        }

        var orphanProd = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (removed.Contains(n)) { continue; }
            // Test-world code is handled by the test cascade below — but a test-project node no test
            // ever reached lives ONLY through production, so production orphaning applies to it.
            if (graph.TestNodes.Contains(n) && rt1.Contains(n)) { continue; }
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
        report.StillReferencedTargets = stillReferenced.Count;

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
        // Config keep patterns hold in the TEST world too: a kept node (and every container it sits
        // in, since a dead container takes its members along) is never poisoned. If it references
        // deleted code it then breaks the build — reported below, and refused by delete.
        var keptProtected = new HashSet<string>(graph.ConfigKeptIds, StringComparer.Ordinal);
        foreach (var k in graph.ConfigKeptIds)
        {
            var cur = k;
            while (graph.ContainerOf.TryGetValue(cur, out var kc))
            {
                keptProtected.Add(kc);
                cur = kc;
            }
        }
        var poisoned = Poison(graph, poisonSeeds, r2, rt1, membersOf, keptProtected);

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
        // Live registrations that die only because they name deleted code (judged once all passes ran).
        var lostRegistrations = new List<StatementNode>();
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
                    if (stmt.Kind == Constants.StatementKindDiRegister) { lostRegistrations.Add(stmt); }
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
        // Every member at any depth (nested types and THEIR members): code inside a nested type is
        // part of the container and dies with it, so it must pass the same checks.
        List<string> Descendants(string typeId)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(typeId);
            while (stack.Count > 0)
            {
                if (!membersOf.TryGetValue(stack.Pop(), out var ms)) { continue; }
                foreach (var m in ms)
                {
                    result.Add(m);
                    stack.Push(m);
                }
            }
            return result;
        }
        var containerCandidates = graph.RootReasons
            .Where(kv => kv.Value is Constants.ReasonAutoMapperProfile or Constants.ReasonAutofacModule)
            .Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        bool TryDeleteEmptyContainer(string typeId)
        {
            if (allDead.Contains(typeId)) { return false; }
            if (!graph.Decl.TryGetValue(typeId, out var containerSym) || containerSym is not INamedTypeSymbol nts) { return false; }
            if (!membersOf.ContainsKey(typeId)) { return false; }
            var containerMembers = Descendants(typeId);
            // An explicit config keep/root wins over everything, this pass included.
            if (graph.ConfigKeptIds.Contains(typeId) || containerMembers.Any(graph.ConfigKeptIds.Contains)) { return false; }
            var allowed = new HashSet<string>(StringComparer.Ordinal) { typeId };
            allowed.UnionWith(containerMembers);
            var allowedAsms = new HashSet<string>(Constants.EmptyContainerBenignAssemblies, StringComparer.OrdinalIgnoreCase);
            // Members of the FRAMEWORK base (Profile / Module and above) are the mapping/registration
            // API itself. A user base class in between (BaseModule with RegisterRepositories) holds
            // code that does real work when called: only the type and its side-effect-free
            // constructors are allowed there, any other member call keeps the container.
            var frameworkBaseName = graph.RootReasons[typeId] == Constants.ReasonAutoMapperProfile ? Constants.AutoMapperProfileBaseName : Constants.AutofacModuleBaseName;
            var memberAllowedBases = new HashSet<string>(StringComparer.Ordinal);
            var userBaseCtors = new List<string>();
            bool frameworkReached = false;
            for (var b = nts.BaseType; b != null; b = b.BaseType)
            {
                if (b.Name == frameworkBaseName) { frameworkReached = true; }
                if (Graph.Id(b.OriginalDefinition) is { } bid)
                {
                    allowed.Add(bid);
                    if (frameworkReached) { memberAllowedBases.Add(bid); }
                    else { userBaseCtors.AddRange(b.OriginalDefinition.InstanceConstructors.Select(Graph.Id).Where(id => id != null)); }
                }
                if (b.ContainingAssembly?.Name is { } ban) { allowedAsms.Add(ban); }
            }
            // A user base constructor is allowed only when it is inert: no statements, no external
            // calls, and no references beyond the base chain (implicit chaining to the next base ctor).
            // Topmost first, so a lower constructor chaining upward sees whether the upper one passed.
            for (int ui = userBaseCtors.Count - 1; ui >= 0; ui--)
            {
                var bcId = userBaseCtors[ui];
                bool inert = !stmtsByMember.ContainsKey(bcId)
                    && !graph.ExternalCallRefs.ContainsKey(bcId)
                    && (!graph.Edges.TryGetValue(bcId, out var bcOuts)
                        || bcOuts.All(t => allowed.Contains(t)
                            || (graph.ContainerOf.TryGetValue(t, out var btc) && memberAllowedBases.Contains(btc))));
                if (inert) { allowed.Add(bcId); }
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
                            || (graph.ContainerOf.TryGetValue(t, out var tc) && (tc == typeId || containerMembers.Contains(tc) || memberAllowedBases.Contains(tc)));
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
                // An unclaimed CALL into any external assembly beyond the truly benign list — the
                // mapping/registration framework's own included: untracked registrations
                // (RegisterInstance, assembly scanning, Register(c => ...)) leave no edge and no
                // statement, but deleting the container would silently drop them.
                if (graph.ExternalCallRefs.TryGetValue(m, out var extCalls) && extCalls.Any(a => !Constants.EmptyContainerBenignAssemblies.Contains(a)))
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
            if (!eligible || stmtsTotal == 0) { return false; }
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
            if (referenced) { return false; }
            // A container in a test project is test-world code: report it as such.
            var bucket = graph.TestNodes.Contains(typeId) ? orphanTestHelpers : orphanProd;
            bucket.Add(typeId);
            allDead.Add(typeId);
            foreach (var m in containerMembers)
            {
                bucket.Add(m);
                allDead.Add(m);
            }
            return true;
        }
        // To a fixed point: deleting one empty container can kill statements (and free another
        // container that only it referenced), so the pass repeats until nothing more dies.
        bool containerDied = true;
        while (containerDied)
        {
            containerDied = false;
            foreach (var typeId in containerCandidates)
            {
                if (TryDeleteEmptyContainer(typeId)) { containerDied = true; }
            }
            KillInvolvedStatements();
        }

        // Fields that share a declaration with surviving fields are kept (they still count as
        // orphaned logically, but cannot be deleted alone). Tracked so cleanup passes treat them
        // as surviving.
        var keptInPlace = new HashSet<string>(StringComparer.Ordinal);

        // Symbol entries. Skip members whose containing type is itself deleted (the type entry covers them).
        // The id tie-break keeps same-FQN overloads in a deterministic report order.
        int publicApiDeleted = 0;
        foreach (var id in allDead.OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal).ThenBy(x => x, StringComparer.Ordinal))
        {
            if (HasDeadContainer(graph, id, allDead))
            {
                // A requested removal inside a type that dies for another reason: the type's entry
                // deletes it, but it must still be listed as requested — without parts of its own.
                if (removed.Contains(id) && graph.Decl.TryGetValue(id, out var coveredSym) && graph.ContainerOf.TryGetValue(id, out var coveringId))
                {
                    while (graph.ContainerOf.TryGetValue(coveringId, out var up) && allDead.Contains(up)) { coveringId = up; }
                    report.Symbols.Add(new SymbolDeletion
                    {
                        Fqn = SymbolNames.Fqn(coveredSym),
                        Kind = SymbolNames.Kind(coveredSym),
                        Category = Constants.CategoryRemoved,
                        Parts = new List<SourcePart>(),
                        CoveredBy = FqnOf(graph, coveringId),
                    });
                }
                continue;
            }
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
            else if (brokenOrphans.Contains(id) && symbol is INamedTypeSymbol bot && FrameworkBaseOf(graph, bot) is { } fw && !ReferencedInSolution(graph, id, r1))
            {
                // "Already unused" may only mean the analysis cannot see who uses it: a class built on
                // an outside framework type that nothing in the solution names is typically created by
                // that framework at runtime (scanning, conventions). Never deleted unconfirmed.
                review = true;
                note = $"possibly created by a framework at runtime: built on {fw} from outside the solution, and nothing in the solution references it";
            }
            else if (brokenOrphans.Contains(id) && symbol is IMethodSymbol or IPropertySymbol
                && graph.ContainerOf.TryGetValue(id, out var ownerId) && !allDead.Contains(ownerId)
                && graph.Decl.TryGetValue(ownerId, out var ownerSym) && ownerSym is INamedTypeSymbol owner
                && FrameworkCreatedReason(graph, owner, ownerId, r2) is { } createdHow)
            {
                // A member nothing calls, in a class that stays and that a framework or container
                // creates: the framework may call it by name (ConfigureServices, Application_Start,
                // a convention the tool does not know) — never deleted unconfirmed.
                review = true;
                note = $"possibly called by a framework at runtime: {SymbolNames.Fqn(owner)} stays, {createdHow}, and no live code creates it with new";
            }
            var parts = DeclarationParts(graph, symbol, id, allDead, keptInPlace, report.Warnings, report.FileTextLengths, report.FileTextHashes);
            if (parts.Count == 0) { continue; }
            // The containing type survives here (dead containers were skipped above): a constructor
            // deleted while its type stays in use may still be called by DI or reflection.
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, IsStatic: false })
            {
                report.Warnings.Add($"constructor deleted while its type stays in use: {SymbolNames.Fqn(symbol)} — DI or reflection may still construct the type through it at runtime; check before deleting");
            }
            if (symbol.DeclaredAccessibility == Accessibility.Public && category is Constants.CategoryOrphaned or Constants.CategoryBrokenOrphan)
            {
                publicApiDeleted++;
            }
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
        if (publicApiDeleted > 0)
        {
            // The analysis treats the solution as the whole world: consumers outside it (NuGet
            // packages, other repositories) are invisible by design and deserve one loud caveat.
            report.Warnings.Add($"{publicApiDeleted} deleted symbol(s) are public API — if any project in this solution is consumed OUTSIDE it (published package, another repo), those consumers are invisible here; add keep patterns for the published surface");
        }

        // Every deleted name, members of wholly-deleted types included: a view references a deleted
        // controller's ACTION names far more often than the type name. One entry per simple name.
        var deletedNames = new List<(string Fqn, string Name)>();
        var seenDeletedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in allDead.Where(x => !keptInPlace.Contains(x)).OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal))
        {
            if (!graph.Decl.TryGetValue(id, out var dsym)) { continue; }
            if (dsym is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor }) { continue; }
            if (dsym.Name.Length < 3 || !seenDeletedNames.Add(dsym.Name)) { continue; }
            deletedNames.Add((SymbolNames.Fqn(dsym), dsym.Name));
        }

        // Deleted symbols whose names still appear inside inactive #if regions: the compiler (and
        // the verifier) cannot see that code, so another build configuration may break silently.
        if (graph.DisabledTexts.Count > 0)
        {
            int mentions = 0;
            foreach (var (fqn, name) in deletedNames)
            {
                foreach (var (file, disabledText) in graph.DisabledTexts.OrderBy(kv => kv.Key, Constants.PathComparer))
                {
                    if (!Regex.IsMatch(disabledText, @"\b" + Regex.Escape(name) + @"\b")) { continue; }
                    mentions++;
                    if (mentions <= Constants.ShownInactiveMentions)
                    {
                        report.Warnings.Add($"deleted {fqn} is mentioned in inactive #if code in {file} — another build configuration may still use it");
                    }
                    break;
                }
            }
            if (mentions > Constants.ShownInactiveMentions)
            {
                report.Warnings.Add($"... and {mentions - Constants.ShownInactiveMentions} more deleted symbols are mentioned in inactive #if code");
            }
        }

        // Deleted symbols whose names appear in Razor/WebForms markup: views compile at RUNTIME, so
        // neither the compiler nor the verifier sees a reference from them, and a page breaks in
        // production instead of in the build.
        if (graph.ViewTexts.Count > 0)
        {
            int viewMentions = 0;
            foreach (var (fqn, name) in deletedNames)
            {
                foreach (var (file, viewText) in graph.ViewTexts.OrderBy(kv => kv.Key, Constants.PathComparer))
                {
                    if (!Regex.IsMatch(viewText, @"\b" + Regex.Escape(name) + @"\b")) { continue; }
                    viewMentions++;
                    if (viewMentions <= Constants.ShownViewMentions)
                    {
                        report.Warnings.Add($"deleted {fqn} is mentioned in {file} — views compile at runtime, so this deletion can break a page the verifier cannot see");
                    }
                    break;
                }
            }
            if (viewMentions > Constants.ShownViewMentions)
            {
                report.Warnings.Add($"... and {viewMentions - Constants.ShownViewMentions} more deleted symbols are mentioned in view/markup files");
            }
        }

        // Deleted members whose names are accessed through 'dynamic' somewhere in the solution:
        // dynamic binding happens at runtime, invisible to the analysis AND the verifier.
        if (graph.DynamicMemberNames.Count > 0)
        {
            int dynMentions = 0;
            foreach (var id in allDead.OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal))
            {
                if (!graph.Decl.TryGetValue(id, out var dsym) || dsym is INamedTypeSymbol) { continue; }
                if (dsym is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }) { continue; }
                if (!graph.DynamicMemberNames.Contains(dsym.Name)) { continue; }
                dynMentions++;
                if (dynMentions <= Constants.ShownDynamicMentions)
                {
                    report.Warnings.Add($"deleted {FqnOf(graph, id)} shares its name with a member accessed dynamically — dynamic member binding happens at runtime and may break; check the dynamic call sites");
                }
            }
            if (dynMentions > Constants.ShownDynamicMentions)
            {
                report.Warnings.Add($"... and {dynMentions - Constants.ShownDynamicMentions} more deleted members share names with dynamic accesses");
            }
        }

        // Deleted types whose full names appear in .config / .json files: config wiring (HTTP
        // modules, providers, serialized settings) references types by name, invisibly.
        if (graph.ConfigTextsByProject.Count > 0)
        {
            int cfgMentions = 0;
            var scannedConfigs = new HashSet<string>(Constants.PathComparer);
            var configTexts = new List<(string File, string Text)>();
            foreach (var texts in graph.ConfigTextsByProject.Values)
            {
                foreach (var t in texts)
                {
                    if (t.Text.Length > 0 && scannedConfigs.Add(t.File)) { configTexts.Add(t); }
                }
            }
            foreach (var id in allDead.OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal))
            {
                if (!graph.Decl.TryGetValue(id, out var tsym) || tsym is not INamedTypeSymbol tnts) { continue; }
                var fqn = SymbolNames.Fqn(tsym);
                // Config wiring spells nested types the CLR way ("Outer+Inner"), not the C# way.
                var metadataFqn = tnts.ContainingType == null ? fqn : SymbolNames.MetadataFqn(tnts);
                foreach (var (file, text) in configTexts)
                {
                    if (!text.Contains(fqn, StringComparison.Ordinal) && !text.Contains(metadataFqn, StringComparison.Ordinal)) { continue; }
                    cfgMentions++;
                    if (cfgMentions <= Constants.ShownConfigMentions)
                    {
                        report.Warnings.Add($"deleted {fqn} is mentioned in {file} — config-wired types load at runtime and are invisible to the analysis; check before deleting");
                    }
                    break;
                }
            }
            if (cfgMentions > Constants.ShownConfigMentions)
            {
                report.Warnings.Add($"... and {cfgMentions - Constants.ShownConfigMentions} more deleted types are mentioned in config files");
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
        // Config-kept code the production sweep does not reach (test-world code) is shielded from the
        // poison pass, so a reference from it into deleted code is a build break the user must fix:
        // counted like a still-referenced target, so delete refuses.
        var keptBrokenTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in graph.ConfigKeptIds.Where(k => !r2.Contains(k) && !allDead.Contains(k)).OrderBy(x => FqnOf(graph, x), StringComparer.Ordinal))
        {
            if (!graph.Edges.TryGetValue(id, out var outs)) { continue; }
            var brokenIds = outs.Where(allDead.Contains).ToList();
            if (brokenIds.Count == 0) { continue; }
            keptBrokenTargets.UnionWith(brokenIds);
            var shown = brokenIds.Select(t => FqnOf(graph, t)).Distinct().OrderBy(x => x, StringComparer.Ordinal).Take(Constants.ShownSurvivingRefs);
            report.Warnings.Add($"STILL REFERENCED: config-kept {FqnOf(graph, id)} references deleted {string.Join(", ", shown)} — the keep pattern protects it, so the build will fail; fix it manually");
        }
        report.StillReferencedTargets += keptBrokenTargets.Count;

        // A live registration lost with a deleted implementation leaves its still-used service with
        // nothing registered — it compiles, and fails when the container first resolves a consumer —
        // unless another surviving registration names the same surviving type.
        foreach (var lost in lostRegistrations)
        {
            var survivingTypes = lost.InvolvedNodes.Where(n => !allDead.Contains(n)).ToHashSet(StringComparer.Ordinal);
            if (survivingTypes.Count == 0) { continue; }
            bool covered = graph.Statements.Any(o => o != lost && o.Kind == Constants.StatementKindDiRegister && !deadStmts.Contains(o)
                && r2.Contains(o) && o.InvolvedNodes.Any(survivingTypes.Contains));
            if (covered) { continue; }
            report.ServicesLeftUnregistered++;
            report.Warnings.Add($"{Constants.ServiceLeftUnregisteredPrefix} {string.Join(", ", survivingTypes.Select(n => FqnOf(graph, n)).Distinct())} stay in use but lose their only registration at {lost.FilePath}:{lost.StartLine} — the container fails when it first creates a consumer: {lost.Snippet}");
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
        // Member-level chain segments of SURVIVING statements whose refs died (an explicitly removed
        // mapped member): only the ".ForMember(...)" goes — the rest of the mapping stands.
        // Method-name positions of SURVIVING statements, per file: a deleted span must never contain
        // one (a live registration nested inside a dying chain's argument or lambda), or deleting the
        // span silently takes the live statement with it. The name — not the anchor — is used: a
        // nested registration can share its anchor statement with the dying one.
        var survivingNames = new Dictionary<string, List<(int Pos, StatementNode Stmt)>>(Constants.PathComparer);
        foreach (var s in graph.Statements)
        {
            if (deadStmts.Contains(s)) { continue; }
            if (!survivingNames.TryGetValue(s.FilePath, out var nlist)) { survivingNames[s.FilePath] = nlist = new List<(int, StatementNode)>(); }
            nlist.Add((s.NameStart, s));
        }
        bool SpanHidesSurvivor(string file, int start, int length, StatementNode self)
        {
            return survivingNames.TryGetValue(file, out var names)
                && names.Any(n => n.Stmt != self && n.Pos >= start && n.Pos < start + length);
        }
        var segmentRemovals = new List<(StatementNode Stmt, StatementSegment Seg)>();
        foreach (var stmt in graph.Statements)
        {
            if (deadStmts.Contains(stmt)) { continue; }
            if (allDead.Contains(stmt.ContainingMember) || HasDeadContainer(graph, stmt.ContainingMember, allDead)) { continue; }
            foreach (var seg in stmt.ChainSegments)
            {
                if (!seg.Refs.Any(allDead.Contains)) { continue; }
                if (SpanHidesSurvivor(stmt.FilePath, seg.Start, seg.Length, stmt))
                {
                    WarnOnce($"manual: {stmt.FilePath}:{seg.Line} — a surviving tracked statement is nested inside this segment; delete it manually: {seg.Snippet}");
                    continue;
                }
                segmentRemovals.Add((stmt, seg));
                var gone = seg.Refs.Where(allDead.Contains).Select(n => FqnOf(graph, n)).Distinct().ToList();
                WarnOnce($"map configuration removed: {stmt.FilePath}:{seg.Line} .{seg.MethodName}(...) references deleted {string.Join(", ", gone)} — the segment is removed, the rest of the mapping is kept");
            }
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
            if (SpanHidesSurvivor(stmt.FilePath, spanStart, spanLength, stmt))
            {
                WarnOnce($"manual: {stmt.FilePath}:{stmt.StartLine} — a surviving tracked statement is nested inside this span; delete it manually: {stmt.Snippet}");
                continue;
            }
            report.FileTextLengths[stmt.FilePath] = stmt.FileTextLength;
            report.FileTextHashes[stmt.FilePath] = stmt.FileTextHash;
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
                MarkerLineInsideToken = stmt.MarkerLineInsideToken,
            });
        }
        foreach (var (stmt, seg) in segmentRemovals)
        {
            report.FileTextLengths[stmt.FilePath] = stmt.FileTextLength;
            report.FileTextHashes[stmt.FilePath] = stmt.FileTextHash;
            report.Statements.Add(new StatementDeletion
            {
                Kind = stmt.Kind + " (segment)",
                File = stmt.FilePath,
                StartLine = seg.Line,
                EndLine = seg.EndLine,
                Snippet = seg.Snippet,
                SpanStart = seg.Start,
                SpanLength = seg.Length,
                WholeStatement = false,
                MarkerLineInsideToken = seg.MarkerLineInsideToken,
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
            if (graph.FilesWithDuplicateDecls.Contains(f))
            {
                report.Warnings.Add($"file kept (declares a type another project also declares under the same full name): {f} — its dead declarations are deleted individually");
                continue;
            }
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
            sym.InDeletableFile = sym.Parts.Count > 0 && sym.Parts.All(p => deletableFileSet.Contains(p.File));
        }

        var lostAssemblies = ComputeProjectCleanup(graph, report, allDead, keptInPlace, deletableFileSet, deadStmts);

        // csproj texts are hashed like source files: delete validates them before editing, so a
        // reference added between the analysis and the edit aborts instead of being edited blind.
        foreach (var path in report.PackageRefRemovals.Select(r => r.CsprojPath)
            .Concat(report.ProjectRefRemovals.Select(r => r.CsprojPath))
            .Concat(report.CompileEntryRemovals.Select(r => r.CsprojPath))
            .Concat(report.DeletableProjects.Select(p => p.CsprojPath))
            .Distinct(Constants.PathComparer))
        {
            if (path == null || !File.Exists(path)) { continue; }
            var ptext = Editor.ReadFileText(path, out _);
            report.FileTextLengths[path] = ptext.Length;
            report.FileTextHashes[path] = Editor.TextHash(ptext);
        }

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
            // "Left untouched" must be literal: pre-existing orphans this run DELETES (removal
            // targets, broken orphans) are excluded from the count.
            AlreadyOrphanedBefore = graph.Nodes.Count(n =>
                !graph.TestNodes.Contains(n) && !r1.Contains(n) && !rt1.Contains(n) && !allDead.Contains(n)),
        };
        return (report, lostAssemblies);
    }

    /// <summary>The first config-like file of the project whose text mentions <paramref name="needle"/>, or null.</summary>
    string ConfigMentionOf(Graph graph, string projectName, string needle)
    {
        if (!graph.ConfigTextsByProject.TryGetValue(projectName, out var texts)) { return null; }
        foreach (var (file, text) in texts)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) { return file; }
        }
        return null;
    }

    /// <summary>The first base class or interface of the type that comes from outside the solution
    /// and outside the plain BCL (Exception, IDisposable, IEquatable are not frameworks), or null.</summary>
    string FrameworkBaseOf(Graph graph, INamedTypeSymbol type)
    {
        bool IsFramework(INamedTypeSymbol t)
        {
            if (Graph.Id(t.OriginalDefinition) is { } tid && graph.Nodes.Contains(tid)) { return false; }
            var asm = t.ContainingAssembly?.Name;
            return asm != null && !Constants.EmptyContainerBenignAssemblies.Contains(asm);
        }
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (IsFramework(b)) { return SymbolNames.Fqn(b.OriginalDefinition); }
        }
        foreach (var i in type.AllInterfaces)
        {
            if (IsFramework(i)) { return SymbolNames.Fqn(i.OriginalDefinition); }
        }
        return null;
    }

    /// <summary>Why a surviving class is probably instantiated by a framework or container rather than
    /// by the solution's own code: it is built on an outside framework type or registered (explicitly
    /// or by an assembly scan), and no live production code creates it with "new". Null otherwise.</summary>
    string FrameworkCreatedReason(Graph graph, INamedTypeSymbol owner, string ownerId, HashSet<object> r2)
    {
        if (graph.CreatedBy.TryGetValue(ownerId, out var creators) && creators.Any(c => r2.Contains(c) && !graph.TestNodes.Contains(c))) { return null; }
        if (FrameworkBaseOf(graph, owner) is { } fw) { return $"is built on {fw}"; }
        if (graph.ScanMatchedTypes.TryGetValue(ownerId, out var scanSite)) { return $"is registered by the assembly scan at {scanSite}"; }
        var reg = graph.Statements.FirstOrDefault(s => s.Kind == Constants.StatementKindDiRegister && r2.Contains(s) && s.InvolvedNodes.Contains(ownerId));
        return reg != null ? $"is registered at {reg.FilePath}:{reg.StartLine}" : null;
    }

    /// <summary>True when any LIVE production code outside the type (or a live tracked statement)
    /// references the type or one of its members. A unit test or already-dead code naming the class
    /// proves nothing about whether a framework creates it.</summary>
    bool ReferencedInSolution(Graph graph, string typeId, HashSet<object> r1)
    {
        bool Inside(string id)
        {
            for (var cur = id; cur != null; cur = graph.ContainerOf.TryGetValue(cur, out var c) ? c : null)
            {
                if (cur == typeId) { return true; }
            }
            return false;
        }
        foreach (var (src, outs) in graph.Edges)
        {
            if (Inside(src) || graph.TestNodes.Contains(src) || !r1.Contains(src)) { continue; }
            if (outs.Any(Inside)) { return true; }
        }
        return graph.Statements.Any(s => r1.Contains(s) && s.InvolvedNodes.Any(Inside));
    }

    /// <summary>Every package the given package depends on, transitively (from project.assets.json).</summary>
    HashSet<string> PackageDependencyClosure(ProjectInfo pi, string package)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(package);
        while (stack.Count > 0)
        {
            if (!pi.PackageDependencies.TryGetValue(stack.Pop(), out var deps)) { continue; }
            foreach (var d in deps)
            {
                if (seen.Add(d)) { stack.Push(d); }
            }
        }
        return seen;
    }

    /// <summary>The first config-like file (any project) naming the project's assembly as an
    /// assembly-qualified type's assembly part (", Lib" followed by a quote, comma or line end), or null.</summary>
    string AssemblyConfigMention(Graph graph, string projectName)
    {
        if (!graph.Projects.TryGetValue(projectName, out var pi)) { return null; }
        var asm = string.IsNullOrEmpty(pi.AssemblyName) ? projectName : pi.AssemblyName;
        var pattern = new Regex(@",\s*" + Regex.Escape(asm) + @"\s*(?=[""',<]|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        foreach (var texts in graph.ConfigTextsByProject.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value))
        {
            foreach (var (file, text) in texts)
            {
                if (pattern.IsMatch(text)) { return file; }
            }
        }
        return null;
    }

    string FqnOf(Graph graph, string id)
    {
        return graph.Decl.TryGetValue(id, out var s) ? SymbolNames.Fqn(s) : id;
    }

    string TreeHashOf(SyntaxTree tree)
    {
        if (!_treeHashes.TryGetValue(tree, out var h)) { _treeHashes[tree] = h = Editor.TextHash(tree.GetText().ToString()); }
        return h;
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

    List<SourcePart> DeclarationParts(Graph graph, ISymbol s, string id, HashSet<string> allDead, HashSet<string> keptInPlace, List<string> warnings, Dictionary<string, int> fileLengths, Dictionary<string, string> fileHashes)
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
                    // Every dead sibling emits the whole-declaration span (identical spans merge at
                    // edit time), so the report lists each deleted field, not just the first.
                    node = container;
                }
            }
            var tree = node.SyntaxTree;
            if (string.IsNullOrEmpty(tree.FilePath)) { continue; }
            // A declaration in a tree with no file on disk (source generators): the generated text
            // regenerates from the surviving sources, so it is never edited.
            if (!File.Exists(tree.FilePath))
            {
                var gmsg = $"skipped: {SymbolNames.Fqn(s)} has a declaration in {tree.FilePath}, which does not exist on disk (source-generated) — it is left to regenerate";
                if (!warnings.Contains(gmsg)) { warnings.Add(gmsg); }
                continue;
            }
            // Generated code that DOES land on disk (gRPC stubs, XAML .g.cs) lives under obj/ and
            // regenerates on build: editing it is pointless and masks the real (regenerating) state.
            if (tree.FilePath.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                var omsg = $"skipped: {SymbolNames.Fqn(s)} is declared in {tree.FilePath}, generated under obj/ — it is left to regenerate";
                if (!warnings.Contains(omsg)) { warnings.Add(omsg); }
                continue;
            }
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
                // The enum itself survives (dead containers are skipped before this runs): members
                // after the deleted one without an explicit "= value" silently change their number.
                bool laterImplicit = false;
                for (int mi = idx + 1; mi < ed.Members.Count; mi++)
                {
                    if (ed.Members[mi].EqualsValue == null)
                    {
                        laterImplicit = true;
                        break;
                    }
                }
                if (idx >= 0 && laterImplicit)
                {
                    var emsg = $"enum values shift: deleting {SymbolNames.Fqn(s)} renumbers the later members of its enum that have no explicit value — persisted or serialized numeric values change; give them explicit values first if that matters";
                    if (!warnings.Contains(emsg)) { warnings.Add(emsg); }
                }
            }
            var lines = tree.GetLineSpan(node.Span);
            // A declaration that starts mid-line ("int a; int b;") shares its first line with other
            // code: its trailing line break belongs to that line and stays, or the next line (a
            // "#if", say) is joined onto it. Such a part also gets no marker line: the line above
            // would label the code that starts the shared line.
            var sourceText = tree.GetText();
            var startLine = sourceText.Lines[lines.StartLinePosition.Line];
            bool startsMidLine = sourceText.ToString(TextSpan.FromBounds(startLine.Start, node.Span.Start)).Trim().Length > 0;
            if (startsMidLine && node is not EnumMemberDeclarationSyntax)
            {
                int keptEnd = node.Span.End;
                foreach (var tr in node.GetTrailingTrivia())
                {
                    if (tr.IsKind(SyntaxKind.EndOfLineTrivia)) { break; }
                    keptEnd = tr.FullSpan.End;
                }
                spanEnd = Math.Min(spanEnd, keptEnd);
            }
            fileLengths[tree.FilePath] = tree.Length;
            fileHashes[tree.FilePath] = TreeHashOf(tree);
            parts.Add(new SourcePart
            {
                File = tree.FilePath,
                StartLine = lines.StartLinePosition.Line + 1,
                EndLine = lines.EndLinePosition.Line + 1,
                SpanStart = spanStart,
                SpanLength = spanEnd - spanStart,
                MarkerLineInsideToken = Editor.LineStartsInsideToken(tree, lines.StartLinePosition.Line),
                StartsMidLine = startsMidLine,
            });
        }
        return parts;
    }

    /// <summary>A file that blocks deleting the whole project: inactive #if regions and duplicate
    /// full names make the deletion's accounting incomplete for it. Checked both by directory (for
    /// declaration-less files) and by the dead nodes' declaring paths (for linked-in files).</summary>
    string ProtectedProjectFile(Graph graph, ProjectInfo pi, List<string> nodeIds)
    {
        var pdir = Path.GetDirectoryName(Path.GetFullPath(pi.FilePath)) ?? "";
        var prefix = pdir + Path.DirectorySeparatorChar;
        foreach (var f in graph.FilesWithInactiveCode.Concat(graph.FilesWithDuplicateDecls))
        {
            if (f.StartsWith(prefix, Constants.PathComparison)) { return f; }
        }
        foreach (var n in nodeIds)
        {
            if (!graph.Decl.TryGetValue(n, out var d)) { continue; }
            foreach (var r in d.DeclaringSyntaxReferences)
            {
                var f = r.SyntaxTree.FilePath;
                if (graph.FilesWithInactiveCode.Contains(f) || graph.FilesWithDuplicateDecls.Contains(f)) { return f; }
            }
        }
        return null;
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
    Dictionary<string, HashSet<string>> ComputeProjectCleanup(Graph graph, DeletionReport report, HashSet<string> allDead, HashSet<string> keptInPlace, HashSet<string> deletableFiles, HashSet<StatementNode> deadStmts)
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
            // The file-level protections hold at project level too: removing the csproj would drop a
            // protected file from the build as surely as deleting it.
            if (ProtectedProjectFile(graph, pi, nodeIds) is { } protectedFile)
            {
                report.Warnings.Add($"{pname}: every declaration is dead, but the project is NOT removed — {protectedFile} has inactive #if regions or duplicate type names; its code is deleted file by file, remove the project manually");
                continue;
            }
            // Snapshot of the project's C# files: delete refuses when one appeared since, instead of
            // stranding code the plan never saw.
            var sourceFiles = new List<string>();
            var pdir = Path.GetDirectoryName(Path.GetFullPath(pi.FilePath));
            try
            {
                if (pdir != null && Directory.Exists(pdir)) { sourceFiles.AddRange(Editor.EnumerateFilesPruned(pdir).Where(f => Path.GetExtension(f) == ".cs")); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report.Warnings.Add($"{pname}: every declaration is dead, but the project is NOT removed — its directory could not be listed ({e.Message}); remove it manually");
                continue;
            }
            deletableProjects.Add(pname);
            report.DeletableProjects.Add(new ProjectDeletion { Project = pname, CsprojPath = pi.FilePath, SourceFiles = sourceFiles });
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
                // Weak refs (implicit-conversion target types): no liveness meaning, but a surviving
                // caller still needs the surviving type's assembly to compile the converted call.
                if (stays && graph.WeakRefs.TryGetValue(n, out var weak))
                {
                    foreach (var t in weak)
                    {
                        if (!Stays(t)) { continue; }
                        if (!graph.ProjectsOf.TryGetValue(t, out var tps) || tps.Contains(pname)) { continue; }
                        var group = tps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                        if (needKeys.Add(string.Join("|", group))) { needs.Add(group); }
                        sp.UnionWith(group);
                    }
                }
            }
            // SURVIVING registrations/mappings reference their types only through suppressed spans
            // (rules, not edges), so their InvolvedNodes must count as needs too — or a still-needed
            // ProjectReference (and the matching using) is planned for removal.
            foreach (var stmt in graph.Statements)
            {
                if (deadStmts.Contains(stmt)) { continue; }
                if (!graph.ProjectsOf.TryGetValue(stmt.ContainingMember, out var sps) || !sps.Contains(pname)) { continue; }
                // Surviving member-level segments keep their text too, so their refs are needs;
                // a segment being removed (a ref died) is not.
                var stmtRefs = stmt.InvolvedNodes
                    .Concat(stmt.ChainSegments.Where(seg => !seg.Refs.Any(allDead.Contains)).SelectMany(seg => seg.Refs));
                foreach (var t in stmtRefs)
                {
                    if (t == null || !graph.ProjectsOf.TryGetValue(t, out var tps) || tps.Contains(pname)) { continue; }
                    var group = tps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (needKeys.Add(string.Join("|", group))) { needs.Add(group); }
                    sp.UnionWith(group);
                }
            }
            // Assembly-level attributes ("[assembly: Marker]") reference their types from no member
            // body, so no edge carries the need. Their targets are roots and always survive: the
            // providing project's reference (and its using) must stay.
            if (graph.AssemblyAttrRefs.TryGetValue(pname, out var attrRefs))
            {
                foreach (var t in attrRefs)
                {
                    if (!graph.ProjectsOf.TryGetValue(t, out var tps) || tps.Contains(pname)) { continue; }
                    var group = tps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (needKeys.Add(string.Join("|", group))) { needs.Add(group); }
                    sp.UnionWith(group);
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
                // A package named in configuration (a Serilog sink in appsettings, a provider in
                // web.config) can be loaded at runtime with no compile-time use left.
                if (ConfigMentionOf(graph, pname, pkg) is { } cfgFile)
                {
                    report.Warnings.Add($"{pname}: PackageReference {pkg} lost its last compile-time user but its name appears in {cfgFile} — left in place, remove manually if appropriate");
                    continue;
                }
                pc.Add(pkg);
            }
            // A package another REMAINING package depends on is often listed only to pin its version
            // upward (a security fix): removing the line silently drops it back to the minimum the
            // other package accepts. Kept, to a fixed point (a kept package's own dependencies count).
            bool pinKept = true;
            while (pinKept)
            {
                pinKept = false;
                foreach (var pkg in pc.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    var dependent = pi.PackageReferences
                        .Where(other => !pc.Contains(other) && !StringComparer.OrdinalIgnoreCase.Equals(other, pkg))
                        .FirstOrDefault(other => PackageDependencyClosure(pi, other).Contains(pkg));
                    if (dependent == null) { continue; }
                    pc.Remove(pkg);
                    pinKept = true;
                    report.Warnings.Add($"{pname}: PackageReference {pkg} lost its last direct user but {dependent} depends on it — left in place (it may pin a newer version), remove manually if appropriate");
                }
            }
            bool referencingIsTest = nodesByProject.TryGetValue(pname, out var ownNodes) && ownNodes.Any(graph.TestNodes.Contains);
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
                // An assembly named in configuration ("Lib.LibModule, Lib" in web.config) is loaded at
                // runtime: dropping the reference stops it shipping. Any project's config counts, since
                // the assembly flows transitively into every consuming application.
                if (!targetDeleted && AssemblyConfigMention(graph, refName) is { } refCfg)
                {
                    report.Warnings.Add($"{pname}: ProjectReference {refName} lost its last compile-time user but its assembly is named in {refCfg} — left in place, remove manually if appropriate");
                    continue;
                }
                // Entry points a framework discovers in the referenced assembly at runtime
                // (controllers as application parts, modules, profiles, migrations) stop shipping
                // with the reference. A test project does not run the application's discovery.
                if (!targetDeleted && !referencingIsTest && nodesByProject.TryGetValue(refName, out var refNodes)
                    && refNodes.FirstOrDefault(n => graph.Roots.Contains(n) && Stays(n)
                        && Constants.RuntimeDiscoveredRootReasons.Contains(graph.RootReasons.GetValueOrDefault(n) ?? "")) is { } liveRoot)
                {
                    report.Warnings.Add($"{pname}: ProjectReference {refName} lost its last compile-time user but {refName} still holds entry points ({FqnOf(graph, liveRoot)}: {graph.RootReasons.GetValueOrDefault(liveRoot)}) — left in place, remove manually if they are not needed at runtime");
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
                var pdir = Path.GetDirectoryName(pi.FilePath) ?? "";
                var rel = Path.GetRelativePath(pdir, file).Replace('\\', '/');
                var match = pi.CompileItems.FirstOrDefault(ci => StringComparer.OrdinalIgnoreCase.Equals(ci.Replace('\\', '/'), rel));
                if (match != null)
                {
                    report.CompileEntryRemovals.Add(new ReferenceRemoval { Project = pname, CsprojPath = pi.FilePath, Reference = match });
                }
                else if (!pi.DefaultCompileItems && file.StartsWith(pdir + Path.DirectorySeparatorChar, Constants.PathComparison))
                {
                    // An explicit-listing project owns the file but none of its <Compile> entries
                    // matched textually (MSBuild property in the Include, odd relative form): the
                    // file is deleted, so a dangling entry breaks the build with CS2001.
                    report.Warnings.Add($"{pname}: deleted file {file} has no textually matching <Compile> entry — if this project lists files explicitly, remove the entry manually or the build fails (CS2001)");
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
        // Liveness is tracked PER ASSEMBLY: a compiled reference only carries the namespaces its own
        // surviving types live in, so another project's types under the same namespace name cannot
        // keep a "using" alive in a consumer that does not reference that project.
        var nameDeclared = new HashSet<string>(StringComparer.Ordinal);
        var nameAliveIn = new HashSet<string>(StringComparer.Ordinal);
        void MarkName(string asm, string name, bool alive)
        {
            nameDeclared.Add(name);
            if (alive) { nameAliveIn.Add(asm + "|" + name); }
        }
        foreach (var (id, sym) in graph.Decl)
        {
            if (sym is not INamedTypeSymbol t) { continue; }
            bool alive = Stays(id);
            // A type in a file linked into several projects is compiled into EACH of their assemblies;
            // the declaring symbol only names the first one.
            var asms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { t.ContainingAssembly?.Name ?? "" };
            if (graph.ProjectsOf.TryGetValue(id, out var declaringProjects))
            {
                foreach (var dp in declaringProjects)
                {
                    if (graph.Projects.TryGetValue(dp, out var dpi) && dpi.AssemblyName.Length > 0) { asms.Add(dpi.AssemblyName); }
                }
            }
            var ns = t.ContainingType == null && t.ContainingNamespace?.IsGlobalNamespace == false ? t.ContainingNamespace.ToDisplayString() : "";
            foreach (var tasm in asms)
            {
                MarkName(tasm, SymbolNames.Fqn(t), alive);
                for (var cur = ns; cur.Length > 0;)
                {
                    MarkName(tasm, cur, alive);
                    var dot = cur.LastIndexOf('.');
                    cur = dot < 0 ? "" : cur[..dot];
                }
            }
        }

        // A linked file is compiled into several projects, one UsingRef entry each: the directive
        // goes only when EVERY surviving project that compiles the file has lost all its providers.
        foreach (var g in graph.UsingRefs.GroupBy(u => u.File + "#" + u.SpanStart, Constants.PathComparer))
        {
            var relevant = g.Where(u => !deletableProjects.Contains(u.Project)).ToList();
            if (relevant.Count == 0) { continue; }
            var u0 = relevant[0];
            if (deletableFiles.Contains(u0.File)) { continue; }
            bool allGone = relevant.All(u => u.Providers.Count > 0
                && goneByProject.TryGetValue(u.Project, out var gone) && u.Providers.All(gone.Contains));
            bool nameDies = nameDeclared.Contains(u0.ResolvedName)
                && relevant.All(u => u.Providers.Count > 0 && u.Providers.All(solutionAssemblies.Contains)
                    && !u.Providers.Any(p => nameAliveIn.Contains(p + "|" + u0.ResolvedName)));
            if (allGone || nameDies)
            {
                report.FileTextLengths[u0.File] = u0.FileTextLength;
                report.FileTextHashes[u0.File] = u0.FileTextHash;
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
                if (narrowed.Count > 1)
                {
                    // Same parameter list on several overloads (they differ only by generic type
                    // parameters): removing all of them on a signature the user meant for one is over-deletion.
                    throw new InvalidOperationException(
                        $"'{key}{signature}' matches {narrowed.Count} overloads — they differ only by generic type parameters:\n  " +
                        string.Join("\n  ", narrowed.Select(m => SymbolNames.Detailed(graph.Decl[m]))));
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

    /// <summary>"(Invoice,int)" / "(refint)" — parameter types with ref kinds, minimal qualification,
    /// no spaces (the user's signature has its spaces stripped before comparing, so "(ref int)" matches).</summary>
    string ShortSignature(IMethodSymbol ms)
    {
        // Nullable reference annotations are dropped ("string?" matches "string"); Nullable<T> value types keep their "?".
        return "(" + string.Join(",", ms.Parameters.Select(p => (RefPrefix(p.RefKind) + p.Type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).Replace(" ", ""))) + ")";
    }

    string RefPrefix(RefKind kind)
    {
        return kind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => "",
        };
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
    HashSet<string> Poison(Graph graph, HashSet<string> seeds, HashSet<object> r2, HashSet<object> rt1, Dictionary<string, List<string>> membersOf, HashSet<string> keptProtected)
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
            if (seeds.Contains(id) || keptProtected.Contains(id) || !dead.Add(id)) { return; }
            queue.Enqueue(id);
            // A dead member of a type that is itself out of use takes the whole type down — except
            // in test classes (one broken test must not delete its healthy siblings) and types the
            // TEST world still reaches: a sibling member live only through tests references nothing
            // deleted, so killing it with the container would over-delete. Such a type still dies
            // when EVERY member died (empty-shell pass below), or through the test-helper cascade
            // once the tests that kept it alive are gone.
            if (graph.ContainerOf.TryGetValue(id, out var container)
                && !r2.Contains(container) && !rt1.Contains(container) && !seeds.Contains(container)
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
            // provided it is itself out of use. A PRODUCTION type the test world still reaches
            // keeps its shell (a test may construct it): over-keeping an empty class is safe,
            // deleting it would take healthy tests along. Test classes still die whole.
            foreach (var (type, members) in membersOf)
            {
                // A keep-protected type never dies (Kill refuses it): skipping it here also keeps this
                // loop from re-trying it forever.
                if (dead.Contains(type) || seeds.Contains(type) || r2.Contains(type) || keptProtected.Contains(type)) { continue; }
                if (rt1.Contains(type) && !graph.TestNodes.Contains(type)) { continue; }
                if (!graph.Nodes.Contains(type)) { continue; }
                if (members.Count > 0 && members.All(m => dead.Contains(m) || seeds.Contains(m)))
                {
                    Kill(type);
                    if (dead.Contains(type)) { changed = true; }
                }
            }
        }
        return dead;
    }
}
