using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DeadCodeCleaner;

public sealed class GraphBuilder
{
    readonly Graph _graph = new();
    readonly AnalyzerConfig _config;
    readonly Action<string> _log;
    readonly List<(INamedTypeSymbol Type, bool IsTest)> _types = new();
    readonly HashSet<InvocationExpressionSyntax> _processedInvocations = new();
    readonly Dictionary<SyntaxTree, string> _treeHashes = new();
    readonly HashSet<string> _solutionAssemblies = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<SyntaxNode, AnchorInfo> _anchors = new();
    readonly HashSet<SyntaxNode> _scanRegions = new();
    readonly Dictionary<SyntaxTree, Compilation> _treeCompilations = new();

    /// <summary>Everything known about one anchor statement shared by tracked registrations: which
    /// chain invocations the registrations claim as their own, and the statements on the anchor.</summary>
    sealed class AnchorInfo
    {
        public StatementSyntax InnerStatement;
        public HashSet<InvocationExpressionSyntax> Claimed { get; } = new();
        public List<StatementNode> Statements { get; } = new();
    }

    public GraphBuilder(AnalyzerConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public async Task<Graph> Build(Solution solution)
    {
        var compilations = new List<(Compilation Comp, bool IsTest, string ProjectName)>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                _graph.Warnings.Add($"{project.Name}: not a C# project — its code and its references into C# code are INVISIBLE to the analysis");
                _graph.ProjectsNotAnalyzed.Add(project.Name);
                continue;
            }
            if (!seenProjects.Add(project.FilePath ?? project.Name))
            {
                // Multi-targeting duplicate: conditional (#if) code of the other frameworks is invisible.
                var w = $"{project.Name}: multi-targets — only the first target framework was analyzed";
                if (!_graph.Warnings.Contains(w)) { _graph.Warnings.Add(w); }
                continue;
            }
            var comp = await project.GetCompilationAsync();
            if (comp == null)
            {
                _graph.Warnings.Add($"{project.Name}: no compilation could be built — the project is INVISIBLE to the analysis");
                _graph.ProjectsNotAnalyzed.Add(project.Name);
                continue;
            }
            // A test project references a test library AND contains at least one actual test; its name
            // never counts. A library referencing a test framework without any test (shared test
            // helpers) is production code.
            bool referencesTestLibrary = ReferencesTestLibrary(project);
            bool isTest = referencesTestLibrary && ContainsTest(comp);
            if (referencesTestLibrary && !isTest)
            {
                _graph.Warnings.Add($"{project.Name}: references a test library but contains no test — treated as PRODUCTION code");
            }
            compilations.Add((comp, isTest, project.Name));
            foreach (var tree in comp.SyntaxTrees) { _treeCompilations.TryAdd(tree, comp); }
            _solutionAssemblies.Add(comp.Assembly.Name);
            CollectProjectInfo(project);
            if (_graph.Projects.TryGetValue(project.Name, out var pinfo))
            {
                pinfo.AssemblyName = comp.Assembly.Name;
            }
            var errors = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            _log($"  {project.Name}: {project.DocumentIds.Count} documents, {comp.ExternalReferences.Length} references, {errors.Count} compile errors" + (isTest ? " [test]" : ""));
            if (errors.Count > 0)
            {
                _graph.Warnings.Add($"{project.Name}: {errors.Count} compile errors — analysis may miss references (first: {errors[0].GetMessage()})");
                _graph.ProjectsWithCompileErrors.Add(project.Name);
                foreach (var e in errors.Take(Constants.ShownCompileErrors)) { _log($"      error: {e.GetMessage()}"); }
            }
        }

        WarnAboutNonCompiledContent();

        // Phase 1: collect all nodes first (rule construction needs the full node set). Production
        // compilations go first: a file linked into a production AND a test project is owned by the
        // first compilation that declares it, and production liveness must win.
        foreach (var (comp, isTest, projectName) in compilations.OrderBy(c => c.IsTest)) { CollectNodes(comp, isTest, projectName); }

        // Phase 2: structural rules and roots.
        foreach (var (type, isTest) in _types) { AddStructuralRulesAndRoots(type, isTest); }
        foreach (var (comp, isTest, pname) in compilations)
        {
            var ep = comp.GetEntryPoint(CancellationToken.None);
            if (ep != null && Graph.Id(ep) is { } epId)
            {
                // The synthesized Main of top-level statements is implicit; admit it explicitly.
                _graph.Nodes.Add(epId);
                _graph.AddNodeProject(epId, pname);
                _graph.Decl.TryAdd(epId, ep.OriginalDefinition);
                if (Graph.Id(ep.ContainingType) is { } eptId) { _graph.ContainerOf.TryAdd(epId, eptId); }
                if (isTest) { _graph.TestNodes.Add(epId); }
                _graph.AddRoot(epId, "entry point", isTest);
                if (Graph.Id(ep.ContainingType) is { } tId && _graph.Nodes.Contains(tId))
                {
                    _graph.AddRoot(tId, "entry point type", isTest);
                }
            }
        }
        ApplyConfigRoots();

        // Phase 3: edges and co-dependent statements.
        foreach (var (comp, isTest, projectName) in compilations)
        {
            foreach (var tree in comp.SyntaxTrees)
            {
                WalkTree(comp, tree, isTest, projectName);
                CollectUsings(comp, tree, projectName);
            }
        }

        // Resolve ProjectReference names: a solution can display a project under a different name
        // than its csproj file, so references are matched by resolved file path (filename fallback).
        var byPath = new Dictionary<string, string>(Constants.PathComparer);
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pname, pinfo2) in _graph.Projects)
        {
            byPath.TryAdd(Path.GetFullPath(pinfo2.FilePath), pname);
            byFileName.TryAdd(Path.GetFileNameWithoutExtension(pinfo2.FilePath), pname);
        }
        foreach (var pinfo2 in _graph.Projects.Values)
        {
            var resolved = new List<(string Name, string Include)>();
            foreach (var include in pinfo2.ProjectReferences.Values.ToList())
            {
                var fileName = Path.GetFileNameWithoutExtension(include);
                var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pinfo2.FilePath) ?? "", include.Replace('\\', '/')));
                var rname = byPath.TryGetValue(full, out var n1) ? n1 : byFileName.TryGetValue(fileName, out var n2) ? n2 : fileName;
                resolved.Add((rname, include));
            }
            pinfo2.ProjectReferences.Clear();
            foreach (var (name, include) in resolved) { pinfo2.ProjectReferences[name] = include; }
        }

        // Phase 4: whole-anchor safety. Deleting the whole anchor statement is only safe when it is
        // a plain expression statement whose entire call chain is claimed by tracked registrations;
        // anything else (return/var anchors, untracked chained calls) must survive partially.
        foreach (var ai in _anchors.Values)
        {
            bool allowed = ai.InnerStatement is ExpressionStatementSyntax es
                && TryGetChain(es.Expression, out var chain) && chain.All(ai.Claimed.Contains);
            foreach (var s in ai.Statements) { s.WholeAnchorAllowed = allowed && s.ManualReason == null; }
        }

        ApplyScanRules(compilations.Select(c => c.Comp.Assembly.Name).ToList());
        ApplyMentionRoots();
        return _graph;
    }

    /// <summary>The top-level invocation chain of an expression statement ("a.B().C()" => [C, B]),
    /// or false when the expression is not a plain receiver chain (assignment, cast, creation, ...).</summary>
    bool TryGetChain(ExpressionSyntax expr, out List<InvocationExpressionSyntax> chain)
    {
        chain = new List<InvocationExpressionSyntax>();
        var cur = expr;
        while (true)
        {
            if (cur is InvocationExpressionSyntax inv)
            {
                chain.Add(inv);
                if (inv.Expression is MemberAccessExpressionSyntax ma)
                {
                    cur = ma.Expression;
                    continue;
                }
                return inv.Expression is SimpleNameSyntax;
            }
            if (cur is MemberAccessExpressionSyntax outer)
            {
                cur = outer.Expression;
                continue;
            }
            return cur is IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax;
        }
    }

    void WarnAboutNonCompiledContent()
    {
        int found = 0;
        var seenFiles = new HashSet<string>(Constants.PathComparer);
        var textsByDir = new Dictionary<string, List<(string File, string Text)>>(Constants.PathComparer);
        foreach (var pinfo in _graph.Projects.Values)
        {
            var dir = Path.GetDirectoryName(pinfo.FilePath);
            if (dir == null) { continue; }
            if (!textsByDir.TryGetValue(dir, out var texts))
            {
                textsByDir[dir] = texts = new List<(string, string)>();
                try
                {
                    foreach (var f in Editor.EnumerateFilesPruned(dir))
                    {
                        if (Constants.NonCompiledContentExtensions.Contains(Path.GetExtension(f)) && seenFiles.Add(f)) { found++; }
                        // View/markup text is kept for the deleted-symbol mention scan: views
                        // compile at runtime and are invisible to the compiler AND the verifier.
                        if (Constants.ViewFileExtensions.Contains(Path.GetExtension(f)) && !_graph.ViewTexts.ContainsKey(f)) { _graph.ViewTexts[f] = ReadConfigText(f); }
                        // Hand-written scripts (minified bundles are noise): scanned for removal
                        // targets' names, since a URL like "/Admin/GetRoles" is invisible to the compiler.
                        if (Path.GetExtension(f) == Constants.ScriptExtension && !f.EndsWith(Constants.MinifiedScriptSuffix, StringComparison.OrdinalIgnoreCase)
                            && !_graph.ScriptTexts.ContainsKey(f) && new FileInfo(f).Length <= Constants.MaxScannedConfigFileBytes)
                        {
                            _graph.ScriptTexts[f] = ReadConfigText(f);
                        }
                        if (!IsConfigLikeFile(f)) { continue; }
                        var txt = ReadConfigText(f);
                        // This tool's own reports (saved under any --json name) mention every deleted
                        // type and removed package by design: scanning them as config would turn the
                        // previous run's output into false "mentioned in config" blocks.
                        if (txt.Contains(Constants.ReportGeneratorJsonFragment, StringComparison.Ordinal)) { continue; }
                        texts.Add((f, txt));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Unreadable directory: content scan is best-effort.
                }
            }
            _graph.ConfigTextsByProject[pinfo.Name] = texts;
        }
        if (found > 0)
        {
            _graph.Warnings.Add($"solution contains {found} non-compiled content file(s) (views / WebForms / XAML / service wiring) — references from them are INVISIBLE to this analysis; review the report before deleting");
        }
    }

    /// <summary>Config-like files that can wire up types and packages by name: .config, and .json
    /// except NuGet/tooling artifacts and this tool's own report.</summary>
    bool IsConfigLikeFile(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".config", StringComparison.OrdinalIgnoreCase)) { return true; }
        if (!string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase)) { return false; }
        var name = Path.GetFileName(path);
        if (string.Equals(name, Constants.DefaultReportFileName, StringComparison.OrdinalIgnoreCase)) { return false; }
        if (name.EndsWith(".assets.json", StringComparison.OrdinalIgnoreCase)) { return false; }
        return true;
    }

    string ReadConfigText(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > Constants.MaxScannedConfigFileBytes)
            {
                _graph.Warnings.Add($"file not scanned (over {Constants.MaxScannedConfigFileBytes} bytes): {path} — mentions of deleted types, members or packages in it go unnoticed");
                return "";
            }
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    void CollectUsings(Compilation comp, SyntaxTree tree, string projectName)
    {
        var model = comp.GetSemanticModel(tree);
        var sourceText = tree.GetText();
        foreach (var u in tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (u.Name == null) { continue; }
            var sym = model.GetSymbolInfo(u.Name).Symbol;
            var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // "using Sub;" inside namespace A resolves to A.Sub: liveness must be looked up under
            // the RESOLVED name, never the written one.
            var resolvedName = u.Name.ToString();
            if (sym is INamespaceSymbol nsSym)
            {
                foreach (var part in nsSym.ConstituentNamespaces)
                {
                    if (part.ContainingAssembly?.Name is { } a) { providers.Add(a); }
                }
                resolvedName = nsSym.ToDisplayString();
            }
            else if (sym is ITypeSymbol t)
            {
                if (t.ContainingAssembly?.Name is { } ta) { providers.Add(ta); }
                resolvedName = SymbolNames.Fqn(t);
            }
            var lines = u.GetLocation().GetLineSpan();
            // Char span for deletion: FullSpan minus leading #directives — clamped to the start of
            // the directive's own line, so a file banner above the first using is never deleted.
            int spanStart = u.FullSpan.Start;
            var leading = u.GetLeadingTrivia();
            for (int ti = leading.Count - 1; ti >= 0; ti--)
            {
                if (leading[ti].IsDirective)
                {
                    spanStart = leading[ti].FullSpan.End;
                    break;
                }
            }
            int lineStart = sourceText.Lines[lines.StartLinePosition.Line].Start;
            if (lineStart > spanStart) { spanStart = lineStart; }
            _graph.UsingRefs.Add(new UsingRef
            {
                Project = projectName,
                File = tree.FilePath,
                Line = lines.StartLinePosition.Line + 1,
                Namespace = u.Name.ToString(),
                ResolvedName = resolvedName,
                Providers = providers,
                SpanStart = spanStart,
                SpanLength = u.FullSpan.End - spanStart,
                FileTextLength = sourceText.Length,
                FileTextHash = TreeHash(tree),
            });
        }
    }

    void CollectProjectInfo(Project project)
    {
        if (project.FilePath == null || !File.Exists(project.FilePath)) { return; }
        var info = new ProjectInfo { Name = project.Name, FilePath = project.FilePath };
        try
        {
            var xml = XDocument.Load(project.FilePath);
            foreach (var el in xml.Descendants())
            {
                var include = el.Attribute("Include")?.Value;
                if (include == null) { continue; }
                // A Condition anywhere up the chain makes the entry build-configuration dependent:
                // only one configuration is analyzed, so such entries are never removed.
                bool conditional = el.AncestorsAndSelf().Any(a => a.Attribute("Condition") != null);
                if (el.Name.LocalName == "PackageReference")
                {
                    if (!info.PackageReferences.Contains(include)) { info.PackageReferences.Add(include); }
                    if (conditional) { info.ConditionalPackages.Add(include); }
                }
                else if (el.Name.LocalName == "ProjectReference")
                {
                    // Keyed by the Include path until resolution: two references whose FILES share a
                    // name (A/Core.csproj, B/Core.csproj) must not collapse into one entry.
                    info.ProjectReferences[include] = include;
                    if (conditional) { info.ConditionalProjectRefIncludes.Add(include.Replace('\\', '/')); }
                }
                else if (el.Name.LocalName == "Compile" && !info.CompileItems.Contains(include))
                {
                    info.CompileItems.Add(include);
                }
            }
            ReadWarningsAsErrors(project.FilePath, info);
            // SDK-style projects include every .cs file by default; an explicit <Compile> there is the
            // exception (a linked file), not a listing every deleted file must appear in.
            bool sdkStyle = xml.Root?.Attribute("Sdk") != null
                || xml.Descendants().Any(d => d.Name.LocalName is "Sdk" or "Import" && d.Attribute("Sdk") != null);
            bool defaultsOff = xml.Descendants().Any(d => d.Name.LocalName is "EnableDefaultCompileItems" or "EnableDefaultItems" && StringComparer.OrdinalIgnoreCase.Equals(d.Value.Trim(), "false"));
            info.DefaultCompileItems = sdkStyle && !defaultsOff;
            var assetsPath = Path.Combine(Path.GetDirectoryName(project.FilePath), "obj", "project.assets.json");
            if (File.Exists(assetsPath))
            {
                var assets = JsonSerializer.Deserialize<ProjectAssetsFile>(File.ReadAllText(assetsPath), Constants.CaseInsensitiveJson);
                var targets = assets?.Targets?.Values ?? Enumerable.Empty<Dictionary<string, ProjectAssetsLibrary>>();
                foreach (var target in targets.Take(1))
                {
                    foreach (var (entryName, lib) in target)
                    {
                        var slash = entryName.IndexOf('/');
                        if (slash <= 0 || lib == null) { continue; }
                        var pkg = entryName[..slash];
                        if (lib.Compile != null)
                        {
                            foreach (var libPath in lib.Compile.Keys)
                            {
                                var file = Path.GetFileNameWithoutExtension(libPath);
                                if (string.IsNullOrEmpty(file) || file == "_") { continue; }
                                if (!info.PackageAssemblies.TryGetValue(pkg, out var list))
                                {
                                    info.PackageAssemblies[pkg] = list = new List<string>();
                                }
                                if (!list.Contains(file)) { list.Add(file); }
                            }
                        }
                        if (lib.Dependencies != null)
                        {
                            if (!info.PackageDependencies.TryGetValue(pkg, out var deps))
                            {
                                info.PackageDependencies[pkg] = deps = new List<string>();
                            }
                            foreach (var dep in lib.Dependencies.Keys)
                            {
                                if (!deps.Contains(dep)) { deps.Add(dep); }
                            }
                        }
                    }
                }
            }
            else if (info.PackageReferences.Count > 0)
            {
                _graph.Warnings.Add($"{project.Name}: no obj/project.assets.json (run restore) — package cleanup skipped for this project");
            }
            if (File.Exists(Path.Combine(Path.GetDirectoryName(project.FilePath), "packages.config")))
            {
                _graph.Warnings.Add($"{project.Name}: uses packages.config — package cleanup not supported for it yet");
            }
        }
        catch (Exception e) when (e is XmlException or JsonException or IOException or UnauthorizedAccessException)
        {
            _graph.Warnings.Add($"{project.Name}: could not read project facts ({e.Message})");
        }
        _graph.Projects[project.Name] = info;
    }

    /// <summary>The project file plus the Directory.Build.props files MSBuild imports into it: the
    /// nearest one above the project, then the next one up only while the previous one imports it
    /// (an Import naming Directory.Build.props or GetPathOfFileAbove).</summary>
    List<string> ProjectBuildFiles(string csprojPath)
    {
        var files = new List<string> { csprojPath };
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath)); dir != null; dir = Path.GetDirectoryName(dir))
        {
            var props = Path.Combine(dir, Constants.DirectoryBuildPropsFileName);
            if (!File.Exists(props)) { continue; }
            files.Add(props);
            bool importsParent;
            try
            {
                importsParent = XDocument.Load(props).Descendants().Any(d => d.Name.LocalName == "Import"
                    && (d.Attribute("Project")?.Value is { } ip)
                    && (ip.Contains(Constants.DirectoryBuildPropsFileName, StringComparison.OrdinalIgnoreCase) || ip.Contains("GetPathOfFileAbove", StringComparison.Ordinal)));
            }
            catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
            {
                // Unreadable: assume the chain continues (reading more settings only verifies stricter).
                importsParent = true;
            }
            if (!importsParent) { break; }
        }
        return files;
    }

    /// <summary>The analysis neutralizes warnings-as-errors so design-time builds load; the verifier
    /// needs the REAL settings back. Read from the csproj and the Directory.Build.props it imports.
    /// Conservative: a conditional "true" counts as on (a stricter verifier only refuses more).</summary>
    void ReadWarningsAsErrors(string csprojPath, ProjectInfo info)
    {
        foreach (var file in ProjectBuildFiles(csprojPath))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(file);
            }
            catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
            {
                _graph.Warnings.Add($"{info.Name}: could not read {file} for warnings-as-errors settings ({e.Message}) — verification may miss warnings the real build treats as errors");
                continue;
            }
            foreach (var el in doc.Descendants())
            {
                var value = el.Value.Trim();
                switch (el.Name.LocalName)
                {
                    case "TreatWarningsAsErrors":
                        if (StringComparer.OrdinalIgnoreCase.Equals(value, "true")) { info.TreatWarningsAsErrors = true; }
                        break;
                    case "WarningsAsErrors":
                        info.WarningsAsErrors.UnionWith(SplitWarningIds(value));
                        break;
                    case "WarningsNotAsErrors":
                        info.WarningsNotAsErrors.UnionWith(SplitWarningIds(value));
                        break;
                }
            }
        }
    }

    IEnumerable<string> SplitWarningIds(string value)
    {
        var ids = new List<string>();
        foreach (var id in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (id.StartsWith("$(", StringComparison.Ordinal)) { continue; }
            if (StringComparer.OrdinalIgnoreCase.Equals(id, Constants.NullableWarningGroup))
            {
                // The compiler's own command-line parser knows which ids the group stands for.
                var parsed = CSharpCommandLineParser.Default.Parse(new[] { "/warnaserror:" + Constants.NullableWarningGroup }, Directory.GetCurrentDirectory(), sdkDirectory: null);
                ids.AddRange(parsed.CompilationOptions.SpecificDiagnosticOptions.Keys);
                continue;
            }
            ids.Add(id.All(char.IsDigit) ? "CS" + id.PadLeft(4, '0') : id);
        }
        return ids;
    }

    bool ReferencesTestLibrary(Project project)
    {
        // Only DIRECT test-framework dependencies count: transitive flow through a shared project
        // reference must not turn a production project into a test project. The csproj is parsed as
        // XML, never matched as raw text: a commented-out package is not evidence.
        try
        {
            if (project.FilePath == null || !File.Exists(project.FilePath)) { return false; }
            // Test packages are often added for a whole tests/ folder by its Directory.Build.props.
            foreach (var el in ProjectBuildFiles(project.FilePath).SelectMany(f => XDocument.Load(f).Descendants()))
            {
                var ln = el.Name.LocalName;
                // <Project Sdk="MSTest.Sdk/3.6.4">: the SDK brings the test framework and IsTestProject.
                if (ln is "Project" or "Sdk" or "Import" && ((string)el.Attribute("Sdk") ?? (ln == "Sdk" ? (string)el.Attribute("Name") : null)) is { } sdk
                    && sdk.Split('/')[0].Trim() == Constants.MsTestSdkName)
                {
                    return true;
                }
                if (ln is "PackageReference" or "Reference")
                {
                    var include = (string)el.Attribute("Include") ?? (string)el.Attribute("Update") ?? "";
                    var id = include.Split(',')[0].Trim();
                    if (Regex.IsMatch(id, Constants.TestPackageIdPattern, RegexOptions.IgnoreCase)) { return true; }
                }
                else if (ln == "IsTestProject" && Regex.IsMatch(el.Value, @"^\s*true\s*$", RegexOptions.IgnoreCase)) { return true; }
                else if (ln == "ProjectTypeGuids" && el.Value.Contains(Constants.TestProjectTypeGuid, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { /* unreadable or malformed project file: no evidence */ }
        return false;
    }

    /// <summary>True when the compilation declares at least one actual test: a test-attributed method,
    /// or a test-fixture-attributed class.</summary>
    bool ContainsTest(Compilation comp)
    {
        foreach (var type in AllSourceTypes(comp.Assembly.GlobalNamespace))
        {
            if (type.GetAttributes().Any(a => AttributeIn(a, Constants.TestClassAttributeNames))) { return true; }
            foreach (var m in type.GetMembers())
            {
                if (m is IMethodSymbol && m.GetAttributes().Any(a => AttributeIn(a, Constants.TestMethodAttributeNames))) { return true; }
            }
        }
        return false;
    }

    /// <summary>The attribute's class name without its "Attribute" suffix ("Fact" for FactAttribute).</summary>
    string ShortAttributeName(AttributeData attr)
    {
        return ShortAttributeName(attr.AttributeClass?.Name ?? "");
    }

    string ShortAttributeName(string an)
    {
        return an.EndsWith("Attribute", StringComparison.Ordinal) ? an[..^9] : an;
    }

    /// <summary>True when the attribute's class, or any class it derives from, has one of the short
    /// names: test frameworks are extended by subclassing ([SkippableFact], [RetryFact], [StaFact]).</summary>
    bool AttributeIn(AttributeData attr, HashSet<string> names)
    {
        for (var c = attr.AttributeClass; c != null; c = c.BaseType)
        {
            if (names.Contains(ShortAttributeName(c.Name))) { return true; }
        }
        return false;
    }

    // ---------------------------------------------------------------- Phase 1

    void CollectNodes(Compilation comp, bool isTest, string projectName)
    {
        foreach (var type in AllSourceTypes(comp.Assembly.GlobalNamespace))
        {
            if (Graph.Id(type) is not { } typeId) { continue; }
            // A linked file compiles the same type into several projects: the first compilation owns
            // the declaration facts, but every project records membership (for reference cleanup).
            bool firstSeen = _graph.Nodes.Add(typeId);
            _graph.AddNodeProject(typeId, projectName);
            if (!firstSeen)
            {
                // A second declaration of an already-known full name in a DIFFERENT file: the graph
                // only tracks the first symbol's files, so this file's whole-file accounting is
                // incomplete and it must never be deleted whole.
                var knownPaths = new HashSet<string>(_graph.Decl[typeId].DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath), Constants.PathComparer);
                foreach (var dr in type.DeclaringSyntaxReferences)
                {
                    var fp = dr.SyntaxTree.FilePath;
                    if (!string.IsNullOrEmpty(fp) && !knownPaths.Contains(fp)) { _graph.FilesWithDuplicateDecls.Add(fp); }
                }
            }
            if (firstSeen)
            {
                _graph.Decl[typeId] = type;
                if (type.ContainingType != null && Graph.Id(type.ContainingType) is { } outerId)
                {
                    _graph.ContainerOf[typeId] = outerId;
                }
                _types.Add((type, isTest));
                if (isTest) { _graph.TestNodes.Add(typeId); }
            }
            bool divergent = false;
            foreach (var m in type.GetMembers())
            {
                if (m.IsImplicitlyDeclared) { continue; }
                if (m is INamedTypeSymbol) { continue; } // nested types enumerated separately
                if (m is IMethodSymbol acc && acc.AssociatedSymbol != null) { continue; } // accessors fold into property/event
                if (m.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event)) { continue; }
                if (m.DeclaringSyntaxReferences.Length == 0) { continue; }
                // A primary constructor's declaring syntax IS the type declaration: tracking it as a
                // node would let an orphaned constructor delete the whole (possibly live) type.
                // References to it fall back to the containing type instead.
                if (m is IMethodSymbol pctor && pctor.MethodKind == MethodKind.Constructor
                    && pctor.DeclaringSyntaxReferences.Any(dr => dr.GetSyntax() is TypeDeclarationSyntax))
                {
                    continue;
                }
                if (Graph.Id(m) is not { } mId) { continue; }
                _graph.AddNodeProject(mId, projectName);
                if (!_graph.Nodes.Add(mId)) { continue; }
                _graph.Decl[mId] = m;
                _graph.ContainerOf[mId] = typeId;
                if (isTest) { _graph.TestNodes.Add(mId); }
                // A member that only this compilation declares: a same-named type with a DIFFERENT
                // body exists in another project — analyze both so no reference goes missing.
                if (!firstSeen) { divergent = true; }
            }
            if (!firstSeen && divergent)
            {
                _types.Add((type, isTest));
                _graph.Warnings.Add($"{SymbolNames.Fqn(type)}: {projectName} declares a different type under the same full name as another project — both declarations are analyzed, verify removals in it manually");
            }
        }
    }

    IEnumerable<INamedTypeSymbol> AllSourceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    foreach (var t in AllSourceTypes(child)) { yield return t; }
                    break;
                case INamedTypeSymbol t:
                    foreach (var x in SelfAndNested(t)) { yield return x; }
                    break;
            }
        }

        IEnumerable<INamedTypeSymbol> SelfAndNested(INamedTypeSymbol t)
        {
            if (t.DeclaringSyntaxReferences.Length == 0) { yield break; }
            yield return t;
            foreach (var nested in t.GetTypeMembers())
            {
                foreach (var x in SelfAndNested(nested)) { yield return x; }
            }
        }
    }

    /// <summary>Node id when the symbol is a node of this graph, else null.</summary>
    string NodeId(ISymbol symbol)
    {
        if (symbol == null) { return null; }
        var id = Graph.Id(symbol);
        return id != null && _graph.Nodes.Contains(id) ? id : null;
    }

    // ---------------------------------------------------------------- Phase 2

    void AddStructuralRulesAndRoots(INamedTypeSymbol type, bool isTest)
    {
        var typeId = Graph.Id(type);
        var members = type.GetMembers();
        var one = new object[1] { typeId };
        // [StructLayout]/[DataContract]/[Serializable]: the FIELD set is the marshalled or
        // serialized shape, whatever each field's accessibility.
        bool layoutSensitive = type.IsSerializable
            || type.GetAttributes().Any(a => a.AttributeClass?.Name is { } an && Constants.LayoutSensitiveAttributeNames.Contains(an));

        foreach (var m in members)
        {
            if (NodeId(m) is not { } mId) { continue; }

            // Members that stay with their type: compiler/framework reaches them without identifiers.
            // The language patterns (foreach, await, deconstruction, using) require accessibility,
            // not "public", so accessibility is not checked for the keep-alive names.
            if (m is IMethodSymbol mm)
            {
                bool keep = mm.MethodKind is MethodKind.StaticConstructor or MethodKind.Destructor
                        or MethodKind.UserDefinedOperator or MethodKind.Conversion
                    || (mm.MethodKind == MethodKind.Constructor && mm.Parameters.Length == 0)
                    || Constants.KeepAliveMemberNames.Contains(mm.Name);
                if (keep) { _graph.AddRule(one, new object[] { mId }, "kept with type"); }
            }
            else if (m is IPropertySymbol np && Constants.KeepAliveMemberNames.Contains(np.Name))
            {
                // Pattern properties (Current, IsCompleted) stay with the type in EVERY mode: the
                // language reaches them without identifiers, whatever their accessibility.
                _graph.AddRule(one, new object[] { mId }, "kept with type");
            }
            else if (m is IFieldSymbol && layoutSensitive)
            {
                _graph.AddRule(one, new object[] { mId }, "layout-sensitive field kept with type");
            }
            else if (!_config.StrictMembers)
            {
                // Serializers / model binding / data binding reach these via reflection; public
                // FIELDS carry serializer shape exactly as public properties do.
                bool keep = (m is IPropertySymbol p && p.DeclaredAccessibility == Accessibility.Public)
                    || (m is IFieldSymbol && type.TypeKind == TypeKind.Enum)
                    || (m is IFieldSymbol pubF && pubF.DeclaredAccessibility == Accessibility.Public);
                if (keep) { _graph.AddRule(one, new object[] { mId }, "public surface kept with type"); }
            }

            // Override dispatch: the override is used whenever the overridden member is used on a
            // usable instance. External overridden member => keep with type.
            ISymbol overridden = m switch
            {
                IMethodSymbol om when om.IsOverride => om.OverriddenMethod,
                IPropertySymbol op when op.IsOverride => op.OverriddenProperty,
                IEventSymbol oe when oe.IsOverride => oe.OverriddenEvent,
                _ => null,
            };
            if (overridden != null)
            {
                if (NodeId(overridden) is { } oId)
                {
                    _graph.AddRule(new object[] { typeId, oId }, new object[] { mId }, "override dispatch");
                    // The reverse holds for compilation: an "override" cannot survive its base member.
                    _graph.AddEdge(mId, oId);
                }
                else
                {
                    _graph.AddRule(one, new object[] { mId }, "overrides external member");
                }
            }

            // An explicit interface implementation ("void IFoo.M()") cannot survive its interface member.
            IEnumerable<ISymbol> explicitImpls = m switch
            {
                IMethodSymbol xm => xm.ExplicitInterfaceImplementations,
                IPropertySymbol xp => xp.ExplicitInterfaceImplementations,
                IEventSymbol xe => xe.ExplicitInterfaceImplementations,
                _ => Array.Empty<ISymbol>(),
            };
            foreach (var ei in explicitImpls)
            {
                if (NodeId(ei) is { } eiId) { _graph.AddEdge(mId, eiId); }
            }
        }

        // Interface dispatch.
        foreach (var iface in type.AllInterfaces)
        {
            bool external = NodeId(iface) == null;
            foreach (var im in iface.GetMembers())
            {
                if (im.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Event)) { continue; }
                if (im is IMethodSymbol iacc && iacc.AssociatedSymbol != null) { continue; }
                var impl = type.FindImplementationForInterfaceMember(im);
                if (impl == null) { continue; }
                if (impl is IMethodSymbol iaccm && iaccm.AssociatedSymbol != null)
                {
                    impl = iaccm.AssociatedSymbol;
                }
                if (NodeId(impl) is not { } implId) { continue; }
                if (external)
                {
                    _graph.AddRule(one, new object[] { implId }, "implements external interface");
                }
                else
                {
                    _graph.AddRule(new object[] { typeId, Graph.Id(im) }, new object[] { implId }, "interface dispatch");
                }
            }
        }

        // Implicit base constructor calls (": base()" omitted, or no explicit constructors at all).
        if (type.BaseType is { } baseType && NodeId(baseType) != null)
        {
            // Callable without arguments: none, or all optional / params (RepoBase(string conn = "db")).
            var baseCtorId = baseType.OriginalDefinition.InstanceConstructors
                .Where(c => c.Parameters.All(p => p.IsOptional || p.IsParams))
                .OrderBy(c => c.Parameters.Length)
                .Select(c => NodeId(c))
                .FirstOrDefault(id => id != null);
            if (baseCtorId != null)
            {
                var explicitCtors = members.OfType<IMethodSymbol>()
                    .Where(c => c.MethodKind == MethodKind.Constructor && !c.IsStatic && NodeId(c) != null)
                    .ToList();
                if (explicitCtors.Count == 0)
                {
                    _graph.AddRule(one, new object[] { baseCtorId }, "implicit ctor chains to base");
                }
                else
                {
                    foreach (var ctor in explicitCtors)
                    {
                        bool hasInitializer = ctor.DeclaringSyntaxReferences
                            .Select(r => r.GetSyntax())
                            .OfType<ConstructorDeclarationSyntax>()
                            .Any(c => c.Initializer != null);
                        if (!hasInitializer) { _graph.AddEdge(NodeId(ctor), baseCtorId); }
                    }
                }
            }
        }

        // Binders: MediatR handlers, validators — bound type in use => implementor in use.
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var (bname, argIndex, isInterface) in Constants.Binders)
            {
                if (!isInterface || iface.Name != bname || iface.TypeArguments.Length <= argIndex) { continue; }
                BindTypeArgToImplementor(iface.TypeArguments[argIndex], type, iface, bname);
            }
        }
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            foreach (var (bname, argIndex, isInterface) in Constants.Binders)
            {
                if (isInterface || b.Name != bname || b.TypeArguments.Length <= argIndex) { continue; }
                BindTypeArgToImplementor(b.TypeArguments[argIndex], type, b, bname);
            }
        }

        DetectRoots(type, typeId, members, isTest);
    }

    void BindTypeArgToImplementor(ITypeSymbol arg, INamedTypeSymbol implementor, INamedTypeSymbol binder, string binderName)
    {
        if (NodeId(arg) is not { } argId) { return; }
        var consequents = new List<object> { Graph.Id(implementor) };
        consequents.AddRange(implementor.InstanceConstructors.Select(NodeId).Where(id => id != null));
        // The framework dispatches through the binder's members (e.g. Handle): while a handler
        // exists, those members count as used even if no source calls them directly.
        consequents.AddRange(binder.OriginalDefinition.GetMembers().Select(NodeId).Where(id => id != null));
        _graph.AddRule(new object[] { argId }, consequents, $"{binderName} binding");
    }

    void DetectRoots(INamedTypeSymbol type, string typeId, System.Collections.Immutable.ImmutableArray<ISymbol> members, bool isTest)
    {
        bool rootWholeType = false;
        bool rootAllMethods = false;
        bool rootEventNamedMethods = false;
        string reason = "";

        if (type.Name.EndsWith("Controller", StringComparison.Ordinal) || BaseChainHasName(type, "Controller", "ControllerBase", "ApiController"))
        {
            rootWholeType = true;
            reason = Constants.ReasonController;
        }
        else if (type.AllInterfaces.Any(i => i.Name == "IHostedService" && IsFrameworkType(i)) || BaseChainHasName(type, "BackgroundService"))
        {
            rootWholeType = true;
            reason = "hosted service";
        }
        else if (type.Name is "Startup" or "Program")
        {
            rootWholeType = true;
            reason = "startup/program";
        }
        else if (BaseChainHasName(type, "Profile"))
        {
            rootWholeType = true;
            reason = Constants.ReasonAutoMapperProfile;
        }
        else if (BaseChainHasExternalName(type, "Module", "Autofac"))
        {
            rootWholeType = true;
            reason = Constants.ReasonAutofacModule;
        }
        // Framework-discovered code: no static reference exists to any of these, so without a root
        // they are "pre-existing orphans" and die the moment they touch newly-dead code.
        else if (BaseChainHasName(type, "Migration", "DbMigration"))
        {
            rootWholeType = true;
            reason = Constants.ReasonEfMigration;
        }
        else if (BaseChainHasName(type, "ViewComponent", "TagHelper", "PageModel", "ComponentBase", "Hub"))
        {
            rootWholeType = true;
            reason = Constants.ReasonFrameworkComponent;
        }
        else if (BaseChainHasName(type, "EntityTypeConfiguration", "ComplexTypeConfiguration", "AreaRegistration", "NinjectModule"))
        {
            // EF6 configurations (AddFromAssembly), MVC areas (RegisterAllAreas), Ninject modules:
            // all discovered by assembly scanning.
            rootWholeType = true;
            reason = Constants.ReasonFrameworkSubclass;
        }
        else if (BaseChainHasExternalName(type, "Page", "System.Web") || BaseChainHasExternalName(type, "UserControl", "System.Web")
            || BaseChainHasExternalName(type, "MasterPage", "System.Web"))
        {
            // Page_Load (AutoEventWireup) and markup-wired handlers are protected and called by name.
            rootWholeType = true;
            rootAllMethods = true;
            reason = "WebForms code-behind";
        }
        else if (BaseChainHasExternalName(type, "HttpApplication", "System.Web"))
        {
            // Application_Start, Session_End, FormsAuthentication_OnAuthenticate, <AnyModule>_<Event>:
            // protected or private, and wired by name.
            rootWholeType = true;
            rootEventNamedMethods = true;
            reason = "Global.asax application";
        }
        else if (type.AllInterfaces.Any(i => i.Name is "IEntityTypeConfiguration" or "IMiddleware" or "IHttpModule" or "IHttpHandler" or "IJob" or "ITagHelper" && IsFrameworkType(i)))
        {
            rootWholeType = true;
            reason = Constants.ReasonFrameworkImplementation;
        }
        else if (type.InstanceConstructors.Any(c => c.Parameters.Any(p => p.Type.Name == "RequestDelegate")))
        {
            rootWholeType = true;
            reason = "convention middleware";
        }
        else if (HasAttributeNamed(type, "ServiceContractAttribute") || type.AllInterfaces.Any(i => HasAttributeNamed(i, "ServiceContractAttribute")))
        {
            rootWholeType = true;
            reason = "WCF service contract";
        }
        else if (BaseChainHasExternalName(type, "WebService", "System.Web"))
        {
            rootWholeType = true;
            reason = "ASMX web service";
        }
        else if (HasAttributeNamed(type, "ExportAttribute") || HasAttributeNamed(type, "InheritedExportAttribute"))
        {
            rootWholeType = true;
            reason = Constants.ReasonMefExport;
        }
        else if (BaseChainHasExternalName(type, "MembershipProvider", "System.Web") || BaseChainHasExternalName(type, "RoleProvider", "System.Web")
            || BaseChainHasExternalName(type, "ProfileProvider", "System.Web"))
        {
            rootWholeType = true;
            reason = "config-wired provider";
        }
        else if (BaseChainHasExternalName(type, "ConfigurationSection", "System.Configuration") || BaseChainHasExternalName(type, "ConfigurationElement", "System.Configuration"))
        {
            rootWholeType = true;
            reason = "configuration section";
        }

        if (rootWholeType)
        {
            _graph.AddRoot(typeId, reason, isTest);
            foreach (var m in members)
            {
                if (NodeId(m) is not { } mId) { continue; }
                bool isCtor = m is IMethodSymbol c && c.MethodKind == MethodKind.Constructor;
                bool byConvention = m is IMethodSymbol { MethodKind: MethodKind.Ordinary }
                    && (rootAllMethods || (rootEventNamedMethods && m.Name.IndexOf(Constants.HttpApplicationEventSeparator) > 0));
                if (isCtor || byConvention || m.DeclaredAccessibility == Accessibility.Public)
                {
                    _graph.AddRoot(mId, reason, isTest);
                }
            }
        }

        // A test fixture with no attributed member of its own: an NUnit/MSTest fixture class, or a
        // concrete class INHERITING its tests from a base fixture (the runner executes them on it).
        bool fixtureAttribute = type.GetAttributes().Any(a => AttributeIn(a, Constants.TestClassAttributeNames));
        bool inheritsTests = type.TypeKind == TypeKind.Class && !type.IsAbstract && BaseChainHasTestMethod(type);
        if (fixtureAttribute || inheritsTests)
        {
            _graph.AddRoot(typeId, "test class", true);
            foreach (var ctor in type.InstanceConstructors)
            {
                if (NodeId(ctor) is { } cId) { _graph.AddRoot(cId, "test/attr class ctor", true); }
            }
        }

        // Attribute-based roots (member level).
        foreach (var m in members)
        {
            if (NodeId(m) is not { } mId) { continue; }
            foreach (var attr in m.GetAttributes())
            {
                var an = attr.AttributeClass?.Name;
                if (an == null) { continue; }
                var shortName = ShortAttributeName(attr);
                bool isTestAttr = AttributeIn(attr, Constants.TestAttributeNames);
                bool isProdAttr = Constants.ProdRootAttributeNames.Contains(shortName);
                if (!isTestAttr && !isProdAttr) { continue; }
                // Test-attributed members root the test world; others root production.
                bool asTest = isTestAttr || isTest;
                _graph.AddRoot(mId, isTestAttr ? "test method" : $"[{shortName}]", asTest);
                _graph.AddRoot(typeId, isTestAttr ? "test class" : $"[{shortName}] class", asTest);
                foreach (var ctor in type.InstanceConstructors)
                {
                    if (NodeId(ctor) is { } cId) { _graph.AddRoot(cId, "test/attr class ctor", asTest); }
                }
                break;
            }
        }
    }

    bool HasAttributeNamed(INamedTypeSymbol type, string name)
    {
        return type.GetAttributes().Any(a => a.AttributeClass?.Name == name);
    }

    /// <summary>True when a base class carries one of the framework names AND comes from the framework:
    /// a solution's own "Profile" or "Hub" class is ordinary code, not a framework-discovered root.</summary>
    bool BaseChainHasName(INamedTypeSymbol type, params string[] names)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (names.Contains(b.Name) && IsFrameworkType(b)) { return true; }
        }
        return false;
    }

    /// <summary>True for a type declared outside the solution (or unresolvable), or for any type when
    /// config "inSolutionRegistrations" opts the solution's own framework stand-ins in.</summary>
    bool IsFrameworkType(INamedTypeSymbol t)
    {
        if (_config.InSolutionRegistrations) { return true; }
        var asm = t.ContainingAssembly?.Name;
        return asm == null || !_solutionAssemblies.Contains(asm);
    }

    bool BaseChainHasTestMethod(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            foreach (var m in b.GetMembers())
            {
                if (m is IMethodSymbol && m.GetAttributes().Any(a => AttributeIn(a, Constants.TestMethodAttributeNames))) { return true; }
            }
        }
        return false;
    }

    bool BaseChainHasExternalName(INamedTypeSymbol type, string name, string assemblyPrefix)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (b.Name == name && b.ContainingAssembly?.Name.StartsWith(assemblyPrefix, StringComparison.Ordinal) == true) { return true; }
        }
        return false;
    }

    void ApplyConfigRoots()
    {
        var patterns = _config.Keep.Concat(_config.Roots).Distinct().ToList();
        if (patterns.Count == 0) { return; }
        var regexes = patterns
            .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*") + "$", RegexOptions.Compiled))
            .ToList();
        var matchedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, symbol) in _graph.Decl)
        {
            var fqn = SymbolNames.Fqn(symbol);
            if (regexes.Any(r => r.IsMatch(fqn)))
            {
                _graph.AddRoot(id, "config keep/root", _graph.TestNodes.Contains(id));
                _graph.ConfigKeptIds.Add(id);
                if (symbol is INamedTypeSymbol) { matchedTypes.Add(id); }
            }
        }
        // Keeping a type keeps it WHOLE: code reached only by reflection calls members nothing names.
        if (matchedTypes.Count == 0) { return; }
        foreach (var id in _graph.Decl.Keys)
        {
            for (var cur = id; _graph.ContainerOf.TryGetValue(cur, out var c); cur = c)
            {
                if (!matchedTypes.Contains(c)) { continue; }
                _graph.AddRoot(id, "config keep/root (member of a kept type)", _graph.TestNodes.Contains(id));
                _graph.ConfigKeptIds.Add(id);
                break;
            }
        }
    }

    // ---------------------------------------------------------------- Phase 3

    void WalkTree(Compilation comp, SyntaxTree tree, bool isTest, string projectName)
    {
        var model = comp.GetSemanticModel(tree);
        var root = tree.GetRoot();

        // Files with non-symbol top-level content must never be deleted whole.
        if (root is CompilationUnitSyntax cu
            && (cu.AttributeLists.Count > 0 || cu.Externs.Count > 0 || cu.Usings.Any(u => u.GlobalKeyword.RawKind != 0)))
        {
            _graph.FilesWithTopLevelContent.Add(tree.FilePath);
        }

        // Files with inactive #if regions hold code the compiler cannot see: never delete them
        // whole, and remember the hidden text so deletions mentioned in it can warn.
        if (root.ContainsDirectives)
        {
            var disabled = root.DescendantTrivia(descendIntoTrivia: true)
                .Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia))
                .Select(t => t.ToString())
                .ToList();
            if (disabled.Count > 0)
            {
                _graph.FilesWithInactiveCode.Add(tree.FilePath);
                _graph.DisabledTexts[tree.FilePath] = string.Join("\n", disabled);
            }
        }

        // Assembly/module-level attributes reference code and packages with no containing member:
        // root everything they resolve so none of it is ever orphaned.
        if (root is CompilationUnitSyntax cu2)
        {
            foreach (var al in cu2.AttributeLists)
            {
                foreach (var at in al.Attributes)
                {
                    RootNamedStartMethod(at, model, projectName, isTest);
                }
                foreach (var node in al.DescendantNodesAndSelf())
                {
                    ISymbol sym = node switch
                    {
                        AttributeSyntax at => model.GetSymbolInfo(at).Symbol,
                        IdentifierNameSyntax or GenericNameSyntax => model.GetSymbolInfo(node).Symbol,
                        _ => null,
                    };
                    if (sym != null) { RootCompilationUnitAttributeSymbol(sym, projectName, isTest); }
                }
            }
        }

        var globals = root.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count > 0)
        {
            var ep = comp.GetEntryPoint(CancellationToken.None);
            if (ep != null && Graph.Id(ep) is { } epId)
            {
                foreach (var g in globals) { WalkMember(g, epId, model); }
            }
        }

        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSym) { continue; }
            if (NodeId(typeSym) is not { } typeId) { continue; }

            foreach (var al in typeDecl.AttributeLists) { WalkMember(al, typeId, model); }
            if (typeDecl.BaseList != null) { WalkMember(typeDecl.BaseList, typeId, model); }
            if (typeDecl is TypeDeclarationSyntax tds)
            {
                foreach (var cc in tds.ConstraintClauses) { WalkMember(cc, typeId, model); }
                if (tds.ParameterList != null) { WalkMember(tds.ParameterList, typeId, model); } // primary ctor
            }

            if (typeDecl is EnumDeclarationSyntax en)
            {
                foreach (var em in en.Members)
                {
                    var fId = NodeId(model.GetDeclaredSymbol(em));
                    foreach (var al in em.AttributeLists) { WalkMember(al, fId ?? typeId, model); }
                    if (em.EqualsValue != null && fId != null) { WalkMember(em.EqualsValue, fId, model); }
                }
                continue;
            }

            foreach (var member in ((TypeDeclarationSyntax)typeDecl).Members)
            {
                switch (member)
                {
                    case BaseTypeDeclarationSyntax:
                    case DelegateDeclarationSyntax:
                        continue; // walked on their own
                    case FieldDeclarationSyntax f:
                        WalkFieldLike(f.Declaration, f.AttributeLists, model);
                        continue;
                    case EventFieldDeclarationSyntax ef:
                        WalkFieldLike(ef.Declaration, ef.AttributeLists, model);
                        continue;
                }
                var sym = model.GetDeclaredSymbol(member);
                if (sym is IMethodSymbol acc && acc.AssociatedSymbol != null)
                {
                    sym = acc.AssociatedSymbol;
                }
                if (NodeId(sym) is not { } memberId) { continue; }
                WalkMember(member, memberId, model);
                if (member is PropertyDeclarationSyntax { Initializer: { } pInit })
                {
                    KeepIfInitializerActs(sym, memberId, pInit.Value, model);
                }
            }
        }

        foreach (var del in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            if (NodeId(model.GetDeclaredSymbol(del)) is { } delId) { WalkMember(del, delId, model); }
        }
    }

    void WalkFieldLike(VariableDeclarationSyntax decl, SyntaxList<AttributeListSyntax> attrs, SemanticModel model)
    {
        // The declaration's type and attributes are shared by EVERY sibling: each needs the edges,
        // or a surviving sibling could not keep the field type alive when another sibling dies.
        var ids = new List<string>();
        foreach (var v in decl.Variables)
        {
            var fSym = model.GetDeclaredSymbol(v);
            if (NodeId(fSym) is not { } fId) { continue; }
            ids.Add(fId);
            if (v.Initializer != null)
            {
                WalkMember(v.Initializer, fId, model);
                KeepIfInitializerActs(fSym, fId, v.Initializer.Value, model);
            }
            if (v.ArgumentList != null) { WalkMember(v.ArgumentList, fId, model); }
        }
        foreach (var fId in ids)
        {
            WalkMember(decl.Type, fId, model);
            foreach (var al in attrs) { WalkMember(al, fId, model); }
        }
        // Siblings share one line of text: a dead one is kept in place while another lives, so its
        // initializer still compiles there — everything it references must stay too.
        if (ids.Count > 1)
        {
            foreach (var fId in ids)
            {
                _graph.AddRule(new object[] { fId }, ids.Where(o => o != fId).Cast<object>().ToList(), "declared in one field declaration");
            }
        }
    }

    /// <summary>An initializer runs with every construction (static: on first use of the type), so one
    /// that DOES something — starts a timer, calls a method — keeps its field/property while the type
    /// is in use, exactly as the same code inside a constructor would. Pure BCL calls and creations
    /// (collections, strings, math) do nothing observable; lambda bodies do not run at initialization.</summary>
    void KeepIfInitializerActs(ISymbol member, string memberId, ExpressionSyntax value, SemanticModel model)
    {
        if (member?.ContainingType == null || NodeId(member.ContainingType) is not { } typeId) { return; }
        if (model.GetOperation(value) is not { } op) { return; }
        if (OperationActs(op, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0))
        {
            _graph.AddRule(new object[] { typeId }, new object[] { memberId }, "initializer with side effects kept with type");
        }
    }

    /// <summary>True when evaluating the operation does something observable: a call that is not pure
    /// BCL, or a construction whose constructor does (followed into solution constructors, their
    /// base calls and their types' instance initializers). Lambda bodies do not run here.</summary>
    bool OperationActs(IOperation op, HashSet<ISymbol> seen, int depth)
    {
        var pending = new Stack<IOperation>();
        pending.Push(op);
        while (pending.Count > 0)
        {
            var cur = pending.Pop();
            if (cur is IAnonymousFunctionOperation or ILocalFunctionOperation) { continue; }
            bool acts = cur switch
            {
                // A constructor invoked as a call is a ": base(...)" / ": this(...)" initializer.
                IInvocationOperation { TargetMethod.MethodKind: MethodKind.Constructor } ci => ConstructionActs(ci.TargetMethod, seen, depth),
                IInvocationOperation inv => !IsPureBclTarget(inv.TargetMethod.OriginalDefinition),
                IObjectCreationOperation oc => oc.Constructor != null && ConstructionActs(oc.Constructor, seen, depth),
                _ => false,
            };
            if (acts) { return true; }
            foreach (var child in cur.ChildOperations) { pending.Push(child); }
        }
        return false;
    }

    bool ConstructionActs(IMethodSymbol ctor, HashSet<ISymbol> seen, int depth)
    {
        var def = ctor.OriginalDefinition;
        if (NodeId(def.ContainingType) == null) { return !IsPureBclTarget(def); }
        if (!seen.Add(def)) { return false; }
        // Too deep to follow: assume it acts (over-keeping is the safe side).
        if (depth >= Constants.MaxConstructionFollowDepth) { return true; }
        var bodies = new List<SyntaxNode>();
        bool hasInitializer = false;
        foreach (var r in def.DeclaringSyntaxReferences)
        {
            var syn = r.GetSyntax();
            // A primary constructor's syntax is the type declaration: not followable here.
            if (syn is not ConstructorDeclarationSyntax cds) { return true; }
            bodies.Add(cds);
            if (cds.Initializer != null) { hasInitializer = true; }
        }
        foreach (var m in def.ContainingType.GetMembers())
        {
            if (m.IsStatic || m is not (IFieldSymbol or IPropertySymbol)) { continue; }
            foreach (var r in m.DeclaringSyntaxReferences)
            {
                var init = r.GetSyntax() switch
                {
                    VariableDeclaratorSyntax v => v.Initializer,
                    PropertyDeclarationSyntax pd => pd.Initializer,
                    _ => null,
                };
                if (init != null) { bodies.Add(init); }
            }
        }
        foreach (var body in bodies)
        {
            if (!_treeCompilations.TryGetValue(body.SyntaxTree, out var comp)) { return true; }
            if (comp.GetSemanticModel(body.SyntaxTree).GetOperation(body) is { } bop && OperationActs(bop, seen, depth + 1)) { return true; }
        }
        // No ": base(...)" written: the base constructor callable without arguments runs.
        if (!hasInitializer && def.ContainingType.BaseType is { } bt)
        {
            var baseCtor = bt.InstanceConstructors.Where(c => c.Parameters.All(p => p.IsOptional || p.IsParams)).OrderBy(c => c.Parameters.Length).FirstOrDefault();
            if (baseCtor != null && ConstructionActs(baseCtor, seen, depth + 1)) { return true; }
        }
        return false;
    }

    /// <summary>[assembly: PreApplicationStartMethod(typeof(X), "Start")] and the WebActivatorEx variants
    /// name the method by STRING: every method of X with that name is rooted.</summary>
    void RootNamedStartMethod(AttributeSyntax at, SemanticModel model, string projectName, bool isTest)
    {
        if (model.GetSymbolInfo(at).Symbol is not IMethodSymbol attrCtor) { return; }
        var attrName = ShortAttributeName(attrCtor.ContainingType.Name);
        if (!Constants.StartMethodAttributeNames.Contains(attrName) || at.ArgumentList == null) { return; }
        var args = at.ArgumentList.Arguments.Select(a => a.Expression).ToList();
        var typeOf = args.OfType<TypeOfExpressionSyntax>().FirstOrDefault();
        // Every string argument may be the method name (OwinStartup's first one is a friendly name):
        // rooting a same-named method too only over-keeps.
        var names = args.Select(a => model.GetConstantValue(a)).Where(c => c.HasValue && c.Value is string).Select(c => (string)c.Value).ToList();
        if (attrName == Constants.OwinStartupAttributeName) { names.Add(Constants.OwinDefaultStartMethod); }
        if (typeOf == null || names.Count == 0) { return; }
        if (model.GetTypeInfo(typeOf.Type).Type is not INamedTypeSymbol target) { return; }
        foreach (var name in names.Distinct())
        {
            foreach (var m in target.GetMembers(name))
            {
                if (NodeId(m) is not { } mId) { continue; }
                _graph.AddRoot(mId, $"[assembly: {attrName}] method", isTest);
                _graph.AddAssemblyAttrRef(projectName, mId);
            }
        }
    }

    void RootCompilationUnitAttributeSymbol(ISymbol sym, string projectName, bool isTest)
    {
        if (sym is IAliasSymbol alias) { sym = alias.Target; }
        sym = sym.OriginalDefinition;
        if (sym is IMethodSymbol acc && acc.AssociatedSymbol != null) { sym = acc.AssociatedSymbol.OriginalDefinition; }
        if (NodeId(sym) is { } id)
        {
            _graph.AddRoot(id, "assembly attribute", isTest);
            _graph.AddAssemblyAttrRef(projectName, id);
            return;
        }
        if (NodeId(sym.ContainingType) is { } tid)
        {
            _graph.AddRoot(tid, "assembly attribute", isTest);
            _graph.AddAssemblyAttrRef(projectName, tid);
        }
        var asm = sym.ContainingAssembly?.Name;
        if (asm != null && !_solutionAssemblies.Contains(asm)) { _graph.AddProjectExternalRef(projectName, asm); }
    }

    void WalkMember(SyntaxNode subtree, string sourceId, SemanticModel model)
    {
        var skipSpans = MatchStatements(subtree, sourceId, model);

        WalkOperations(subtree, sourceId, model, skipSpans);

        foreach (var node in subtree.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case IdentifierNameSyntax or GenericNameSyntax:
                    if (InSkip(skipSpans, node.Span))
                    {
                        // Suppressed type arguments still pin external packages while the member lives.
                        RecordExternal(sourceId, model.GetSymbolInfo(node).Symbol);
                        break;
                    }
                    AddSymbolInfoEdges(sourceId, model.GetSymbolInfo(node));
                    break;
                case BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax or ElementAccessExpressionSyntax:
                    if (InSkip(skipSpans, node.Span))
                    {
                        // Inside a claimed chain these belong to the statement (its InvolvedNodes and
                        // chain references), never to the containing member: only pin packages here.
                        RecordExternal(sourceId, model.GetSymbolInfo(node).Symbol);
                        break;
                    }
                    AddSymbolInfoEdges(sourceId, model.GetSymbolInfo(node));
                    break;
                case PrimaryConstructorBaseTypeSyntax pbt:
                    // ": Base(x)" of a primary constructor — the only reference to the base
                    // constructor, with no identifier naming it.
                    AddSymbolInfoEdges(sourceId, model.GetSymbolInfo(pbt));
                    break;
                case AttributeSyntax at when at.ArgumentList != null:
                    AddAttributeNamedMemberEdges(sourceId, at, model);
                    break;
                case CommonForEachStatementSyntax fe:
                {
                    // CommonForEachStatementSyntax covers BOTH forms: "foreach (var x in y)" and the
                    // deconstructing "foreach (var (a, b) in y)" — the latter is a different node
                    // type, and matching only the plain form loses extension GetEnumerator edges.
                    var info = model.GetForEachStatementInfo(fe);
                    AddEdgeTo(sourceId, info.GetEnumeratorMethod);
                    AddEdgeTo(sourceId, info.MoveNextMethod);
                    AddEdgeTo(sourceId, info.CurrentProperty);
                    AddEdgeTo(sourceId, info.DisposeMethod);
                    break;
                }
                case AwaitExpressionSyntax aw:
                {
                    var info = model.GetAwaitExpressionInfo(aw);
                    AddEdgeTo(sourceId, info.GetAwaiterMethod);
                    AddEdgeTo(sourceId, info.IsCompletedProperty);
                    AddEdgeTo(sourceId, info.GetResultMethod);
                    break;
                }
            }
        }
    }

    /// <summary>An attribute pairing typeof(T) with a string naming one of T's members hands that member
    /// to a framework, which calls it by reflection: [BindServiceMethod(typeof(Greeter), "BindService")]
    /// is gRPC's only route to the generated binder, and through it to every service override.</summary>
    void AddAttributeNamedMemberEdges(string sourceId, AttributeSyntax at, SemanticModel model)
    {
        var types = at.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>()
            .Select(t => model.GetTypeInfo(t.Type).Type).OfType<INamedTypeSymbol>().ToList();
        if (types.Count == 0) { return; }
        foreach (var arg in at.ArgumentList.Arguments)
        {
            if (model.GetConstantValue(arg.Expression) is not { HasValue: true, Value: string name }) { continue; }
            foreach (var t in types)
            {
                foreach (var m in t.GetMembers(name)) { AddEdgeTo(sourceId, m); }
            }
        }
    }

    bool InSkip(List<TextSpan> spans, TextSpan span)
    {
        if (spans == null) { return false; }
        foreach (var s in spans)
        {
            if (s.Contains(span)) { return true; }
        }
        return false;
    }

    /// <summary>Harvests references from the compiler's bound tree (IOperation). Unlike the syntax
    /// walk, the bound tree contains EVERY symbol the compiler resolved — including the ones with no
    /// identifier in source: query-syntax operators, collection-initializer Add, conditional element
    /// access, user-defined operators and conversions, inferred ("var") types.</summary>
    void WalkOperations(SyntaxNode subtree, string sourceId, SemanticModel model, List<TextSpan> skipSpans)
    {
        var rootOp = model.GetOperation(subtree)
            ?? (subtree is GlobalStatementSyntax gs ? model.GetOperation(gs.Statement) : null);
        if (rootOp != null)
        {
            HarvestOperation(rootOp, sourceId, skipSpans);
            return;
        }
        // Not itself an operation root (property/type declarations): find the roots inside it.
        // PrimaryConstructorBaseTypeSyntax carries the ": Base(x)" call of a primary constructor —
        // the only reference to the base constructor, invisible to the identifier walk.
        foreach (var node in subtree.DescendantNodesAndSelf())
        {
            bool candidate = node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax
                or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax or AttributeSyntax
                or PrimaryConstructorBaseTypeSyntax;
            if (!candidate || node == subtree) { continue; }
            var op = model.GetOperation(node);
            if (op == null || op.Parent != null) { continue; }
            HarvestOperation(op, sourceId, skipSpans);
        }
    }

    void HarvestOperation(IOperation op, string sourceId, List<TextSpan> skipSpans)
    {
        // Tracked registration calls and suppressed spans: their references live and die with the
        // STATEMENT (claimed chains, type arguments), so they only pin external packages here.
        bool suppressed = InSkip(skipSpans, op.Syntax.Span)
            || (op is IInvocationOperation pi && pi.Syntax is InvocationExpressionSyntax pis && _processedInvocations.Contains(pis));
        void Emit(ISymbol s)
        {
            if (s == null) { return; }
            if (suppressed) { RecordExternal(sourceId, s); }
            else { AddEdgeTo(sourceId, s); }
        }
        switch (op)
        {
            case IInvocationOperation iv:
                Emit(iv.TargetMethod);
                // An invocation of a CONSTRUCTOR is a ": base(...)"/": this(...)" initializer —
                // explicit, or the implicit base call Roslyn synthesizes for every constructor
                // body. That is construction of the object itself, not a droppable side effect,
                // and it must not block the empty-container pass.
                if (!suppressed && iv.TargetMethod?.MethodKind != MethodKind.Constructor) { RecordExternalCall(sourceId, iv.TargetMethod); }
                break;
            case IObjectCreationOperation oc:
                Emit(oc.Constructor);
                if (!suppressed) { RecordExternalCall(sourceId, oc.Constructor); }
                if (oc.Type is INamedTypeSymbol created && NodeId(created.OriginalDefinition) is { } createdId)
                {
                    if (!_graph.CreatedBy.TryGetValue(createdId, out var creators)) { _graph.CreatedBy[createdId] = creators = new HashSet<string>(StringComparer.Ordinal); }
                    creators.Add(sourceId);
                }
                break;
            case IMemberReferenceOperation mr: Emit(mr.Member); break;
            case IUnaryOperation u: Emit(u.OperatorMethod); break;
            case IBinaryOperation b: Emit(b.OperatorMethod); break;
            case ICompoundAssignmentOperation ca: Emit(ca.OperatorMethod); break;
            case IIncrementOrDecrementOperation incr: Emit(incr.OperatorMethod); break;
            case IConversionOperation cv: Emit(cv.OperatorMethod); break;
            case ITypeOfOperation to: Emit(to.TypeOperand); break;
            case IIsTypeOperation it: Emit(it.TypeOperand); break;
            case IDynamicMemberReferenceOperation dmr when dmr.MemberName.Length > 0:
                _graph.DynamicMemberNames.Add(dmr.MemberName);
                break;
        }
        // Writes to external members (ServicePointManager.SecurityProtocol = ..., an event
        // subscription) are side effects exactly like calls, but leave no invocation behind.
        if (!suppressed)
        {
            var writeTarget = op switch
            {
                IAssignmentOperation asg => asg.Target,
                IIncrementOrDecrementOperation inc => inc.Target,
                IEventAssignmentOperation ea => ea.EventReference,
                _ => null,
            };
            if (writeTarget is IMemberReferenceOperation wmr) { RecordExternalWrite(sourceId, wmr); }
        }
        if (op is IConversionOperation icv && icv.IsImplicit)
        {
            // An IMPLICIT conversion has no syntax of its own: its target type (the callee's
            // parameter type binding an argument, a base type on assignment) is the CALLEE's choice,
            // never a name the caller's text mentions. A liveness edge here would pin every removed
            // method's parameter types to their callers and stall the cascade. The caller's PROJECT
            // still needs the type's assembly to compile the call, so it stays a cleanup need.
            if (op.Type is INamedTypeSymbol wnamed)
            {
                if (suppressed || NodeId(wnamed) == null) { RecordExternal(sourceId, wnamed); }
                else { _graph.AddWeakRef(sourceId, NodeId(wnamed)); }
            }
        }
        else if (op.Type is INamedTypeSymbol named) { Emit(named); }
        foreach (var child in op.ChildOperations) { HarvestOperation(child, sourceId, skipSpans); }
    }

    void AddSymbolInfoEdges(string sourceId, SymbolInfo info)
    {
        if (info.Symbol != null) { AddEdgeTo(sourceId, info.Symbol); }
        foreach (var c in info.CandidateSymbols) { AddEdgeTo(sourceId, c); }
    }

    void AddEdgeTo(string sourceId, ISymbol target)
    {
        if (Normalize(target) is { } targetId) { _graph.AddEdge(sourceId, targetId); }
        else { RecordExternal(sourceId, target); }
    }

    /// <summary>Records a CALL into an external assembly (invocation or construction) that no claimed
    /// statement owns: a side effect the empty-container pass must respect, invisible as an edge.</summary>
    void RecordExternalCall(string sourceId, ISymbol target)
    {
        if (target == null) { return; }
        var def = target.OriginalDefinition;
        var asm = def.ContainingAssembly?.Name;
        if (asm == null || _solutionAssemblies.Contains(asm)) { return; }
        if (Constants.EmptyContainerBenignAssemblies.Contains(asm) && !IsPureBclTarget(def))
        {
            // mscorlib / System.Runtime also hold File, Environment and Process: a call into them
            // is only benign when its TYPE is on the pure-surface lists. Everything else is recorded
            // under a name outside the benign set, so it blocks the empty-container pass.
            _graph.AddExternalCallRef(sourceId, asm + "!" + (def.ContainingType?.Name ?? def.Name));
            return;
        }
        _graph.AddExternalCallRef(sourceId, asm);
    }

    /// <summary>Records a write to an external member as a blocking external call. Writes through the
    /// instance itself ("this.X = ...", object initializers) only change state that dies with it.</summary>
    void RecordExternalWrite(string sourceId, IMemberReferenceOperation target)
    {
        if (target.Instance is IInstanceReferenceOperation) { return; }
        var asm = target.Member?.OriginalDefinition.ContainingAssembly?.Name;
        if (asm == null || _solutionAssemblies.Contains(asm)) { return; }
        _graph.AddExternalCallRef(sourceId, asm + Constants.ExternalWriteSuffix);
    }

    bool IsPureBclTarget(ISymbol def)
    {
        var top = def.ContainingType;
        if (top == null) { return false; }
        while (top.ContainingType != null) { top = top.ContainingType; }
        var ns = top.ContainingNamespace?.IsGlobalNamespace == false ? top.ContainingNamespace.ToDisplayString() : "";
        if (Constants.EmptyContainerPureNamespaces.Contains(ns)) { return true; }
        return Constants.EmptyContainerPureTypes.Contains(ns + "." + top.Name);
    }

    void RecordExternal(string sourceId, ISymbol target)
    {
        if (target == null) { return; }
        if (target is IAliasSymbol alias) { target = alias.Target; }
        target = target.OriginalDefinition;
        if (target.Kind is not (SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property
            or SymbolKind.Field or SymbolKind.Event))
        {
            return;
        }
        var asm = target.ContainingAssembly?.Name;
        if (asm == null || _solutionAssemblies.Contains(asm)) { return; }
        _graph.AddExternalRef(sourceId, asm);
    }

    string Normalize(ISymbol target)
    {
        if (target == null) { return null; }
        if (target is IAliasSymbol alias) { target = alias.Target; }
        if (target is IMethodSymbol red && red.ReducedFrom != null) { target = red.ReducedFrom; }
        target = target.OriginalDefinition;
        if (target is IMethodSymbol acc && acc.AssociatedSymbol != null)
        {
            target = acc.AssociatedSymbol.OriginalDefinition;
        }
        if (target.Kind is not (SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property
            or SymbolKind.Field or SymbolKind.Event))
        {
            return null;
        }
        if (NodeId(target) is { } id) { return id; }
        return NodeId(target.ContainingType);
    }

    // --------------------------------------------- hard-to-trace mentions

    /// <summary>The exclusion list: code named where the analysis cannot follow the use — runtime-compiled
    /// views and markup, inactive #if code, 'dynamic' member access, type names in config files — is
    /// treated as in use (a root), never deleted. Matching is by name, so it over-keeps; that is the
    /// safe side. Explicit removal targets are still removed (the sweep ignores roots that are targets).</summary>
    void ApplyMentionRoots()
    {
        var viewWords = WordIndex(_graph.ViewTexts, Constants.IdentifierWordPattern);
        var disabledWords = WordIndex(_graph.DisabledTexts, Constants.IdentifierWordPattern);
        var configTexts = new Dictionary<string, string>(Constants.PathComparer);
        foreach (var texts in _graph.ConfigTextsByProject.Values)
        {
            foreach (var (file, text) in texts)
            {
                if (text.Length > 0) { configTexts.TryAdd(file, text); }
            }
        }
        var configNames = WordIndex(configTexts, Constants.DottedNameWordPattern);
        foreach (var (id, sym) in _graph.Decl.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (sym is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) { continue; }
            string where = null;
            if (sym.Name.Length >= Constants.MinMentionNameLength)
            {
                if (viewWords.TryGetValue(sym.Name, out var vf)) { where = $"named in {vf}"; }
                else if (disabledWords.TryGetValue(sym.Name, out var df)) { where = $"named in inactive #if code in {df}"; }
            }
            if (where == null && sym is not INamedTypeSymbol && _graph.DynamicMemberNames.Contains(sym.Name))
            {
                where = "accessed by that name through 'dynamic'";
            }
            if (where == null && sym is INamedTypeSymbol nt)
            {
                var fqn = SymbolNames.Fqn(nt);
                var metadataFqn = nt.ContainingType == null ? fqn : SymbolNames.MetadataFqn(nt);
                if (configNames.TryGetValue(fqn, out var cf) || configNames.TryGetValue(metadataFqn, out cf)) { where = $"named in {cf}"; }
            }
            if (where == null) { continue; }
            // Already an entry point for a real reason: the mention adds nothing.
            if (_graph.Roots.Contains(id) || _graph.TestRoots.Contains(id)) { continue; }
            _graph.MentionRoots[id] = where;
            _graph.AddRoot(id, "mentioned: " + where, _graph.TestNodes.Contains(id));
        }
    }

    /// <summary>Every word (by the pattern) of the texts -> the first file (in path order) holding it.</summary>
    Dictionary<string, string> WordIndex(Dictionary<string, string> texts, string pattern)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var regex = new Regex(pattern, RegexOptions.Compiled);
        foreach (var (file, text) in texts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (Match m in regex.Matches(text)) { index.TryAdd(m.Value, file); }
        }
        return index;
    }

    // --------------------------------------------- assembly-scanning registrations

    /// <summary>Reads one assembly-scanning registration: which assemblies it scans, which classes
    /// its filter admits, and how they register. Autofac's RegisterAssemblyTypes is read along its
    /// fluent chain (filters included); other libraries (Scrutor, StructureMap, Windsor, Ninject,
    /// Unity) are read over their whole statement, filters not interpreted (every class matches).</summary>
    void RecordScan(InvocationExpressionSyntax inv, string name, string sourceId, SemanticModel model)
    {
        // Like registrations: a solution method that merely shares the name is not a scan.
        if (!_config.InSolutionRegistrations)
        {
            var resolvedAsm = model.GetSymbolInfo(inv).Symbol?.ContainingAssembly?.Name;
            if (resolvedAsm != null && _solutionAssemblies.Contains(resolvedAsm)) { return; }
        }
        var currentAsm = model.Compilation.Assembly.Name;
        var loc = inv.GetLocation().GetLineSpan();
        var site = $"{inv.SyntaxTree.FilePath}:{loc.StartLinePosition.Line + 1}";
        HashSet<string> assemblies = new(StringComparer.OrdinalIgnoreCase);
        bool assembliesKnown = true;
        var callNames = new HashSet<string>(StringComparer.Ordinal);
        Func<INamedTypeSymbol, bool> filter = null;
        bool filterExact = true;
        bool isAutofac = name == Constants.AutofacScanMethodName;
        SyntaxNode region;

        var serviceTypes = new List<INamedTypeSymbol>();
        // Set when a registration call cannot be followed: every matched class then lives while the
        // scanning member does.
        bool keepAll = false;

        if (isAutofac)
        {
            region = inv;
            foreach (var arg in inv.ArgumentList.Arguments)
            {
                if (ScanAssemblyOf(arg.Expression, model, currentAsm) is { } a) { assemblies.Add(a); }
                else { assembliesKnown = false; }
            }
            if (inv.ArgumentList.Arguments.Count == 0) { assembliesKnown = false; }
            // The fluent chain upward: every Where narrows the matched set (AND); AssignableTo<T> too.
            var cur = inv;
            while (cur.Parent is MemberAccessExpressionSyntax ma && ma.Parent is InvocationExpressionSyntax pInv)
            {
                var mname = ma.Name.Identifier.Text;
                callNames.Add(mname);
                if (mname == "Where" && pInv.ArgumentList.Arguments.Count == 1 && pInv.ArgumentList.Arguments[0].Expression is LambdaExpressionSyntax lambda)
                {
                    var (f, exact) = ParseScanFilter(lambda, model);
                    if (!exact) { filterExact = false; }
                    if (f != null) { filter = AndFilter(filter, f); }
                }
                else if (mname is "AssignableTo" && ma.Name is GenericNameSyntax ag && ag.TypeArgumentList.Arguments.Count == 1
                    && model.GetTypeInfo(ag.TypeArgumentList.Arguments[0]).Type is INamedTypeSymbol at)
                {
                    filter = AndFilter(filter, t => IsAssignableTo(t, at));
                }
                else if (Constants.ScanServiceTypeNames.Contains(mname))
                {
                    if (!CollectScanServiceTypes(ma.Name, pInv, model, serviceTypes)) { keepAll = true; }
                }
                else if (Constants.AutofacActivationNames.Contains(mname))
                {
                    keepAll = true;
                }
                else if (!Constants.AutofacScanNarrowingNames.Contains(mname) && !Constants.ScanAllInterfacesNames.Contains(mname)
                    && !Constants.ScanSelfNames.Contains(mname) && !Constants.ScanMatchingInterfaceNames.Contains(mname)
                    && !Constants.AutofacChainContinuers.Contains(mname) && !Constants.AutofacExtraLifetimeNames.Contains(mname))
                {
                    // A call this reading does not know may register services it cannot see.
                    keepAll = true;
                }
                region = pInv;
                cur = pInv;
            }
        }
        else
        {
            // The whole outermost statement: Scan(s => s.FromAssemblyOf<X>().AddClasses().AsMatchingInterface()).
            region = inv.AncestorsAndSelf().OfType<ExpressionStatementSyntax>().LastOrDefault() ?? (SyntaxNode)inv;
            bool anyAssemblySource = false;
            foreach (var call in region.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (GetMethodName(call) is not { } cn) { continue; }
                var cname = cn.Identifier.Text;
                callNames.Add(cname);
                if (Constants.ScanAssemblyOfTypeNames.Contains(cname))
                {
                    anyAssemblySource = true;
                    // FromAssemblyOf<X>(), FromAssembliesOf(typeof(A), typeof(B)): every named type counts.
                    var ofTypes = new List<ITypeSymbol>();
                    if (cn is GenericNameSyntax gn)
                    {
                        ofTypes.AddRange(gn.TypeArgumentList.Arguments.Select(ta => model.GetTypeInfo(ta).Type));
                    }
                    ofTypes.AddRange(call.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().Select(t => model.GetTypeInfo(t.Type).Type));
                    if (ofTypes.Count == 0 || call.ArgumentList.Arguments.Any(a => a.Expression is not TypeOfExpressionSyntax)) { assembliesKnown = false; }
                    foreach (var of in ofTypes)
                    {
                        if (of?.ContainingAssembly?.Name is { } oa) { assemblies.Add(oa); }
                        else { assembliesKnown = false; }
                    }
                }
                else if (Constants.ScanCurrentAssemblyNames.Contains(cname))
                {
                    anyAssemblySource = true;
                    assemblies.Add(currentAsm);
                }
                else if (Constants.ScanUnknownAssemblyNames.Contains(cname))
                {
                    anyAssemblySource = true;
                    if (Constants.ScanAssemblyListNames.Contains(cname) && call.ArgumentList.Arguments.Count > 0)
                    {
                        // FromAssemblies(typeof(X).Assembly) is still precise.
                        foreach (var arg in call.ArgumentList.Arguments)
                        {
                            if (ScanAssemblyOf(arg.Expression, model, currentAsm) is { } a) { assemblies.Add(a); }
                            else { assembliesKnown = false; }
                        }
                    }
                    else
                    {
                        assembliesKnown = false;
                    }
                }
                else if (Constants.ScanServiceTypeNames.Contains(cname) && call.Expression is MemberAccessExpressionSyntax sma)
                {
                    if (!CollectScanServiceTypes(sma.Name, call, model, serviceTypes)) { keepAll = true; }
                }
            }
            foreach (var ma in region.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            {
                if (ma.Name.Identifier.Text == "Assembly" && ma.Expression is TypeOfExpressionSyntax tof && model.GetTypeInfo(tof.Type).Type?.ContainingAssembly?.Name is { } ta)
                {
                    anyAssemblySource = true;
                    assemblies.Add(ta);
                }
                else if (Constants.ScanMatchingInterfaceNames.Contains(ma.Name.Identifier.Text) || Constants.ScanAllInterfacesNames.Contains(ma.Name.Identifier.Text))
                {
                    // Unity passes WithMappings.FromMatchingInterface as a method group, not a call.
                    callNames.Add(ma.Name.Identifier.Text);
                }
            }
            bool registers = callNames.Overlaps(Constants.ScanAllInterfacesNames) || callNames.Overlaps(Constants.ScanMatchingInterfaceNames)
                || callNames.Overlaps(Constants.ScanSelfNames) || callNames.Overlaps(Constants.ScanKeepAllNames)
                || callNames.Overlaps(Constants.ScanSelectionNames) || serviceTypes.Count > 0;
            // Neither assembly nor registration vocabulary: not a registration scan (some other "Scan" method).
            if (!anyAssemblySource && !registers) { return; }
            // Registration vocabulary over assemblies named in a way this reading does not know.
            if (!anyAssemblySource) { assembliesKnown = false; }
            if (callNames.Overlaps(Constants.ScanKeepAllNames)) { keepAll = true; }
            filterExact = false;
        }
        if (!_scanRegions.Add(region)) { return; }

        ScanMode mode;
        if (keepAll) { mode = ScanMode.KeepAll; }
        else if (callNames.Overlaps(Constants.ScanAllInterfacesNames)) { mode = ScanMode.AllInterfaces; }
        else if (callNames.Overlaps(Constants.ScanMatchingInterfaceNames)) { mode = ScanMode.MatchingInterface; }
        // Autofac registers scanned types as themselves unless told otherwise.
        else if (callNames.Overlaps(Constants.ScanSelfNames) || isAutofac || serviceTypes.Count > 0) { mode = ScanMode.SelfOnly; }
        else { mode = ScanMode.Unknown; }

        _graph.ScanRegistrations.Add(new ScanRegistration
        {
            SourceId = sourceId,
            Site = site,
            Assemblies = assembliesKnown && assemblies.Count > 0 ? assemblies : null,
            Filter = filter,
            FilterExact = filterExact,
            Mode = mode,
            ServiceTypes = serviceTypes,
            RegistersSelf = callNames.Overlaps(Constants.ScanSelfNames) || (mode == ScanMode.SelfOnly && serviceTypes.Count == 0),
        });
    }

    /// <summary>The service types a scan registration call names by generic argument or typeof:
    /// As&lt;Base&gt;(), As(typeof(IFoo)), AsClosedTypesOf(typeof(IHandler&lt;&gt;)), Named&lt;IPlugin&gt;(t =&gt; t.Name).
    /// False when the call names its services some other way (a lambda, a variable).</summary>
    bool CollectScanServiceTypes(SimpleNameSyntax name, InvocationExpressionSyntax call, SemanticModel model, List<INamedTypeSymbol> serviceTypes)
    {
        int found = 0;
        if (name is GenericNameSyntax g)
        {
            foreach (var ta in g.TypeArgumentList.Arguments)
            {
                if (model.GetTypeInfo(ta).Type is not INamedTypeSymbol st) { return false; }
                serviceTypes.Add(st.OriginalDefinition);
                found++;
            }
        }
        foreach (var arg in call.ArgumentList.Arguments)
        {
            if (arg.Expression is TypeOfExpressionSyntax tof)
            {
                if (model.GetTypeInfo(tof.Type).Type is not INamedTypeSymbol st) { return false; }
                serviceTypes.Add(st.OriginalDefinition);
                found++;
            }
            else if (name.Identifier.Text is "Named" or "Keyed")
            {
                // The name/key argument (a string, or a t => t.Name lambda): not a service.
                continue;
            }
            else
            {
                // As(t => ...) selecting services by code, or a Type variable: not followable.
                return false;
            }
        }
        return found > 0;
    }

    /// <summary>The assembly an expression like typeof(X).Assembly or Assembly.GetExecutingAssembly()
    /// names, or null when it cannot be determined.</summary>
    string ScanAssemblyOf(ExpressionSyntax expr, SemanticModel model, string currentAsm)
    {
        if (expr is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Assembly")
        {
            var inner = ma.Expression;
            // typeof(X).GetTypeInfo().Assembly
            if (inner is InvocationExpressionSyntax gti && gti.Expression is MemberAccessExpressionSyntax gtiMa && gtiMa.Name.Identifier.Text == "GetTypeInfo") { inner = gtiMa.Expression; }
            if (inner is TypeOfExpressionSyntax tof) { return model.GetTypeInfo(tof.Type).Type?.ContainingAssembly?.Name; }
            return null;
        }
        if (expr is InvocationExpressionSyntax call && GetMethodName(call) is { } n && Constants.ScanCurrentAssemblyNames.Contains(n.Identifier.Text))
        {
            return currentAsm;
        }
        return null;
    }

    Func<INamedTypeSymbol, bool> AndFilter(Func<INamedTypeSymbol, bool> a, Func<INamedTypeSymbol, bool> b)
    {
        if (a == null) { return b; }
        return t => a(t) && b(t);
    }

    bool IsAssignableTo(INamedTypeSymbol t, INamedTypeSymbol target)
    {
        var def = target.OriginalDefinition;
        for (var b = (INamedTypeSymbol)t; b != null; b = b.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, def)) { return true; }
        }
        return t.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, def));
    }

    /// <summary>Interprets a scan filter lambda (t => t.Name.EndsWith("Service") || t.IsSubclassOf(typeof(B))).
    /// Returns the predicate and whether it is EXACT. A part that cannot be read is widened to
    /// "matches" (a superset: classes are over-kept, never under-kept); null means "every class".</summary>
    (Func<INamedTypeSymbol, bool> Filter, bool Exact) ParseScanFilter(LambdaExpressionSyntax lambda, SemanticModel model)
    {
        string param = lambda switch
        {
            SimpleLambdaExpressionSyntax s => s.Parameter.Identifier.Text,
            ParenthesizedLambdaExpressionSyntax p when p.ParameterList.Parameters.Count == 1 => p.ParameterList.Parameters[0].Identifier.Text,
            _ => null,
        };
        if (param == null || lambda.Body is not ExpressionSyntax body) { return (null, false); }
        return ParseScanPredicate(body, param, model);
    }

    (Func<INamedTypeSymbol, bool> Filter, bool Exact) ParseScanPredicate(ExpressionSyntax e, string p, SemanticModel model)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax pe:
                return ParseScanPredicate(pe.Expression, p, model);
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression):
            {
                var (l, le) = ParseScanPredicate(b.Left, p, model);
                var (r, re) = ParseScanPredicate(b.Right, p, model);
                // An unreadable side of an AND only widens the set: keep the readable side.
                if (l == null) { return (r, false); }
                if (r == null) { return (l, false); }
                return (t => l(t) && r(t), le && re);
            }
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression):
            {
                var (l, le) = ParseScanPredicate(b.Left, p, model);
                var (r, re) = ParseScanPredicate(b.Right, p, model);
                if (l == null || r == null) { return (null, false); }
                return (t => l(t) || r(t), le && re);
            }
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
            {
                // Negating an approximation is not safe: only an exact inner predicate may be negated.
                var (inner, exact) = ParseScanPredicate(u.Operand, p, model);
                if (inner == null || !exact) { return (null, false); }
                return (t => !inner(t), true);
            }
            case BinaryExpressionSyntax eq when eq.IsKind(SyntaxKind.EqualsExpression) || eq.IsKind(SyntaxKind.NotEqualsExpression):
            {
                var lhs = TypeStringOf(eq.Left, p);
                var rhs = TypeStringOf(eq.Right, p);
                var getter = lhs ?? rhs;
                if (getter == null) { return (null, false); }
                var other = lhs != null ? eq.Right : eq.Left;
                bool negate = eq.IsKind(SyntaxKind.NotEqualsExpression);
                // t.Namespace != null: the usual guard in front of t.Namespace.StartsWith(...).
                if (other.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    return (t => (getter(t) == null) != negate, true);
                }
                var lit = StringLiteral(other, model);
                if (lit == null) { return (null, false); }
                return (t => (getter(t) == lit) != negate, true);
            }
            case InvocationExpressionSyntax call when call.Expression is MemberAccessExpressionSyntax cma:
            {
                var method = cma.Name.Identifier.Text;
                var args = call.ArgumentList.Arguments;
                // t.Name.EndsWith("Service") / StartsWith / Contains, optionally with a StringComparison.
                if (method is "EndsWith" or "StartsWith" or "Contains" && args.Count >= 1 && TypeStringOf(cma.Expression, p) is { } getter
                    && StringLiteral(args[0].Expression, model) is { } lit)
                {
                    var cmp = StringComparison.Ordinal;
                    if (args.Count == 2)
                    {
                        // (value, StringComparison): the odd enum values are the IgnoreCase ones.
                        if (model.GetConstantValue(args[1].Expression) is not { HasValue: true, Value: int cmpValue }) { return (null, false); }
                        if (cmpValue % 2 == 1) { cmp = StringComparison.OrdinalIgnoreCase; }
                    }
                    else if (args.Count == 3)
                    {
                        // (value, bool ignoreCase, CultureInfo).
                        if (model.GetConstantValue(args[1].Expression) is not { HasValue: true, Value: bool ignoreCase }) { return (null, false); }
                        if (ignoreCase) { cmp = StringComparison.OrdinalIgnoreCase; }
                    }
                    else if (args.Count > 3)
                    {
                        return (null, false);
                    }
                    // A null string (a type in the global namespace has no Namespace) matches nothing.
                    return method switch
                    {
                        "EndsWith" => (t => getter(t) is { } v && v.EndsWith(lit, cmp), true),
                        "StartsWith" => (t => getter(t) is { } v && v.StartsWith(lit, cmp), true),
                        _ => (t => getter(t) is { } v && v.Contains(lit, cmp), true),
                    };
                }
                // t.IsSubclassOf(typeof(B)) / t.IsAssignableTo(typeof(B))
                if (method is "IsSubclassOf" or "IsAssignableTo" && IsParam(cma.Expression, p) && args.Count == 1
                    && args[0].Expression is TypeOfExpressionSyntax tof && model.GetTypeInfo(tof.Type).Type is INamedTypeSymbol bt)
                {
                    if (method == "IsSubclassOf") { return (t => !SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, bt.OriginalDefinition) && IsAssignableTo(t, bt) && bt.TypeKind != TypeKind.Interface, true); }
                    return (t => IsAssignableTo(t, bt), true);
                }
                // typeof(B).IsAssignableFrom(t)
                if (method == "IsAssignableFrom" && cma.Expression is TypeOfExpressionSyntax tof2 && args.Count == 1 && IsParam(args[0].Expression, p)
                    && model.GetTypeInfo(tof2.Type).Type is INamedTypeSymbol at)
                {
                    return (t => IsAssignableTo(t, at), true);
                }
                return (null, false);
            }
            case MemberAccessExpressionSyntax flag when IsParam(flag.Expression, p):
            {
                // Boolean Type properties: t.IsClass, t.IsAbstract, t.IsPublic, ...
                Func<INamedTypeSymbol, bool> f = flag.Name.Identifier.Text switch
                {
                    "IsClass" => t => t.TypeKind == TypeKind.Class,
                    "IsInterface" => t => t.TypeKind == TypeKind.Interface,
                    "IsAbstract" => t => t.IsAbstract,
                    "IsSealed" => t => t.IsSealed,
                    "IsGenericType" => t => t.IsGenericType,
                    "IsPublic" => t => t.DeclaredAccessibility == Accessibility.Public && t.ContainingType == null,
                    _ => null,
                };
                return (f, f != null);
            }
            default:
                return (null, false);
        }
    }

    bool IsParam(ExpressionSyntax e, string p)
    {
        return e is IdentifierNameSyntax id && id.Identifier.Text == p;
    }

    /// <summary>A reader for t.Name / t.Namespace / t.FullName, or null.</summary>
    Func<INamedTypeSymbol, string> TypeStringOf(ExpressionSyntax e, string p)
    {
        if (e is not MemberAccessExpressionSyntax ma || !IsParam(ma.Expression, p)) { return null; }
        return ma.Name.Identifier.Text switch
        {
            "Name" => t => t.MetadataName,
            "Namespace" => t => t.ContainingNamespace?.IsGlobalNamespace == false ? t.ContainingNamespace.ToDisplayString() : null,
            "FullName" => t => SymbolNames.MetadataFqn(t),
            _ => null,
        };
    }

    string StringLiteral(ExpressionSyntax e, SemanticModel model)
    {
        var c = model.GetConstantValue(e);
        return c.HasValue && c.Value is string s ? s : null;
    }

    /// <summary>Turns every read scan into rules: a matched class lives while the scanning member AND
    /// one interface it is registered under live (as any registration does), so implementations of
    /// interfaces nobody uses any more still die.</summary>
    void ApplyScanRules(List<string> solutionAssemblies)
    {
        foreach (var scan in _graph.ScanRegistrations)
        {
            if (!scan.FilterExact)
            {
                _graph.Warnings.Add($"assembly scan at {scan.Site}: its class filter could not be read in full — every class it might match is assumed registered (over-keeps)");
            }
            if (scan.Assemblies == null)
            {
                _graph.Warnings.Add($"assembly scan at {scan.Site}: the scanned assemblies could not be determined — every solution assembly is assumed scanned (over-keeps)");
            }
            var asms = scan.Assemblies ?? new HashSet<string>(solutionAssemblies, StringComparer.OrdinalIgnoreCase);
            foreach (var (type, _) in _types)
            {
                if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic) { continue; }
                if (type.ContainingAssembly?.Name is not { } tasm || !asms.Contains(tasm)) { continue; }
                if (scan.Filter != null && !scan.Filter(type)) { continue; }
                if (NodeId(type) is not { } typeId) { continue; }
                // Recorded only when the scan actually registers the class (as itself or under a
                // service): a filter match the registration style then ignores registers nothing.
                if (scan.Mode is ScanMode.KeepAll or ScanMode.Unknown || scan.RegistersSelf || scan.ServiceTypes.Any(svc => IsAssignableTo(type, svc))
                    || (scan.Mode is ScanMode.AllInterfaces or ScanMode.MatchingInterface && type.AllInterfaces.Any(i => scan.Mode == ScanMode.AllInterfaces || IsDefaultInterfaceOf(i, type))))
                {
                    _graph.ScanMatchedTypes.TryAdd(typeId, scan.Site);
                }
                var ctorIds = type.InstanceConstructors.Select(NodeId).Where(id => id != null).Cast<object>().ToList();
                // The container constructs a registered class itself: no source names its constructors.
                if (ctorIds.Count > 0)
                {
                    _graph.AddRule(new object[] { scan.SourceId, typeId }, ctorIds, "assembly-scanned class: the container constructs it");
                }
                var payload = new List<object> { typeId };
                payload.AddRange(ctorIds);
                if (scan.Mode is ScanMode.KeepAll or ScanMode.Unknown)
                {
                    _graph.AddRule(new object[] { scan.SourceId }, payload, "assembly-scan registration (style not readable)");
                    continue;
                }
                foreach (var svc in scan.ServiceTypes)
                {
                    if (!IsAssignableTo(type, svc)) { continue; }
                    if (NodeId(svc) is { } svcId)
                    {
                        _graph.AddRule(new object[] { scan.SourceId, svcId }, payload, "assembly-scan registration");
                    }
                    else
                    {
                        _graph.AddRule(new object[] { scan.SourceId }, payload, "assembly-scan registration (external service)");
                    }
                }
                if (scan.Mode is not (ScanMode.AllInterfaces or ScanMode.MatchingInterface)) { continue; }
                foreach (var iface in type.AllInterfaces)
                {
                    // Autofac itself excludes these from AsImplementedInterfaces.
                    if (iface.ContainingNamespace is { IsGlobalNamespace: false } ins && ins.ToDisplayString() == "System" && iface.Name is "IDisposable" or "IAsyncDisposable") { continue; }
                    if (scan.Mode == ScanMode.MatchingInterface && !IsDefaultInterfaceOf(iface, type)) { continue; }
                    if (NodeId(iface) is { } ifaceId)
                    {
                        _graph.AddRule(new object[] { scan.SourceId, ifaceId }, payload, "assembly-scan registration");
                    }
                    else
                    {
                        // An external service: resolved outside the solution's sight.
                        _graph.AddRule(new object[] { scan.SourceId }, payload, "assembly-scan registration (external service)");
                    }
                }
            }
        }
    }

    /// <summary>UseStartup&lt;ApiStartup&gt;() / UseStartup(typeof(ApiStartup)): the host calls the class's
    /// ConfigureServices/Configure methods by convention, so it is rooted like a class named Startup.</summary>
    void RootStartupClass(InvocationExpressionSyntax inv, SimpleNameSyntax name, SemanticModel model)
    {
        var types = new List<ITypeSymbol>();
        if (name is GenericNameSyntax g) { types.AddRange(g.TypeArgumentList.Arguments.Select(ta => model.GetTypeInfo(ta).Type)); }
        types.AddRange(inv.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().Select(t => model.GetTypeInfo(t.Type).Type));
        foreach (var t in types.OfType<INamedTypeSymbol>())
        {
            var def = t.OriginalDefinition;
            if (NodeId(def) is not { } typeId) { continue; }
            bool isTest = _graph.TestNodes.Contains(typeId);
            _graph.AddRoot(typeId, "startup class", isTest);
            foreach (var m in def.GetMembers())
            {
                bool isCtor = m is IMethodSymbol { MethodKind: MethodKind.Constructor };
                if ((isCtor || m.DeclaredAccessibility == Accessibility.Public) && NodeId(m) is { } mId)
                {
                    _graph.AddRoot(mId, "startup class", isTest);
                }
            }
        }
    }

    /// <summary>The widest "default interface" rule of the scanning libraries: Windsor and Ninject's
    /// plural form match when the class name CONTAINS the interface name without its leading "I"
    /// (SqlOrderRepository -> IOrderRepository); Ninject's singular form ignores case. The exact
    /// "I" + name conventions (StructureMap, Scrutor, Unity) are a subset.</summary>
    bool IsDefaultInterfaceOf(INamedTypeSymbol iface, INamedTypeSymbol type)
    {
        var bare = iface.Name.Length > 1 && iface.Name[0] == 'I' ? iface.Name[1..] : iface.Name;
        return type.Name.Contains(bare, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------- co-dependent statements

    List<TextSpan> MatchStatements(SyntaxNode subtree, string sourceId, SemanticModel model)
    {
        List<TextSpan> skip = null;
        foreach (var inv in subtree.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (_processedInvocations.Contains(inv)) { continue; }
            var name = GetMethodName(inv);
            if (name == null) { continue; }
            var id = name.Identifier.Text;
            if (Constants.AssemblyScanMethodNames.Contains(id)) { RecordScan(inv, id, sourceId, model); }
            if (Constants.UseStartupMethodNames.Contains(id)) { RootStartupClass(inv, name, model); }
            var typeArgs = (name as GenericNameSyntax)?.TypeArgumentList.Arguments;

            bool looksTracked = (id == Constants.CreateMapMethodName && typeArgs is { Count: 2 })
                || Constants.MsDiMethodNames.Contains(id) || Constants.AutofacRegisterMethodNames.Contains(id);
            if (!looksTracked) { continue; }
            // A tracked NAME alone is not enough: the resolved method must come from OUTSIDE the
            // solution (AutoMapper, MS-DI, Autofac) — a domain method that happens to share the
            // name keeps plain edge semantics, or its call sites would inherit registration
            // deletability. An unresolvable method (partially loaded legacy solution) still counts
            // as external. Config "inSolutionRegistrations" opts in solution-declared stand-ins.
            if (!_config.InSolutionRegistrations)
            {
                var resolvedAsm = model.GetSymbolInfo(inv).Symbol?.ContainingAssembly?.Name;
                if (resolvedAsm != null && _solutionAssemblies.Contains(resolvedAsm)) { continue; }
            }
            if (id == Constants.CreateMapMethodName && typeArgs is { Count: 2 })
            {
                HandleCreateMap(inv, typeArgs.Value, sourceId, model, ref skip);
            }
            else if (Constants.MsDiMethodNames.Contains(id))
            {
                HandleMsDi(inv, typeArgs, sourceId, model, ref skip);
            }
            else if (Constants.AutofacRegisterMethodNames.Contains(id))
            {
                HandleAutofac(inv, typeArgs, sourceId, model, ref skip);
            }
        }
        return skip;
    }

    SimpleNameSyntax GetMethodName(InvocationExpressionSyntax inv)
    {
        return inv.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name,
            SimpleNameSyntax sn => sn,
            _ => null,
        };
    }

    string ResolveTypeNode(SyntaxNode typeSyntax, SemanticModel model)
    {
        var sym = model.GetSymbolInfo(typeSyntax).Symbol
                  ?? (typeSyntax is TypeSyntax ts ? model.GetTypeInfo(ts).Type : null);
        return sym is ITypeSymbol t ? NodeId(t) : null;
    }

    /// <summary>Every node referenced anywhere inside a type-argument syntax, nested type arguments
    /// included ("IRepo&lt;Customer&gt;" yields both IRepo`1 and Customer). The whole span is
    /// suppressed in the walks, so these must become statement references or they are lost.</summary>
    void CollectTypeRefNodes(SyntaxNode typeSyntax, SemanticModel model, List<string> into)
    {
        foreach (var n in typeSyntax.DescendantNodesAndSelf())
        {
            if (n is not (IdentifierNameSyntax or GenericNameSyntax)) { continue; }
            if (Normalize(model.GetSymbolInfo(n).Symbol) is { } nid && !into.Contains(nid)) { into.Add(nid); }
        }
    }

    /// <summary>True when the construct's header (condition, iterator, resource) contains anything
    /// with a side effect outside the registration itself: deleting the whole construct would then
    /// silently drop behavior, so the statement must be flagged for manual removal instead.</summary>
    bool HeaderHasSideEffects(SyntaxNode construct, SyntaxNode body)
    {
        foreach (var n in construct.DescendantNodes(d => d != body))
        {
            if (n == body || body.Span.Contains(n.Span)) { continue; }
            if (n is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax or AwaitExpressionSyntax or AssignmentExpressionSyntax) { return true; }
            var kind = n.Kind();
            if (kind is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
                or SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression)
            {
                return true;
            }
        }
        return false;
    }

    StatementNode NewStatement(string kind, InvocationExpressionSyntax inv, string containingMember, IEnumerable<InvocationExpressionSyntax> claimed, IEnumerable<string> involved, bool allowSegment = true, InvocationExpressionSyntax segmentEnd = null)
    {
        var innerStatement = inv.FirstAncestorOrSelf<StatementSyntax>();
        SyntaxNode anchor = innerStatement ?? (SyntaxNode)inv;
        string manualReason = null;
        // No statement ancestor: the registration IS the body of an expression-bodied member or an
        // initializer. Deleting the whole span would leave "=> ;" — plan a manual removal instead.
        if (innerStatement == null)
        {
            manualReason = "the registration is the body of an expression-bodied member or initializer";
        }
        // A registration inside a lambda (factory delegates, MapperConfiguration callbacks) cannot be
        // deleted by span arithmetic without breaking the surrounding expression.
        for (var a = inv.Parent; a != null && a != innerStatement; a = a.Parent)
        {
            if (a is AnonymousFunctionExpressionSyntax)
            {
                manualReason = "the registration sits inside a lambda";
                break;
            }
        }
        // A braceless embedded statement cannot be deleted alone: expand to the construct that
        // owns it (deleting "if (x) Register();" whole) — unless the construct's header has side
        // effects, or an else branch makes whole-construct deletion wrong; then flag for manual action.
        while (innerStatement != null && manualReason == null)
        {
            var parent = anchor.Parent;
            SyntaxNode construct = null;
            if (parent is IfStatementSyntax ifs && ifs.Statement == anchor)
            {
                if (ifs.Else != null)
                {
                    manualReason = "the registration sits in an if/else branch";
                    break;
                }
                construct = ifs;
            }
            else if (parent is ElseClauseSyntax ec && ec.Statement == anchor)
            {
                anchor = ec;
                break;
            }
            else if (parent is WhileStatementSyntax w && w.Statement == anchor) { construct = w; }
            else if (parent is ForStatementSyntax f && f.Statement == anchor) { construct = f; }
            else if (parent is ForEachStatementSyntax fe && fe.Statement == anchor) { construct = fe; }
            else if (parent is DoStatementSyntax d && d.Statement == anchor) { construct = d; }
            else if (parent is UsingStatementSyntax us && us.Statement == anchor) { construct = us; }
            else if (parent is LockStatementSyntax lk && lk.Statement == anchor) { construct = lk; }
            else { break; }
            if (HeaderHasSideEffects(construct, anchor))
            {
                manualReason = "the surrounding statement's condition/header has side effects";
                break;
            }
            anchor = construct;
        }
        // Exclude leading #directives from the deletable span so their counterparts stay paired.
        int anchorStart = anchor.FullSpan.Start;
        var leading = anchor.GetLeadingTrivia();
        for (int ti = leading.Count - 1; ti >= 0; ti--)
        {
            if (leading[ti].IsDirective)
            {
                anchorStart = leading[ti].FullSpan.End;
                break;
            }
        }
        int segmentStart = -1, segmentLength = 0;
        if (allowSegment && inv.Expression is MemberAccessExpressionSyntax chain)
        {
            // The segment runs to the end of the CLAIMED chain (ForMember, ReverseMap, ...), not
            // just this call: a shorter cut would leave the continuation dangling off the receiver.
            segmentStart = chain.Expression.Span.End;
            segmentLength = (segmentEnd ?? inv).Span.End - segmentStart;
        }
        var lines = anchor.GetLocation().GetLineSpan();
        var snippet = Regex.Replace(anchor.ToString(), @"\s+", " ").Trim();
        if (snippet.Length > Constants.SnippetMaxLength) { snippet = snippet[..(Constants.SnippetMaxLength - 3)] + "..."; }
        var stmt = new StatementNode
        {
            Kind = kind,
            FilePath = anchor.SyntaxTree.FilePath,
            StartLine = lines.StartLinePosition.Line + 1,
            EndLine = lines.EndLinePosition.Line + 1,
            Snippet = snippet,
            ContainingMember = containingMember,
            AnchorStart = anchorStart,
            AnchorLength = anchor.FullSpan.End - anchorStart,
            SegmentStart = segmentStart,
            SegmentLength = segmentLength,
            NameStart = GetMethodName(inv)?.SpanStart ?? inv.SpanStart,
            MarkerLineInsideToken = Editor.LineStartsInsideToken(anchor.SyntaxTree, lines.StartLinePosition.Line),
            FileTextLength = anchor.SyntaxTree.Length,
            FileTextHash = TreeHash(anchor.SyntaxTree),
            ManualReason = manualReason,
        };
        foreach (var nid in involved)
        {
            if (nid != null && !stmt.InvolvedNodes.Contains(nid)) { stmt.InvolvedNodes.Add(nid); }
        }
        _graph.Statements.Add(stmt);
        if (!_anchors.TryGetValue(anchor, out var ai)) { _anchors[anchor] = ai = new AnchorInfo { InnerStatement = innerStatement }; }
        ai.Statements.Add(stmt);
        foreach (var ci in claimed) { ai.Claimed.Add(ci); }
        return stmt;
    }

    void AddSkip(ref List<TextSpan> skip, TextSpan span)
    {
        skip ??= new List<TextSpan>();
        skip.Add(span);
    }

    string TreeHash(SyntaxTree tree)
    {
        if (!_treeHashes.TryGetValue(tree, out var h)) { _treeHashes[tree] = h = Editor.TextHash(tree.GetText().ToString()); }
        return h;
    }

    void HandleCreateMap(InvocationExpressionSyntax inv, SeparatedSyntaxList<TypeSyntax> typeArgs, string sourceId, SemanticModel model, ref List<TextSpan> skip)
    {
        var involved = new List<string>();
        var argNodes = new List<string>();
        foreach (var ta in typeArgs)
        {
            if (ResolveTypeNode(ta, model) is { } t) { involved.Add(t); }
            CollectTypeRefNodes(ta, model, argNodes);
        }
        if (involved.Count == 0) { return; }
        _processedInvocations.Add(inv);
        // The suppressed call still references its target method: when that is the solution's OWN
        // look-alike (not the framework's), the suppression must not orphan it.
        AddEdgeTo(sourceId, model.GetSymbolInfo(inv).Symbol);
        foreach (var ta in typeArgs) { AddSkip(ref skip, ta.Span); }

        // The whole fluent chain hanging off CreateMap (ForMember, ReverseMap, ...) is map
        // configuration: it lives and dies with the mapping, so the statement claims it — including
        // every reference inside its lambdas. Those must NOT become edges from the containing member
        // (a rooted profile would keep the mapped members alive forever); they belong to the
        // statement, which keeps them while it lives and releases them when it dies.
        var claimed = new List<InvocationExpressionSyntax> { inv };
        var chainRefs = new List<string>();        // every chained ref: kept alive while the statement lives
        var structuralRefs = new List<string>();   // refs of structural calls (ConvertUsing, Include): kill the whole statement
        var segments = new List<StatementSegment>();
        var chainTop = (SyntaxNode)inv;
        while (chainTop.Parent is MemberAccessExpressionSyntax cma && cma.Parent is InvocationExpressionSyntax cInv)
        {
            claimed.Add(cInv);
            _processedInvocations.Add(cInv);
            AddSkip(ref skip, TextSpan.FromBounds(cma.Name.SpanStart, cInv.Span.End));
            var segRefs = new List<string>();
            // The chained call's own TARGET: a solution-declared extension method on the mapping
            // expression (.IgnoreAllNonExisting()) is called from here and nowhere else visible.
            if (Normalize(model.GetSymbolInfo(cInv).Symbol) is { } chainTarget) { segRefs.Add(chainTarget); }
            foreach (var idn in cInv.ArgumentList.DescendantNodes())
            {
                if (idn is not (IdentifierNameSyntax or GenericNameSyntax)) { continue; }
                if (Normalize(model.GetSymbolInfo(idn).Symbol) is { } nid && !segRefs.Contains(nid)) { segRefs.Add(nid); }
            }
            // Generic type arguments on the chained call's NAME (ConvertUsing<TConverter>,
            // Include<TDerived,TOther>) sit inside the suppressed span and appear in no argument
            // list: claim them as statement references or they are lost entirely.
            if (cma.Name is GenericNameSyntax chainedGeneric)
            {
                foreach (var ta in chainedGeneric.TypeArgumentList.Arguments) { CollectTypeRefNodes(ta, model, segRefs); }
            }
            foreach (var r in segRefs)
            {
                if (!chainRefs.Contains(r)) { chainRefs.Add(r); }
            }
            var mname = cma.Name.Identifier.Text;
            if (Constants.MemberLevelMapChainNames.Contains(mname))
            {
                // The first argument names the member being configured; everything else is used.
                var configured = new List<string>();
                if (cInv.ArgumentList.Arguments.Count > 0)
                {
                    foreach (var idn in cInv.ArgumentList.Arguments[0].DescendantNodes())
                    {
                        if (idn is not (IdentifierNameSyntax or GenericNameSyntax)) { continue; }
                        if (Normalize(model.GetSymbolInfo(idn).Symbol) is { } cid && !configured.Contains(cid)) { configured.Add(cid); }
                    }
                }
                // Member-level configuration: when its member dies, only THIS segment goes and the
                // rest of the mapping stands. The span starts at the '.', so the chain stays valid.
                var segLines = cInv.GetLocation().GetLineSpan();
                var segSnippet = Regex.Replace("." + cma.Name + cInv.ArgumentList, @"\s+", " ").Trim();
                if (segSnippet.Length > Constants.SnippetMaxLength) { segSnippet = segSnippet[..(Constants.SnippetMaxLength - 3)] + "..."; }
                var segLine0 = cma.Name.GetLocation().GetLineSpan().StartLinePosition.Line;
                segments.Add(new StatementSegment
                {
                    Start = cma.OperatorToken.SpanStart,
                    Length = cInv.Span.End - cma.OperatorToken.SpanStart,
                    MarkerLineInsideToken = Editor.LineStartsInsideToken(cInv.SyntaxTree, segLine0),
                    Line = segLine0 + 1,
                    EndLine = segLines.EndLinePosition.Line + 1,
                    MethodName = mname,
                    Snippet = segSnippet,
                    Refs = segRefs,
                    UsedRefs = segRefs.Where(r => !configured.Contains(r)).ToList(),
                });
            }
            else
            {
                foreach (var r in segRefs)
                {
                    if (!structuralRefs.Contains(r)) { structuralRefs.Add(r); }
                }
            }
            chainTop = cInv;
        }
        var stmt = NewStatement("AutoMapper.CreateMap", inv, sourceId, claimed, involved.Concat(structuralRefs).Concat(argNodes), segmentEnd: (InvocationExpressionSyntax)chainTop);
        stmt.ChainSegments.AddRange(segments);
        stmt.UsedRefs.AddRange(structuralRefs.Where(r => !involved.Contains(r) && !argNodes.Contains(r)));
        var guards = new List<object> { sourceId };
        guards.AddRange(involved);
        _graph.AddRule(guards, new object[] { stmt }, "CreateMap in use iff both types in use");
        // Nested type arguments ("CreateMap<Dto<Customer>, ...>") sit inside suppressed spans: the
        // statement must keep them alive while it lives, or they orphan under surviving text.
        var extraArgs = argNodes.Where(a => !involved.Contains(a)).Cast<object>().ToList();
        if (extraArgs.Count > 0)
        {
            _graph.AddRule(new object[] { stmt }, extraArgs, "type arguments of the mapping");
        }
        if (chainRefs.Count > 0)
        {
            _graph.AddRule(new object[] { stmt }, chainRefs.Cast<object>().ToList(), "map configuration references");
        }

        if (!_config.StrictMembers)
        {
            var props = involved
                .Select(t => _graph.Decl.TryGetValue(t, out var d) ? d as INamedTypeSymbol : null)
                .Where(t => t != null)
                .SelectMany(t => t.GetMembers().OfType<IPropertySymbol>())
                .Select(NodeId)
                .Where(pid => pid != null)
                .Cast<object>()
                .ToList();
            if (props.Count > 0)
            {
                _graph.AddRule(new object[] { stmt }, props, "mapped properties used via reflection");
            }
        }
    }

    void HandleMsDi(InvocationExpressionSyntax inv, SeparatedSyntaxList<TypeSyntax>? typeArgs, string sourceId, SemanticModel model, ref List<TextSpan> skip)
    {
        string service = null, impl = null;
        bool externalService = false;
        var spansToSkip = new List<TextSpan>();
        var argNodes = new List<string>();
        if (typeArgs is { Count: 2 })
        {
            service = ResolveTypeNode(typeArgs.Value[0], model);
            impl = ResolveTypeNode(typeArgs.Value[1], model);
            externalService = service == null;
            spansToSkip.Add(typeArgs.Value[0].Span);
            spansToSkip.Add(typeArgs.Value[1].Span);
            CollectTypeRefNodes(typeArgs.Value[0], model, argNodes);
            CollectTypeRefNodes(typeArgs.Value[1], model, argNodes);
        }
        else if (typeArgs is { Count: 1 })
        {
            service = impl = ResolveTypeNode(typeArgs.Value[0], model);
            spansToSkip.Add(typeArgs.Value[0].Span);
            CollectTypeRefNodes(typeArgs.Value[0], model, argNodes);
        }
        else if (typeArgs == null)
        {
            var typeofs = inv.ArgumentList.Arguments
                .Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList();
            if (typeofs.Count == 0) { return; }
            service = ResolveTypeNode(typeofs[0].Type, model);
            impl = typeofs.Count > 1 ? ResolveTypeNode(typeofs[1].Type, model) : service;
            externalService = service == null && typeofs.Count > 1;
            // The WHOLE typeof expression: its ITypeOfOperation spans it entirely, and a narrower
            // skip would let the operation walk re-pin the type with a permanent edge.
            foreach (var t in typeofs)
            {
                spansToSkip.Add(t.Span);
                CollectTypeRefNodes(t.Type, model, argNodes);
            }
        }
        if (service == null && impl == null) { return; }

        _processedInvocations.Add(inv);
        // See HandleCreateMap: a solution-declared look-alike target must keep its call edge.
        AddEdgeTo(sourceId, model.GetSymbolInfo(inv).Symbol);
        foreach (var s in spansToSkip) { AddSkip(ref skip, s); }

        var stmt = NewStatement(Constants.StatementKindDiRegister, inv, sourceId, new[] { inv }, new[] { service, impl }.Concat(argNodes));
        if (service != null) { stmt.ServiceNodes.Add(service); }
        // Nested type arguments inside the suppressed spans must stay alive while the statement does.
        var extraArgs = argNodes.Where(a => a != service && a != impl).Cast<object>().ToList();
        if (extraArgs.Count > 0)
        {
            _graph.AddRule(new object[] { stmt }, extraArgs, "type arguments of the registration");
        }
        var guards = new List<object> { sourceId };
        if (service != null) { guards.Add(service); }
        // An EXTERNAL service type (AddSingleton<IAuthorizationHandler, MinAgeHandler>): the
        // framework resolves it at runtime, so no in-solution reference can exist. The statement
        // lives with its containing member alone — guarding it on the implementation would be
        // circular (the implementation lives only through this statement) and would delete a
        // runtime-live component as a "pre-existing orphan".
        else if (impl != null && !externalService) { guards.Add(impl); } // unresolvable without a service arg: tie to the implementation
        _graph.AddRule(guards, new object[] { stmt }, externalService ? "external service registration" : "DI registration in use iff service in use");

        if (impl != null)
        {
            var payload = new List<object> { impl };
            if (_graph.Decl.TryGetValue(impl, out var d) && d is INamedTypeSymbol it)
            {
                payload.AddRange(it.InstanceConstructors.Select(NodeId).Where(id => id != null));
            }
            _graph.AddRule(new object[] { stmt }, payload, "registration instantiates implementation");
        }
    }

    void HandleAutofac(InvocationExpressionSyntax inv, SeparatedSyntaxList<TypeSyntax>? typeArgs, string sourceId, SemanticModel model, ref List<TextSpan> skip)
    {
        string impl = null;
        var argNodes = new List<string>();
        if (typeArgs is { Count: 1 })
        {
            impl = ResolveTypeNode(typeArgs.Value[0], model);
            AddSkip(ref skip, typeArgs.Value[0].Span);
            CollectTypeRefNodes(typeArgs.Value[0], model, argNodes);
        }
        else
        {
            var typeofs = inv.ArgumentList.Arguments
                .Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList();
            if (typeofs.Count == 1)
            {
                impl = ResolveTypeNode(typeofs[0].Type, model);
                // The WHOLE typeof expression (see HandleMsDi).
                AddSkip(ref skip, typeofs[0].Span);
                CollectTypeRefNodes(typeofs[0].Type, model, argNodes);
            }
        }
        _processedInvocations.Add(inv);
        // See HandleCreateMap: a solution-declared look-alike target must keep its call edge.
        AddEdgeTo(sourceId, model.GetSymbolInfo(inv).Symbol);

        // Follow the fluent chain upward: .As<T>(), .AsSelf(), .AsImplementedInterfaces(), lifetimes.
        var services = new List<string>();
        var textualRefs = new List<string>();
        var claimed = new List<InvocationExpressionSyntax> { inv };
        bool asSelf = false, hasExternalService = false;
        var cur = inv;
        InvocationExpressionSyntax outermost = inv;
        while (cur.Parent is MemberAccessExpressionSyntax ma && ma.Parent is InvocationExpressionSyntax pInv)
        {
            var mname = ma.Name.Identifier.Text;
            if (mname is "As" or "Keyed" or "Named" && ma.Name is not GenericNameSyntax)
            {
                // Non-generic forms: As(typeof(IFoo), ...), Named("x", typeof(IFoo)), Keyed(k, typeof(IFoo)).
                // Every Type argument is a service; one that is not a typeof (a variable) is unknown,
                // so the registration must live as long as its containing member.
                bool anyService = false;
                foreach (var arg in pInv.ArgumentList.Arguments)
                {
                    if (arg.Expression is TypeOfExpressionSyntax tof)
                    {
                        anyService = true;
                        if (ResolveTypeNode(tof.Type, model) is { } s)
                        {
                            services.Add(s);
                            textualRefs.Add(s);
                            AddSkip(ref skip, tof.Span);
                            CollectTypeRefNodes(tof.Type, model, argNodes);
                        }
                        else { hasExternalService = true; }
                    }
                    else if (model.GetTypeInfo(arg.Expression).Type is { Name: "Type" } argType && argType.ContainingNamespace?.ToDisplayString() == "System")
                    {
                        anyService = true;
                        hasExternalService = true;
                    }
                }
                if (!anyService) { hasExternalService = true; }
            }
            else if (mname == "As" && ma.Name is GenericNameSyntax g)
            {
                foreach (var ta in g.TypeArgumentList.Arguments)
                {
                    if (ResolveTypeNode(ta, model) is { } s)
                    {
                        services.Add(s);
                        textualRefs.Add(s);
                        AddSkip(ref skip, ta.Span);
                        CollectTypeRefNodes(ta, model, argNodes);
                    }
                    else { hasExternalService = true; }
                }
            }
            else if (mname == "AsSelf") { asSelf = true; }
            else if (mname == "AsImplementedInterfaces")
            {
                if (impl != null && _graph.Decl.TryGetValue(impl, out var d) && d is INamedTypeSymbol it)
                {
                    foreach (var i in it.AllInterfaces)
                    {
                        // Autofac itself EXCLUDES IDisposable/IAsyncDisposable from the registered
                        // services: counting them as external services would pin nearly every legacy
                        // registration (and its implementation) forever.
                        if (i.ContainingNamespace is { IsGlobalNamespace: false } ins && ins.ToDisplayString() == "System" && i.Name is "IDisposable" or "IAsyncDisposable") { continue; }
                        if (NodeId(i) is { } iId) { services.Add(iId); }
                        // An EXTERNAL interface (IHostedService, IAuthorizationHandler): consumers
                        // resolve through it outside the solution's sight.
                        else { hasExternalService = true; }
                    }
                }
            }
            else if (mname is "Keyed" or "Named" && ma.Name is GenericNameSyntax kg)
            {
                // Keyed<IFoo>("key") / Named<IFoo>("name") register a service exactly as As<IFoo> does.
                foreach (var ta in kg.TypeArgumentList.Arguments)
                {
                    if (ResolveTypeNode(ta, model) is { } s)
                    {
                        services.Add(s);
                        textualRefs.Add(s);
                        AddSkip(ref skip, ta.Span);
                        CollectTypeRefNodes(ta, model, argNodes);
                    }
                    else { hasExternalService = true; }
                }
            }
            else if (Constants.AutofacActivationNames.Contains(mname))
            {
                // AutoActivate(): the container builds the component at start-up, whoever resolves it.
                hasExternalService = true;
            }
            else if (!Constants.AutofacChainContinuers.Contains(mname) && !Constants.AutofacExtraLifetimeNames.Contains(mname))
            {
                // An unknown call may declare services this walk cannot see (an As<IFoo>() can follow
                // it): the registration then lives as long as its containing member. The walk goes on
                // through calls that still return a registration builder, and stops elsewhere.
                hasExternalService = true;
                var curType = model.GetTypeInfo(cur).Type;
                var nextType = model.GetTypeInfo(pInv).Type;
                bool stillBuilder = curType is { TypeKind: not TypeKind.Error } && nextType is { TypeKind: not TypeKind.Error }
                    && curType.OriginalDefinition.Name == nextType.OriginalDefinition.Name;
                if (!stillBuilder) { break; }
            }
            _processedInvocations.Add(pInv);
            claimed.Add(pInv);
            outermost = pInv;
            cur = pInv;
        }

        if (impl == null && services.Count == 0)
        {
            // Unresolvable registration (RegisterType(variable), external-only services): the chain
            // is already claimed and suppressed, so no edge and no statement will track it. Record
            // the CALL, or the empty-container pass could delete the container around it.
            RecordExternalCall(sourceId, model.GetSymbolInfo(inv).Symbol);
            return;
        }

        // No segment: a chain segment of the outermost call would delete only the lifetime call,
        // not the registration, so a partially surviving anchor falls back to a manual warning.
        var stmt = NewStatement(Constants.StatementKindDiRegister, outermost, sourceId, claimed, textualRefs.Append(impl).Concat(argNodes), allowSegment: false);
        stmt.ServiceNodes.AddRange(services.Distinct());
        if (impl != null && (asSelf || services.Count == 0) && !stmt.ServiceNodes.Contains(impl)) { stmt.ServiceNodes.Add(impl); }
        // Nested type arguments inside the suppressed spans must stay alive while the statement does.
        var extraArgs = argNodes.Where(a => a != impl && !services.Contains(a)).Cast<object>().ToList();
        if (extraArgs.Count > 0)
        {
            _graph.AddRule(new object[] { stmt }, extraArgs, "type arguments of the registration");
        }
        // An external service wins over everything: its consumers resolve outside the solution's
        // sight, so the registration stays while the containing member does — even when in-solution
        // services registered alongside it die.
        if (hasExternalService)
        {
            _graph.AddRule(new object[] { sourceId }, new object[] { stmt }, "external service registration");
        }
        if (services.Count > 0)
        {
            foreach (var s in services)
            {
                _graph.AddRule(new object[] { sourceId, s }, new object[] { stmt }, "registration in use iff service in use");
            }
            if (asSelf && impl != null)
            {
                _graph.AddRule(new object[] { sourceId, impl }, new object[] { stmt }, "self registration");
            }
        }
        else if (!hasExternalService && impl != null)
        {
            _graph.AddRule(new object[] { sourceId, impl }, new object[] { stmt }, "self registration");
        }

        if (impl != null)
        {
            var payload = new List<object> { impl };
            if (_graph.Decl.TryGetValue(impl, out var d2) && d2 is INamedTypeSymbol it2)
            {
                payload.AddRange(it2.InstanceConstructors.Select(NodeId).Where(id => id != null));
            }
            _graph.AddRule(new object[] { stmt }, payload, "registration instantiates implementation");
        }
    }
}
