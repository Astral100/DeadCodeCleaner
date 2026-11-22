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
    readonly HashSet<string> _solutionAssemblies = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<SyntaxNode, AnchorInfo> _anchors = new();

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
            if (project.Language != LanguageNames.CSharp) continue;
            if (!seenProjects.Add(project.FilePath ?? project.Name))
            {
                // Multi-targeting duplicate: conditional (#if) code of the other frameworks is invisible.
                var w = $"{project.Name}: multi-targets — only the first target framework was analyzed";
                if (!_graph.Warnings.Contains(w)) _graph.Warnings.Add(w);
                continue;
            }
            var comp = await project.GetCompilationAsync();
            if (comp == null) continue;
            bool isTest = IsTestProject(project);
            compilations.Add((comp, isTest, project.Name));
            _solutionAssemblies.Add(comp.Assembly.Name);
            CollectProjectInfo(project);
            if (_graph.Projects.TryGetValue(project.Name, out var pinfo))
                pinfo.AssemblyName = comp.Assembly.Name;
            var errors = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            _log($"  {project.Name}: {project.DocumentIds.Count} documents, {comp.ExternalReferences.Length} references, {errors.Count} compile errors" + (isTest ? " [test]" : ""));
            if (errors.Count > 0)
            {
                _graph.Warnings.Add($"{project.Name}: {errors.Count} compile errors — analysis may miss references (first: {errors[0].GetMessage()})");
                foreach (var e in errors.Take(Constants.ShownCompileErrors)) _log($"      error: {e.GetMessage()}");
            }
        }

        WarnAboutNonCompiledContent();

        // Phase 1: collect all nodes first (rule construction needs the full node set).
        foreach (var (comp, isTest, projectName) in compilations) CollectNodes(comp, isTest, projectName);

        // Phase 2: structural rules and roots.
        foreach (var (type, isTest) in _types) AddStructuralRulesAndRoots(type, isTest);
        foreach (var (comp, isTest, pname) in compilations)
        {
            var ep = comp.GetEntryPoint(CancellationToken.None);
            if (ep != null && Graph.Id(ep) is { } epId)
            {
                // The synthesized Main of top-level statements is implicit; admit it explicitly.
                _graph.Nodes.Add(epId);
                _graph.AddNodeProject(epId, pname);
                _graph.Decl.TryAdd(epId, ep.OriginalDefinition);
                if (Graph.Id(ep.ContainingType) is { } eptId) _graph.ContainerOf.TryAdd(epId, eptId);
                if (isTest) _graph.TestNodes.Add(epId);
                _graph.AddRoot(epId, "entry point", isTest);
                if (Graph.Id(ep.ContainingType) is { } tId && _graph.Nodes.Contains(tId))
                    _graph.AddRoot(tId, "entry point type", isTest);
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
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pname, pinfo2) in _graph.Projects)
        {
            byPath.TryAdd(Path.GetFullPath(pinfo2.FilePath), pname);
            byFileName.TryAdd(Path.GetFileNameWithoutExtension(pinfo2.FilePath), pname);
        }
        foreach (var pinfo2 in _graph.Projects.Values)
        {
            var resolved = new List<(string Name, string Include)>();
            foreach (var (name, include) in pinfo2.ProjectReferences)
            {
                var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pinfo2.FilePath) ?? "", include.Replace('\\', '/')));
                var rname = byPath.TryGetValue(full, out var n1) ? n1 : byFileName.TryGetValue(name, out var n2) ? n2 : name;
                resolved.Add((rname, include));
            }
            pinfo2.ProjectReferences.Clear();
            foreach (var (name, include) in resolved) pinfo2.ProjectReferences[name] = include;
        }

        // Phase 4: whole-anchor safety. Deleting the whole anchor statement is only safe when it is
        // a plain expression statement whose entire call chain is claimed by tracked registrations;
        // anything else (return/var anchors, untracked chained calls) must survive partially.
        foreach (var ai in _anchors.Values)
        {
            bool allowed = ai.InnerStatement is ExpressionStatementSyntax es
                && TryGetChain(es.Expression, out var chain) && chain.All(ai.Claimed.Contains);
            foreach (var s in ai.Statements) s.WholeAnchorAllowed = allowed && s.ManualReason == null;
        }
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
                if (inv.Expression is MemberAccessExpressionSyntax ma) { cur = ma.Expression; continue; }
                return inv.Expression is SimpleNameSyntax;
            }
            if (cur is MemberAccessExpressionSyntax outer) { cur = outer.Expression; continue; }
            return cur is IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax;
        }
    }

    void WarnAboutNonCompiledContent()
    {
        int found = 0;
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pinfo in _graph.Projects.Values)
        {
            var dir = Path.GetDirectoryName(pinfo.FilePath);
            if (dir == null || !scanned.Add(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var sep = Path.DirectorySeparatorChar;
                    if (f.Contains($"{sep}bin{sep}") || f.Contains($"{sep}obj{sep}")) continue;
                    if (Constants.NonCompiledContentExtensions.Contains(Path.GetExtension(f))) found++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Unreadable directory: content scan is best-effort.
            }
        }
        if (found > 0)
            _graph.Warnings.Add($"solution contains {found} non-compiled content file(s) (views / WebForms / config wiring) — references from them are INVISIBLE to this analysis; review the report before deleting");
    }

    void CollectUsings(Compilation comp, SyntaxTree tree, string projectName)
    {
        var model = comp.GetSemanticModel(tree);
        foreach (var u in tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (u.Name == null) continue;
            var sym = model.GetSymbolInfo(u.Name).Symbol;
            var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sym is INamespaceSymbol ns)
            {
                foreach (var part in ns.ConstituentNamespaces)
                    if (part.ContainingAssembly?.Name is { } a) providers.Add(a);
            }
            else if (sym is ITypeSymbol t && t.ContainingAssembly?.Name is { } ta)
            {
                providers.Add(ta);
            }
            var lines = u.GetLocation().GetLineSpan();
            // Char span for deletion: FullSpan minus leading #directives, same as declarations.
            int spanStart = u.FullSpan.Start;
            var leading = u.GetLeadingTrivia();
            for (int ti = leading.Count - 1; ti >= 0; ti--)
            {
                if (leading[ti].IsDirective) { spanStart = leading[ti].FullSpan.End; break; }
            }
            _graph.UsingRefs.Add(new UsingRef
            {
                Project = projectName,
                File = tree.FilePath,
                Line = lines.StartLinePosition.Line + 1,
                Namespace = u.Name.ToString(),
                Providers = providers,
                SpanStart = spanStart,
                SpanLength = u.FullSpan.End - spanStart,
            });
        }
    }

    void CollectProjectInfo(Project project)
    {
        if (project.FilePath == null || !File.Exists(project.FilePath)) return;
        var info = new ProjectInfo { Name = project.Name, FilePath = project.FilePath };
        try
        {
            var xml = XDocument.Load(project.FilePath);
            foreach (var el in xml.Descendants())
            {
                var include = el.Attribute("Include")?.Value;
                if (include == null) continue;
                if (el.Name.LocalName == "PackageReference" && !info.PackageReferences.Contains(include))
                    info.PackageReferences.Add(include);
                else if (el.Name.LocalName == "ProjectReference")
                    info.ProjectReferences[Path.GetFileNameWithoutExtension(include)] = include;
                else if (el.Name.LocalName == "Compile" && !info.CompileItems.Contains(include))
                    info.CompileItems.Add(include);
            }
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
                        if (slash <= 0 || lib == null) continue;
                        var pkg = entryName[..slash];
                        if (lib.Compile != null)
                        {
                            foreach (var libPath in lib.Compile.Keys)
                            {
                                var file = Path.GetFileNameWithoutExtension(libPath);
                                if (string.IsNullOrEmpty(file) || file == "_") continue;
                                if (!info.PackageAssemblies.TryGetValue(pkg, out var list))
                                    info.PackageAssemblies[pkg] = list = new List<string>();
                                if (!list.Contains(file)) list.Add(file);
                            }
                        }
                        if (lib.Dependencies != null)
                        {
                            if (!info.PackageDependencies.TryGetValue(pkg, out var deps))
                                info.PackageDependencies[pkg] = deps = new List<string>();
                            foreach (var dep in lib.Dependencies.Keys)
                                if (!deps.Contains(dep)) deps.Add(dep);
                        }
                    }
                }
            }
            else if (info.PackageReferences.Count > 0)
            {
                _graph.Warnings.Add($"{project.Name}: no obj/project.assets.json (run restore) — package cleanup skipped for this project");
            }
            if (File.Exists(Path.Combine(Path.GetDirectoryName(project.FilePath), "packages.config")))
                _graph.Warnings.Add($"{project.Name}: uses packages.config — package cleanup not supported for it yet");
        }
        catch (Exception e) when (e is XmlException or JsonException or IOException or UnauthorizedAccessException)
        {
            _graph.Warnings.Add($"{project.Name}: could not read project facts ({e.Message})");
        }
        _graph.Projects[project.Name] = info;
    }

    bool IsTestProject(Project project)
    {
        var name = project.Name;
        if (name.EndsWith("Test") || name.EndsWith("Tests")
            || Regex.IsMatch(name, Constants.TestProjectNamePattern) || name.Contains("TestCommon"))
            return true;
        // Only DIRECT test-framework dependencies count: transitive flow through a shared
        // project reference must not turn a production project into a test project.
        try
        {
            if (project.FilePath != null && File.Exists(project.FilePath))
            {
                var text = File.ReadAllText(project.FilePath);
                if (Regex.IsMatch(text, Constants.TestPackagePattern, RegexOptions.IgnoreCase))
                    return true;
                if (text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (IOException) { /* unreadable project file: treat as production */ }
        catch (UnauthorizedAccessException) { /* unreadable project file: treat as production */ }
        return false;
    }

    // ---------------------------------------------------------------- Phase 1

    void CollectNodes(Compilation comp, bool isTest, string projectName)
    {
        foreach (var type in AllSourceTypes(comp.Assembly.GlobalNamespace))
        {
            if (Graph.Id(type) is not { } typeId) continue;
            // A linked file compiles the same type into several projects: the first compilation owns
            // the declaration facts, but every project records membership (for reference cleanup).
            bool firstSeen = _graph.Nodes.Add(typeId);
            _graph.AddNodeProject(typeId, projectName);
            if (firstSeen)
            {
                _graph.Decl[typeId] = type;
                if (type.ContainingType != null && Graph.Id(type.ContainingType) is { } outerId)
                    _graph.ContainerOf[typeId] = outerId;
                _types.Add((type, isTest));
                if (isTest) _graph.TestNodes.Add(typeId);
            }
            bool divergent = false;
            foreach (var m in type.GetMembers())
            {
                if (m.IsImplicitlyDeclared) continue;
                if (m is INamedTypeSymbol) continue; // nested types enumerated separately
                if (m is IMethodSymbol acc && acc.AssociatedSymbol != null) continue; // accessors fold into property/event
                if (m.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event)) continue;
                if (m.DeclaringSyntaxReferences.Length == 0) continue;
                if (Graph.Id(m) is not { } mId) continue;
                _graph.AddNodeProject(mId, projectName);
                if (!_graph.Nodes.Add(mId)) continue;
                _graph.Decl[mId] = m;
                _graph.ContainerOf[mId] = typeId;
                if (isTest) _graph.TestNodes.Add(mId);
                // A member that only this compilation declares: a same-named type with a DIFFERENT
                // body exists in another project — analyze both so no reference goes missing.
                if (!firstSeen) divergent = true;
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
                    foreach (var t in AllSourceTypes(child)) yield return t;
                    break;
                case INamedTypeSymbol t:
                    foreach (var x in SelfAndNested(t)) yield return x;
                    break;
            }
        }

        IEnumerable<INamedTypeSymbol> SelfAndNested(INamedTypeSymbol t)
        {
            if (t.DeclaringSyntaxReferences.Length == 0) yield break;
            yield return t;
            foreach (var nested in t.GetTypeMembers())
                foreach (var x in SelfAndNested(nested)) yield return x;
        }
    }

    /// <summary>Node id when the symbol is a node of this graph, else null.</summary>
    string NodeId(ISymbol symbol)
    {
        if (symbol == null) return null;
        var id = Graph.Id(symbol);
        return id != null && _graph.Nodes.Contains(id) ? id : null;
    }

    // ---------------------------------------------------------------- Phase 2

    void AddStructuralRulesAndRoots(INamedTypeSymbol type, bool isTest)
    {
        var typeId = Graph.Id(type);
        var members = type.GetMembers();
        var one = new object[1] { typeId };

        foreach (var m in members)
        {
            if (NodeId(m) is not { } mId) continue;

            // Members that stay with their type: compiler/framework reaches them without identifiers.
            if (m is IMethodSymbol mm)
            {
                bool keep = mm.MethodKind is MethodKind.StaticConstructor or MethodKind.Destructor
                        or MethodKind.UserDefinedOperator or MethodKind.Conversion
                    || (mm.MethodKind == MethodKind.Constructor && mm.Parameters.Length == 0)
                    || (mm.DeclaredAccessibility == Accessibility.Public && Constants.KeepAliveMemberNames.Contains(mm.Name));
                if (keep) _graph.AddRule(one, new object[] { mId }, "kept with type");
            }
            else if (!_config.StrictMembers)
            {
                // Serializers / model binding / data binding reach these via reflection.
                bool keep = (m is IPropertySymbol p && p.DeclaredAccessibility == Accessibility.Public)
                    || (m is IFieldSymbol && type.TypeKind == TypeKind.Enum);
                if (keep) _graph.AddRule(one, new object[] { mId }, "public surface kept with type");
            }
            else if (m is IPropertySymbol kp && kp.DeclaredAccessibility == Accessibility.Public
                     && Constants.KeepAliveMemberNames.Contains(kp.Name))
            {
                _graph.AddRule(one, new object[] { mId }, "kept with type");
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
                if (NodeId(ei) is { } eiId) _graph.AddEdge(mId, eiId);
            }
        }

        // Interface dispatch.
        foreach (var iface in type.AllInterfaces)
        {
            bool external = NodeId(iface) == null;
            foreach (var im in iface.GetMembers())
            {
                if (im.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Event)) continue;
                if (im is IMethodSymbol iacc && iacc.AssociatedSymbol != null) continue;
                var impl = type.FindImplementationForInterfaceMember(im);
                if (impl == null) continue;
                if (impl is IMethodSymbol iaccm && iaccm.AssociatedSymbol != null)
                    impl = iaccm.AssociatedSymbol;
                if (NodeId(impl) is not { } implId) continue;
                if (external)
                    _graph.AddRule(one, new object[] { implId }, "implements external interface");
                else
                    _graph.AddRule(new object[] { typeId, Graph.Id(im) }, new object[] { implId }, "interface dispatch");
            }
        }

        // Implicit base constructor calls (": base()" omitted, or no explicit constructors at all).
        if (type.BaseType is { } baseType && NodeId(baseType) != null)
        {
            var baseCtorId = baseType.OriginalDefinition.InstanceConstructors
                .Where(c => c.Parameters.Length == 0)
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
                        if (!hasInitializer) _graph.AddEdge(NodeId(ctor), baseCtorId);
                    }
                }
            }
        }

        // Binders: MediatR handlers, validators — bound type in use => implementor in use.
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var (bname, argIndex, isInterface) in Constants.Binders)
            {
                if (!isInterface || iface.Name != bname || iface.TypeArguments.Length <= argIndex) continue;
                BindTypeArgToImplementor(iface.TypeArguments[argIndex], type, iface, bname);
            }
        }
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            foreach (var (bname, argIndex, isInterface) in Constants.Binders)
            {
                if (isInterface || b.Name != bname || b.TypeArguments.Length <= argIndex) continue;
                BindTypeArgToImplementor(b.TypeArguments[argIndex], type, b, bname);
            }
        }

        DetectRoots(type, typeId, members, isTest);
    }

    void BindTypeArgToImplementor(ITypeSymbol arg, INamedTypeSymbol implementor, INamedTypeSymbol binder, string binderName)
    {
        if (NodeId(arg) is not { } argId) return;
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
        string reason = "";

        if (type.Name.EndsWith("Controller") || BaseChainHasName(type, "Controller", "ControllerBase", "ApiController"))
        {
            rootWholeType = true; reason = "controller";
        }
        else if (type.AllInterfaces.Any(i => i.Name == "IHostedService") || BaseChainHasName(type, "BackgroundService"))
        {
            rootWholeType = true; reason = "hosted service";
        }
        else if (type.Name is "Startup" or "Program")
        {
            rootWholeType = true; reason = "startup/program";
        }
        else if (BaseChainHasName(type, "Profile") || BaseChainHasExternalName(type, "Profile", "AutoMapper"))
        {
            rootWholeType = true; reason = Constants.ReasonAutoMapperProfile;
        }
        else if (BaseChainHasExternalName(type, "Module", "Autofac"))
        {
            rootWholeType = true; reason = Constants.ReasonAutofacModule;
        }

        if (rootWholeType)
        {
            _graph.AddRoot(typeId, reason, isTest);
            foreach (var m in members)
            {
                if (NodeId(m) is not { } mId) continue;
                bool isCtor = m is IMethodSymbol c && c.MethodKind == MethodKind.Constructor;
                if (isCtor || m.DeclaredAccessibility == Accessibility.Public)
                    _graph.AddRoot(mId, reason, isTest);
            }
        }

        // Attribute-based roots (member level).
        foreach (var m in members)
        {
            if (NodeId(m) is not { } mId) continue;
            foreach (var attr in m.GetAttributes())
            {
                var an = attr.AttributeClass?.Name;
                if (an == null) continue;
                var shortName = an.EndsWith("Attribute") ? an[..^9] : an;
                bool isTestAttr = Constants.TestAttributeNames.Contains(shortName);
                bool isProdAttr = Constants.ProdRootAttributeNames.Contains(shortName);
                if (!isTestAttr && !isProdAttr) continue;
                // Test-attributed members root the test world; others root production.
                bool asTest = isTestAttr || isTest;
                _graph.AddRoot(mId, isTestAttr ? "test method" : $"[{shortName}]", asTest);
                _graph.AddRoot(typeId, isTestAttr ? "test class" : $"[{shortName}] class", asTest);
                foreach (var ctor in type.InstanceConstructors)
                    if (NodeId(ctor) is { } cId)
                        _graph.AddRoot(cId, "test/attr class ctor", asTest);
                break;
            }
        }
    }

    bool BaseChainHasName(INamedTypeSymbol type, params string[] names)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (names.Contains(b.Name)) return true;
        return false;
    }

    bool BaseChainHasExternalName(INamedTypeSymbol type, string name, string assemblyPrefix)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (b.Name == name && b.ContainingAssembly?.Name.StartsWith(assemblyPrefix) == true) return true;
        return false;
    }

    void ApplyConfigRoots()
    {
        var patterns = _config.Keep.Concat(_config.Roots).Distinct().ToList();
        if (patterns.Count == 0) return;
        var regexes = patterns
            .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*") + "$", RegexOptions.Compiled))
            .ToList();
        foreach (var (id, symbol) in _graph.Decl)
        {
            var fqn = SymbolNames.Fqn(symbol);
            if (regexes.Any(r => r.IsMatch(fqn)))
                _graph.AddRoot(id, "config keep/root", _graph.TestNodes.Contains(id));
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
            _graph.FilesWithTopLevelContent.Add(tree.FilePath);

        // Files with inactive #if regions hold code the compiler cannot see: never delete them whole.
        if (root.ContainsDirectives && root.DescendantTrivia(descendIntoTrivia: true).Any(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
            _graph.FilesWithInactiveCode.Add(tree.FilePath);

        // Assembly/module-level attributes reference code and packages with no containing member:
        // root everything they resolve so none of it is ever orphaned.
        if (root is CompilationUnitSyntax cu2)
        {
            foreach (var al in cu2.AttributeLists)
            {
                foreach (var node in al.DescendantNodesAndSelf())
                {
                    ISymbol sym = node switch
                    {
                        AttributeSyntax at => model.GetSymbolInfo(at).Symbol,
                        IdentifierNameSyntax or GenericNameSyntax => model.GetSymbolInfo(node).Symbol,
                        _ => null,
                    };
                    if (sym != null) RootCompilationUnitAttributeSymbol(sym, projectName, isTest);
                }
            }
        }

        var globals = root.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count > 0)
        {
            var ep = comp.GetEntryPoint(CancellationToken.None);
            if (ep != null && Graph.Id(ep) is { } epId)
                foreach (var g in globals) WalkMember(g, epId, model);
        }

        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSym) continue;
            if (NodeId(typeSym) is not { } typeId) continue;

            foreach (var al in typeDecl.AttributeLists) WalkMember(al, typeId, model);
            if (typeDecl.BaseList != null) WalkMember(typeDecl.BaseList, typeId, model);
            if (typeDecl is TypeDeclarationSyntax tds)
            {
                foreach (var cc in tds.ConstraintClauses) WalkMember(cc, typeId, model);
                if (tds.ParameterList != null) WalkMember(tds.ParameterList, typeId, model); // primary ctor
            }

            if (typeDecl is EnumDeclarationSyntax en)
            {
                foreach (var em in en.Members)
                {
                    if (em.EqualsValue == null) continue;
                    if (NodeId(model.GetDeclaredSymbol(em)) is { } fId) WalkMember(em.EqualsValue, fId, model);
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
                    sym = acc.AssociatedSymbol;
                if (NodeId(sym) is not { } memberId) continue;
                WalkMember(member, memberId, model);
            }
        }

        foreach (var del in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            if (NodeId(model.GetDeclaredSymbol(del)) is { } delId)
                WalkMember(del, delId, model);
        }
    }

    void WalkFieldLike(VariableDeclarationSyntax decl, SyntaxList<AttributeListSyntax> attrs, SemanticModel model)
    {
        string first = null;
        foreach (var v in decl.Variables)
        {
            if (NodeId(model.GetDeclaredSymbol(v)) is not { } fId) continue;
            first ??= fId;
            if (v.Initializer != null) WalkMember(v.Initializer, fId, model);
            if (v.ArgumentList != null) WalkMember(v.ArgumentList, fId, model);
        }
        if (first == null) return;
        WalkMember(decl.Type, first, model);
        foreach (var al in attrs) WalkMember(al, first, model);
    }

    void RootCompilationUnitAttributeSymbol(ISymbol sym, string projectName, bool isTest)
    {
        if (sym is IAliasSymbol alias) sym = alias.Target;
        sym = sym.OriginalDefinition;
        if (sym is IMethodSymbol acc && acc.AssociatedSymbol != null) sym = acc.AssociatedSymbol.OriginalDefinition;
        if (NodeId(sym) is { } id)
        {
            _graph.AddRoot(id, "assembly attribute", isTest);
            return;
        }
        if (NodeId(sym.ContainingType) is { } tid) _graph.AddRoot(tid, "assembly attribute", isTest);
        var asm = sym.ContainingAssembly?.Name;
        if (asm != null && !_solutionAssemblies.Contains(asm)) _graph.AddProjectExternalRef(projectName, asm);
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
                case BaseObjectCreationExpressionSyntax oc:
                    AddSymbolInfoEdges(sourceId, model.GetSymbolInfo(oc));
                    break;
                case ConstructorInitializerSyntax ci:
                    AddSymbolInfoEdges(sourceId, model.GetSymbolInfo(ci));
                    break;
                case ElementAccessExpressionSyntax ea:
                    AddSymbolInfoEdges(sourceId, model.GetSymbolInfo(ea));
                    break;
                case ForEachStatementSyntax fe:
                {
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
                    AddEdgeTo(sourceId, info.GetResultMethod);
                    break;
                }
            }
        }
    }

    bool InSkip(List<TextSpan> spans, TextSpan span)
    {
        if (spans == null) return false;
        foreach (var s in spans) if (s.Contains(span)) return true;
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
        foreach (var node in subtree.DescendantNodes())
        {
            bool candidate = node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax
                or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax or AttributeSyntax;
            if (!candidate) continue;
            var op = model.GetOperation(node);
            if (op == null || op.Parent != null) continue;
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
            if (s == null) return;
            if (suppressed) RecordExternal(sourceId, s);
            else AddEdgeTo(sourceId, s);
        }
        switch (op)
        {
            case IInvocationOperation iv: Emit(iv.TargetMethod); break;
            case IObjectCreationOperation oc: Emit(oc.Constructor); break;
            case IMemberReferenceOperation mr: Emit(mr.Member); break;
            case IUnaryOperation u: Emit(u.OperatorMethod); break;
            case IBinaryOperation b: Emit(b.OperatorMethod); break;
            case ICompoundAssignmentOperation ca: Emit(ca.OperatorMethod); break;
            case IIncrementOrDecrementOperation incr: Emit(incr.OperatorMethod); break;
            case IConversionOperation cv: Emit(cv.OperatorMethod); break;
            case ITypeOfOperation to: Emit(to.TypeOperand); break;
            case IIsTypeOperation it: Emit(it.TypeOperand); break;
        }
        if (op.Type is INamedTypeSymbol named) Emit(named);
        foreach (var child in op.ChildOperations) HarvestOperation(child, sourceId, skipSpans);
    }

    void AddSymbolInfoEdges(string sourceId, SymbolInfo info)
    {
        if (info.Symbol != null) AddEdgeTo(sourceId, info.Symbol);
        foreach (var c in info.CandidateSymbols) AddEdgeTo(sourceId, c);
    }

    void AddEdgeTo(string sourceId, ISymbol target)
    {
        if (Normalize(target) is { } targetId) _graph.AddEdge(sourceId, targetId);
        else RecordExternal(sourceId, target);
    }

    void RecordExternal(string sourceId, ISymbol target)
    {
        if (target == null) return;
        if (target is IAliasSymbol alias) target = alias.Target;
        target = target.OriginalDefinition;
        if (target.Kind is not (SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property
            or SymbolKind.Field or SymbolKind.Event)) return;
        var asm = target.ContainingAssembly?.Name;
        if (asm == null || _solutionAssemblies.Contains(asm)) return;
        _graph.AddExternalRef(sourceId, asm);
    }

    string Normalize(ISymbol target)
    {
        if (target == null) return null;
        if (target is IAliasSymbol alias) target = alias.Target;
        if (target is IMethodSymbol red && red.ReducedFrom != null) target = red.ReducedFrom;
        target = target.OriginalDefinition;
        if (target is IMethodSymbol acc && acc.AssociatedSymbol != null)
            target = acc.AssociatedSymbol.OriginalDefinition;
        if (target.Kind is not (SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property
            or SymbolKind.Field or SymbolKind.Event)) return null;
        if (NodeId(target) is { } id) return id;
        return NodeId(target.ContainingType);
    }

    // --------------------------------------------- co-dependent statements

    List<TextSpan> MatchStatements(SyntaxNode subtree, string sourceId, SemanticModel model)
    {
        List<TextSpan> skip = null;
        foreach (var inv in subtree.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (_processedInvocations.Contains(inv)) continue;
            var name = GetMethodName(inv);
            if (name == null) continue;
            var id = name.Identifier.Text;
            var typeArgs = (name as GenericNameSyntax)?.TypeArgumentList.Arguments;

            if (id == Constants.CreateMapMethodName && typeArgs is { Count: 2 })
                HandleCreateMap(inv, typeArgs.Value, sourceId, model, ref skip);
            else if (Constants.MsDiMethodNames.Contains(id))
                HandleMsDi(inv, typeArgs, sourceId, model, ref skip);
            else if (Constants.AutofacRegisterMethodNames.Contains(id))
                HandleAutofac(inv, typeArgs, sourceId, model, ref skip);
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

    StatementNode NewStatement(string kind, InvocationExpressionSyntax inv, string containingMember, IEnumerable<InvocationExpressionSyntax> claimed, IEnumerable<string> involved, bool allowSegment = true)
    {
        var innerStatement = inv.FirstAncestorOrSelf<StatementSyntax>();
        SyntaxNode anchor = innerStatement ?? (SyntaxNode)inv;
        string manualReason = null;
        // A registration inside a lambda (factory delegates, MapperConfiguration callbacks) cannot be
        // deleted by span arithmetic without breaking the surrounding expression.
        for (var a = inv.Parent; a != null && a != innerStatement; a = a.Parent)
        {
            if (a is AnonymousFunctionExpressionSyntax) { manualReason = "the registration sits inside a lambda"; break; }
        }
        // A braceless embedded statement cannot be deleted alone: expand to the construct that
        // owns it (deleting "if (x) Register();" whole), or flag for manual action when an else
        // branch makes whole-construct deletion wrong.
        while (innerStatement != null && manualReason == null)
        {
            var parent = anchor.Parent;
            if (parent is IfStatementSyntax ifs && ifs.Statement == anchor)
            {
                if (ifs.Else != null) { manualReason = "the registration sits in an if/else branch"; break; }
                anchor = ifs;
            }
            else if (parent is ElseClauseSyntax ec && ec.Statement == anchor) { anchor = ec; break; }
            else if (parent is WhileStatementSyntax w && w.Statement == anchor) anchor = w;
            else if (parent is ForStatementSyntax f && f.Statement == anchor) anchor = f;
            else if (parent is ForEachStatementSyntax fe && fe.Statement == anchor) anchor = fe;
            else if (parent is DoStatementSyntax d && d.Statement == anchor) anchor = d;
            else if (parent is UsingStatementSyntax us && us.Statement == anchor) anchor = us;
            else if (parent is LockStatementSyntax lk && lk.Statement == anchor) anchor = lk;
            else break;
        }
        // Exclude leading #directives from the deletable span so their counterparts stay paired.
        int anchorStart = anchor.FullSpan.Start;
        var leading = anchor.GetLeadingTrivia();
        for (int ti = leading.Count - 1; ti >= 0; ti--)
        {
            if (leading[ti].IsDirective) { anchorStart = leading[ti].FullSpan.End; break; }
        }
        int segmentStart = -1, segmentLength = 0;
        if (allowSegment && inv.Expression is MemberAccessExpressionSyntax chain)
        {
            segmentStart = chain.Expression.Span.End;
            segmentLength = inv.Span.End - segmentStart;
        }
        var lines = anchor.GetLocation().GetLineSpan();
        var snippet = Regex.Replace(anchor.ToString(), @"\s+", " ").Trim();
        if (snippet.Length > Constants.SnippetMaxLength) snippet = snippet[..(Constants.SnippetMaxLength - 3)] + "...";
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
            ManualReason = manualReason,
        };
        foreach (var nid in involved)
        {
            if (nid != null && !stmt.InvolvedNodes.Contains(nid)) stmt.InvolvedNodes.Add(nid);
        }
        _graph.Statements.Add(stmt);
        if (!_anchors.TryGetValue(anchor, out var ai)) _anchors[anchor] = ai = new AnchorInfo { InnerStatement = innerStatement };
        ai.Statements.Add(stmt);
        foreach (var ci in claimed) ai.Claimed.Add(ci);
        return stmt;
    }

    void AddSkip(ref List<TextSpan> skip, TextSpan span)
    {
        skip ??= new List<TextSpan>();
        skip.Add(span);
    }

    void HandleCreateMap(InvocationExpressionSyntax inv, SeparatedSyntaxList<TypeSyntax> typeArgs, string sourceId, SemanticModel model, ref List<TextSpan> skip)
    {
        var involved = new List<string>();
        foreach (var ta in typeArgs)
        {
            if (ResolveTypeNode(ta, model) is { } t) involved.Add(t);
        }
        if (involved.Count == 0) return;
        _processedInvocations.Add(inv);
        foreach (var ta in typeArgs) AddSkip(ref skip, ta.Span);

        // The whole fluent chain hanging off CreateMap (ForMember, ReverseMap, ...) is map
        // configuration: it lives and dies with the mapping, so the statement claims it — including
        // every reference inside its lambdas. Those must NOT become edges from the containing member
        // (a rooted profile would keep the mapped members alive forever); they belong to the
        // statement, which keeps them while it lives and releases them when it dies.
        var claimed = new List<InvocationExpressionSyntax> { inv };
        var chainRefs = new List<string>();
        var chainTop = (SyntaxNode)inv;
        while (chainTop.Parent is MemberAccessExpressionSyntax cma && cma.Parent is InvocationExpressionSyntax cInv)
        {
            claimed.Add(cInv);
            _processedInvocations.Add(cInv);
            AddSkip(ref skip, TextSpan.FromBounds(cma.Name.SpanStart, cInv.Span.End));
            foreach (var idn in cInv.ArgumentList.DescendantNodes())
            {
                if (idn is not (IdentifierNameSyntax or GenericNameSyntax)) continue;
                if (Normalize(model.GetSymbolInfo(idn).Symbol) is { } nid && !chainRefs.Contains(nid)) chainRefs.Add(nid);
            }
            chainTop = cInv;
        }
        var stmt = NewStatement("AutoMapper.CreateMap", inv, sourceId, claimed, involved.Concat(chainRefs));
        var guards = new List<object> { sourceId };
        guards.AddRange(involved);
        _graph.AddRule(guards, new object[] { stmt }, "CreateMap in use iff both types in use");
        if (chainRefs.Count > 0)
            _graph.AddRule(new object[] { stmt }, chainRefs.Cast<object>().ToList(), "map configuration references");

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
                _graph.AddRule(new object[] { stmt }, props, "mapped properties used via reflection");
        }
    }

    void HandleMsDi(InvocationExpressionSyntax inv, SeparatedSyntaxList<TypeSyntax>? typeArgs, string sourceId, SemanticModel model, ref List<TextSpan> skip)
    {
        string service = null, impl = null;
        var spansToSkip = new List<TextSpan>();
        if (typeArgs is { Count: 2 })
        {
            service = ResolveTypeNode(typeArgs.Value[0], model);
            impl = ResolveTypeNode(typeArgs.Value[1], model);
            spansToSkip.Add(typeArgs.Value[0].Span);
            spansToSkip.Add(typeArgs.Value[1].Span);
        }
        else if (typeArgs is { Count: 1 })
        {
            service = impl = ResolveTypeNode(typeArgs.Value[0], model);
            spansToSkip.Add(typeArgs.Value[0].Span);
        }
        else if (typeArgs == null)
        {
            var typeofs = inv.ArgumentList.Arguments
                .Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList();
            if (typeofs.Count == 0) return;
            service = ResolveTypeNode(typeofs[0].Type, model);
            impl = typeofs.Count > 1 ? ResolveTypeNode(typeofs[1].Type, model) : service;
            foreach (var t in typeofs) spansToSkip.Add(t.Type.Span);
        }
        if (service == null && impl == null) return;

        _processedInvocations.Add(inv);
        foreach (var s in spansToSkip) AddSkip(ref skip, s);

        var stmt = NewStatement("DI.Register", inv, sourceId, new[] { inv }, new[] { service, impl });
        var guards = new List<object> { sourceId };
        if (service != null) guards.Add(service);
        else if (impl != null) guards.Add(impl); // external service type: tie to the implementation
        _graph.AddRule(guards, new object[] { stmt }, "DI registration in use iff service in use");

        if (impl != null)
        {
            var payload = new List<object> { impl };
            if (_graph.Decl.TryGetValue(impl, out var d) && d is INamedTypeSymbol it)
                payload.AddRange(it.InstanceConstructors.Select(NodeId).Where(id => id != null));
            _graph.AddRule(new object[] { stmt }, payload, "registration instantiates implementation");
        }
    }

    void HandleAutofac(InvocationExpressionSyntax inv, SeparatedSyntaxList<TypeSyntax>? typeArgs, string sourceId, SemanticModel model, ref List<TextSpan> skip)
    {
        string impl = null;
        if (typeArgs is { Count: 1 })
        {
            impl = ResolveTypeNode(typeArgs.Value[0], model);
            AddSkip(ref skip, typeArgs.Value[0].Span);
        }
        else
        {
            var typeofs = inv.ArgumentList.Arguments
                .Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList();
            if (typeofs.Count == 1)
            {
                impl = ResolveTypeNode(typeofs[0].Type, model);
                AddSkip(ref skip, typeofs[0].Type.Span);
            }
        }
        _processedInvocations.Add(inv);

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
            if (mname == "As" && ma.Name is GenericNameSyntax g)
            {
                foreach (var ta in g.TypeArgumentList.Arguments)
                {
                    if (ResolveTypeNode(ta, model) is { } s) { services.Add(s); textualRefs.Add(s); AddSkip(ref skip, ta.Span); }
                    else hasExternalService = true;
                }
            }
            else if (mname == "AsSelf") asSelf = true;
            else if (mname == "AsImplementedInterfaces")
            {
                if (impl != null && _graph.Decl.TryGetValue(impl, out var d) && d is INamedTypeSymbol it)
                {
                    foreach (var i in it.AllInterfaces)
                        if (NodeId(i) is { } iId) services.Add(iId);
                }
            }
            else if (!Constants.AutofacChainContinuers.Contains(mname)) break;
            _processedInvocations.Add(pInv);
            claimed.Add(pInv);
            outermost = pInv;
            cur = pInv;
        }

        if (impl == null && services.Count == 0) return;

        // No segment: a chain segment of the outermost call would delete only the lifetime call,
        // not the registration, so a partially surviving anchor falls back to a manual warning.
        var stmt = NewStatement("DI.Register", outermost, sourceId, claimed, textualRefs.Append(impl), allowSegment: false);
        if (services.Count > 0)
        {
            foreach (var s in services)
                _graph.AddRule(new object[] { sourceId, s }, new object[] { stmt }, "registration in use iff service in use");
            if (asSelf && impl != null)
                _graph.AddRule(new object[] { sourceId, impl }, new object[] { stmt }, "self registration");
        }
        else if (hasExternalService)
        {
            _graph.AddRule(new object[] { sourceId }, new object[] { stmt }, "external service registration");
        }
        else if (impl != null)
        {
            _graph.AddRule(new object[] { sourceId, impl }, new object[] { stmt }, "self registration");
        }

        if (impl != null)
        {
            var payload = new List<object> { impl };
            if (_graph.Decl.TryGetValue(impl, out var d2) && d2 is INamedTypeSymbol it2)
                payload.AddRange(it2.InstanceConstructors.Select(NodeId).Where(id => id != null));
            _graph.AddRule(new object[] { stmt }, payload, "registration instantiates implementation");
        }
    }
}
