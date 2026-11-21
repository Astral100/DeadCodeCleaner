using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text.RegularExpressions;

namespace DeadCodeCleaner;

public sealed class GraphBuilder
{
    readonly Graph _graph = new();
    readonly AnalyzerConfig _config;
    readonly Action<string> _log;
    readonly List<(INamedTypeSymbol Type, bool IsTest)> _types = new();
    readonly HashSet<InvocationExpressionSyntax> _processedInvocations = new();

    public GraphBuilder(AnalyzerConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestMethod", "TestCase", "TestCaseSource", "SetUp", "TearDown",
        "OneTimeSetUp", "OneTimeTearDown", "TestInitialize", "TestCleanup", "ClassInitialize",
        "ClassCleanup", "AssemblyInitialize", "AssemblyCleanup", "TestFixture",
    };

    static readonly HashSet<string> ProdRootAttributeNames = new(StringComparer.Ordinal)
    {
        "FunctionName", "Function", "Benchmark", "GlobalSetup", "GlobalCleanup",
    };

    // Members kept while their type is in use: pattern-based language features and framework
    // callbacks reference these without a resolvable identifier in syntax.
    static readonly HashSet<string> KeepAliveMemberNames = new(StringComparer.Ordinal)
    {
        "GetEnumerator", "MoveNext", "Current", "GetAwaiter", "GetResult", "IsCompleted",
        "OnCompleted", "UnsafeOnCompleted", "Deconstruct", "Dispose", "DisposeAsync", "Add",
    };

    // Implementing/inheriting these (matched by simple name) binds type-argument[Index] to the
    // implementor: when the bound type is in use, the implementor is in use (MediatR handlers,
    // FluentValidation validators).
    static readonly (string Name, int ArgIndex, bool IsInterface)[] Binders =
    {
        ("IRequestHandler", 0, true), ("INotificationHandler", 0, true),
        ("IStreamRequestHandler", 0, true), ("IRequestExceptionHandler", 0, true),
        ("IRequestExceptionAction", 0, true),
        ("AbstractValidator", 0, false), ("IValidator", 0, true),
    };

    static readonly HashSet<string> MsDiMethodNames = new(StringComparer.Ordinal)
    {
        "AddScoped", "AddTransient", "AddSingleton", "TryAddScoped", "TryAddTransient", "TryAddSingleton",
    };

    static readonly HashSet<string> AutofacChainContinuers = new(StringComparer.Ordinal)
    {
        "SingleInstance", "InstancePerLifetimeScope", "InstancePerDependency", "InstancePerRequest",
        "InstancePerMatchingLifetimeScope", "InstancePerOwned", "ExternallyOwned", "OwnedByLifetimeScope",
        "WithParameter", "WithParameters", "WithProperty", "Named", "Keyed", "PreserveExistingDefaults",
        "IfNotRegistered", "AutoActivate", "PropertiesAutowired", "UsingConstructor", "OnActivating",
        "OnActivated", "OnPreparing", "OnRelease",
    };

    readonly HashSet<string> _solutionAssemblies = new(StringComparer.OrdinalIgnoreCase);

    public async Task<Graph> BuildAsync(Solution solution)
    {
        var compilations = new List<(Compilation Comp, bool IsTest, string ProjectName)>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp) continue;
            if (!seenProjects.Add(project.FilePath ?? project.Name)) continue; // multi-targeting duplicate
            var comp = await project.GetCompilationAsync();
            if (comp == null) continue;
            bool isTest = IsTestProject(project);
            compilations.Add((comp, isTest, project.Name));
            _solutionAssemblies.Add(comp.Assembly.Name);
            CollectProjectInfo(project);
            if (_graph.Projects.TryGetValue(project.Name, out var pinfo))
                pinfo.AssemblyName = comp.Assembly.Name;
            var errors = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            _log($"  {project.Name}: {project.DocumentIds.Count} documents, " +
                 $"{comp.ExternalReferences.Length} references, {errors.Count} compile errors" +
                 (isTest ? " [test]" : ""));
            if (errors.Count > 0)
            {
                _graph.Warnings.Add($"{project.Name}: {errors.Count} compile errors — analysis may miss references " +
                    $"(first: {errors[0].GetMessage()})");
                foreach (var e in errors.Take(3)) _log($"      error: {e.GetMessage()}");
            }
        }

        // Phase 1: collect all nodes first (rule construction needs the full node set).
        foreach (var (comp, isTest, projectName) in compilations) CollectNodes(comp, isTest, projectName);

        // Phase 2: structural rules and roots.
        foreach (var (type, isTest) in _types) AddStructuralRulesAndRoots(type, isTest);
        foreach (var (comp, isTest, _) in compilations)
        {
            var ep = comp.GetEntryPoint(CancellationToken.None);
            if (ep != null && Graph.Id(ep) is { } epId)
            {
                // The synthesized Main of top-level statements is implicit; admit it explicitly.
                _graph.Nodes.Add(epId);
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
                WalkTree(comp, tree, isTest);
                CollectUsings(comp, tree, projectName);
            }
        }
        return _graph;
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
            _graph.UsingRefs.Add(new UsingRef
            {
                Project = projectName,
                File = tree.FilePath,
                Line = lines.StartLinePosition.Line + 1,
                Namespace = u.Name.ToString(),
                Providers = providers,
            });
        }
    }

    void CollectProjectInfo(Project project)
    {
        if (project.FilePath == null || !File.Exists(project.FilePath)) return;
        var info = new ProjectInfo { Name = project.Name, FilePath = project.FilePath };
        try
        {
            var xml = System.Xml.Linq.XDocument.Load(project.FilePath);
            foreach (var el in xml.Descendants())
            {
                var include = el.Attribute("Include")?.Value;
                if (include == null) continue;
                if (el.Name.LocalName == "PackageReference" && !info.PackageReferences.Contains(include))
                    info.PackageReferences.Add(include);
                else if (el.Name.LocalName == "ProjectReference")
                    info.ProjectReferences[Path.GetFileNameWithoutExtension(include)] = include;
            }
            var assetsPath = Path.Combine(Path.GetDirectoryName(project.FilePath)!, "obj", "project.assets.json");
            if (File.Exists(assetsPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(assetsPath));
                if (doc.RootElement.TryGetProperty("targets", out var targets))
                {
                    foreach (var target in targets.EnumerateObject().Take(1))
                    {
                        foreach (var entry in target.Value.EnumerateObject())
                        {
                            var slash = entry.Name.IndexOf('/');
                            if (slash <= 0) continue;
                            var pkg = entry.Name[..slash];
                            if (!entry.Value.TryGetProperty("compile", out var compile)) continue;
                            foreach (var lib in compile.EnumerateObject())
                            {
                                var file = Path.GetFileNameWithoutExtension(lib.Name);
                                if (string.IsNullOrEmpty(file) || file == "_") continue;
                                if (!info.PackageAssemblies.TryGetValue(pkg, out var list))
                                    info.PackageAssemblies[pkg] = list = new List<string>();
                                if (!list.Contains(file)) list.Add(file);
                            }
                        }
                    }
                }
            }
            else if (info.PackageReferences.Count > 0)
            {
                _graph.Warnings.Add($"{project.Name}: no obj/project.assets.json (run restore) — package cleanup skipped for this project");
            }
            if (File.Exists(Path.Combine(Path.GetDirectoryName(project.FilePath)!, "packages.config")))
                _graph.Warnings.Add($"{project.Name}: uses packages.config — package cleanup not supported for it yet");
        }
        catch (Exception e)
        {
            _graph.Warnings.Add($"{project.Name}: could not read project facts ({e.Message})");
        }
        _graph.Projects[project.Name] = info;
    }

    static bool IsTestProject(Project project)
    {
        var name = project.Name;
        if (name.EndsWith("Test") || name.EndsWith("Tests")
            || Regex.IsMatch(name, @"(^|[._])Tests?([._]|$)") || name.Contains("TestCommon"))
            return true;
        // Only DIRECT test-framework dependencies count: transitive flow through a shared
        // project reference must not turn a production project into a test project.
        try
        {
            if (project.FilePath != null && File.Exists(project.FilePath))
            {
                var text = File.ReadAllText(project.FilePath);
                if (Regex.IsMatch(text, @"Include=""(xunit|nunit|NUnit|MSTest|Microsoft\.NET\.Test\.Sdk)", RegexOptions.IgnoreCase))
                    return true;
                if (text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* unreadable project file: treat as production */ }
        return false;
    }

    // ---------------------------------------------------------------- Phase 1

    void CollectNodes(Compilation comp, bool isTest, string projectName)
    {
        foreach (var type in AllSourceTypes(comp.Assembly.GlobalNamespace))
        {
            if (Graph.Id(type) is not { } typeId) continue;
            if (!_graph.Nodes.Add(typeId)) continue;
            _graph.Decl[typeId] = type;
            _graph.ProjectOf[typeId] = projectName;
            if (type.ContainingType != null && Graph.Id(type.ContainingType) is { } outerId)
                _graph.ContainerOf[typeId] = outerId;
            _types.Add((type, isTest));
            if (isTest) _graph.TestNodes.Add(typeId);
            foreach (var m in type.GetMembers())
            {
                if (m.IsImplicitlyDeclared) continue;
                if (m is INamedTypeSymbol) continue; // nested types enumerated separately
                if (m is IMethodSymbol acc && acc.AssociatedSymbol != null) continue; // accessors fold into property/event
                if (m.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event)) continue;
                if (m.DeclaringSyntaxReferences.Length == 0) continue;
                if (Graph.Id(m) is not { } mId) continue;
                _graph.Nodes.Add(mId);
                _graph.Decl[mId] = m;
                _graph.ProjectOf[mId] = projectName;
                _graph.ContainerOf[mId] = typeId;
                if (isTest) _graph.TestNodes.Add(mId);
            }
        }
    }

    static IEnumerable<INamedTypeSymbol> AllSourceTypes(INamespaceSymbol ns)
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

        static IEnumerable<INamedTypeSymbol> SelfAndNested(INamedTypeSymbol t)
        {
            if (t.DeclaringSyntaxReferences.Length == 0) yield break;
            yield return t;
            foreach (var nested in t.GetTypeMembers())
                foreach (var x in SelfAndNested(nested)) yield return x;
        }
    }

    /// <summary>Node id when the symbol is a node of this graph, else null.</summary>
    string? NodeId(ISymbol? symbol)
    {
        if (symbol == null) return null;
        var id = Graph.Id(symbol);
        return id != null && _graph.Nodes.Contains(id) ? id : null;
    }

    // ---------------------------------------------------------------- Phase 2

    void AddStructuralRulesAndRoots(INamedTypeSymbol type, bool isTest)
    {
        var typeId = Graph.Id(type)!;
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
                    || (mm.DeclaredAccessibility == Accessibility.Public && KeepAliveMemberNames.Contains(mm.Name));
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
                     && KeepAliveMemberNames.Contains(kp.Name))
            {
                _graph.AddRule(one, new object[] { mId }, "kept with type");
            }

            // Override dispatch: the override is used whenever the overridden member is used on a
            // usable instance. External overridden member => keep with type.
            ISymbol? overridden = m switch
            {
                IMethodSymbol om when om.IsOverride => om.OverriddenMethod,
                IPropertySymbol op when op.IsOverride => op.OverriddenProperty,
                IEventSymbol oe when oe.IsOverride => oe.OverriddenEvent,
                _ => null,
            };
            if (overridden != null)
            {
                if (NodeId(overridden) is { } oId)
                    _graph.AddRule(new object[] { typeId, oId }, new object[] { mId }, "override dispatch");
                else
                    _graph.AddRule(one, new object[] { mId }, "overrides external member");
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
                    _graph.AddRule(new object[] { typeId, Graph.Id(im)! }, new object[] { implId }, "interface dispatch");
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
                        if (!hasInitializer) _graph.AddEdge(NodeId(ctor)!, baseCtorId);
                    }
                }
            }
        }

        // Binders: MediatR handlers, validators — bound type in use => implementor in use.
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var (bname, argIndex, isInterface) in Binders)
            {
                if (!isInterface || iface.Name != bname || iface.TypeArguments.Length <= argIndex) continue;
                BindTypeArgToImplementor(iface.TypeArguments[argIndex], type, iface, bname);
            }
        }
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            foreach (var (bname, argIndex, isInterface) in Binders)
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
        var consequents = new List<object> { Graph.Id(implementor)! };
        consequents.AddRange(implementor.InstanceConstructors.Select(NodeId).Where(id => id != null)!);
        // The framework dispatches through the binder's members (e.g. Handle): while a handler
        // exists, those members count as used even if no source calls them directly.
        consequents.AddRange(binder.OriginalDefinition.GetMembers().Select(NodeId).Where(id => id != null)!);
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
            rootWholeType = true; reason = "AutoMapper profile";
        }
        else if (BaseChainHasExternalName(type, "Module", "Autofac"))
        {
            rootWholeType = true; reason = "Autofac module";
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
                bool isTestAttr = TestAttributeNames.Contains(shortName);
                bool isProdAttr = ProdRootAttributeNames.Contains(shortName);
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

    static bool BaseChainHasName(INamedTypeSymbol type, params string[] names)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (names.Contains(b.Name)) return true;
        return false;
    }

    static bool BaseChainHasExternalName(INamedTypeSymbol type, string name, string assemblyPrefix)
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

    void WalkTree(Compilation comp, SyntaxTree tree, bool isTest)
    {
        var model = comp.GetSemanticModel(tree);
        var root = tree.GetRoot();

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
        string? first = null;
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

    void WalkMember(SyntaxNode subtree, string sourceId, SemanticModel model)
    {
        var skipSpans = MatchStatements(subtree, sourceId, model);

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

    static bool InSkip(List<TextSpan>? spans, TextSpan span)
    {
        if (spans == null) return false;
        foreach (var s in spans) if (s.Contains(span)) return true;
        return false;
    }

    void AddSymbolInfoEdges(string sourceId, SymbolInfo info)
    {
        if (info.Symbol != null) AddEdgeTo(sourceId, info.Symbol);
        foreach (var c in info.CandidateSymbols) AddEdgeTo(sourceId, c);
    }

    void AddEdgeTo(string sourceId, ISymbol? target)
    {
        if (Normalize(target) is { } targetId) _graph.AddEdge(sourceId, targetId);
        else RecordExternal(sourceId, target);
    }

    void RecordExternal(string sourceId, ISymbol? target)
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

    string? Normalize(ISymbol? target)
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

    List<TextSpan>? MatchStatements(SyntaxNode subtree, string sourceId, SemanticModel model)
    {
        List<TextSpan>? skip = null;
        foreach (var inv in subtree.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (_processedInvocations.Contains(inv)) continue;
            var name = GetMethodName(inv);
            if (name == null) continue;
            var id = name.Identifier.Text;
            var typeArgs = (name as GenericNameSyntax)?.TypeArgumentList.Arguments;

            if (id == "CreateMap" && typeArgs is { Count: 2 })
                HandleCreateMap(inv, typeArgs.Value, sourceId, model, ref skip);
            else if (MsDiMethodNames.Contains(id))
                HandleMsDi(inv, typeArgs, sourceId, model, ref skip);
            else if (id is "RegisterType" or "RegisterGeneric")
                HandleAutofac(inv, typeArgs, sourceId, model, ref skip);
        }
        return skip;
    }

    static SimpleNameSyntax? GetMethodName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name,
        SimpleNameSyntax sn => sn,
        _ => null,
    };

    string? ResolveTypeNode(SyntaxNode typeSyntax, SemanticModel model)
    {
        var sym = model.GetSymbolInfo(typeSyntax).Symbol
                  ?? (typeSyntax is TypeSyntax ts ? model.GetTypeInfo(ts).Type : null);
        return sym is ITypeSymbol t ? NodeId(t) : null;
    }

    StatementNode NewStatement(string kind, InvocationExpressionSyntax inv, string containingMember)
    {
        var anchor = (SyntaxNode?)inv.FirstAncestorOrSelf<StatementSyntax>() ?? inv;
        var lines = anchor.GetLocation().GetLineSpan();
        var snippet = Regex.Replace(anchor.ToString(), @"\s+", " ").Trim();
        if (snippet.Length > 160) snippet = snippet[..157] + "...";
        var stmt = new StatementNode
        {
            Kind = kind,
            FilePath = anchor.SyntaxTree.FilePath,
            StartLine = lines.StartLinePosition.Line + 1,
            EndLine = lines.EndLinePosition.Line + 1,
            Snippet = snippet,
            ContainingMember = containingMember,
        };
        _graph.Statements.Add(stmt);
        return stmt;
    }

    static void AddSkip(ref List<TextSpan>? skip, TextSpan span)
    {
        skip ??= new List<TextSpan>();
        skip.Add(span);
    }

    void HandleCreateMap(InvocationExpressionSyntax inv, SeparatedSyntaxList<TypeSyntax> typeArgs,
        string sourceId, SemanticModel model, ref List<TextSpan>? skip)
    {
        var involved = new List<string>();
        foreach (var ta in typeArgs)
        {
            if (ResolveTypeNode(ta, model) is { } t) involved.Add(t);
        }
        if (involved.Count == 0) return;
        _processedInvocations.Add(inv);
        foreach (var ta in typeArgs) AddSkip(ref skip, ta.Span);

        var stmt = NewStatement("AutoMapper.CreateMap", inv, sourceId);
        var guards = new List<object> { sourceId };
        guards.AddRange(involved);
        _graph.AddRule(guards, new object[] { stmt }, "CreateMap in use iff both types in use");

        if (!_config.StrictMembers)
        {
            var props = involved
                .Select(t => _graph.Decl.TryGetValue(t, out var d) ? d as INamedTypeSymbol : null)
                .Where(t => t != null)
                .SelectMany(t => t!.GetMembers().OfType<IPropertySymbol>())
                .Select(NodeId)
                .Where(pid => pid != null)
                .Cast<object>()
                .ToList();
            if (props.Count > 0)
                _graph.AddRule(new object[] { stmt }, props, "mapped properties used via reflection");
        }
    }

    void HandleMsDi(InvocationExpressionSyntax inv, SeparatedSyntaxList<TypeSyntax>? typeArgs,
        string sourceId, SemanticModel model, ref List<TextSpan>? skip)
    {
        string? service = null, impl = null;
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

        var stmt = NewStatement("DI.Register", inv, sourceId);
        var guards = new List<object> { sourceId };
        if (service != null) guards.Add(service);
        else if (impl != null) guards.Add(impl); // external service type: tie to the implementation
        _graph.AddRule(guards, new object[] { stmt }, "DI registration in use iff service in use");

        if (impl != null)
        {
            var payload = new List<object> { impl };
            if (_graph.Decl.TryGetValue(impl, out var d) && d is INamedTypeSymbol it)
                payload.AddRange(it.InstanceConstructors.Select(NodeId).Where(id => id != null)!);
            _graph.AddRule(new object[] { stmt }, payload, "registration instantiates implementation");
        }
    }

    void HandleAutofac(InvocationExpressionSyntax inv, SeparatedSyntaxList<TypeSyntax>? typeArgs,
        string sourceId, SemanticModel model, ref List<TextSpan>? skip)
    {
        string? impl = null;
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
                    if (ResolveTypeNode(ta, model) is { } s) { services.Add(s); AddSkip(ref skip, ta.Span); }
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
            else if (!AutofacChainContinuers.Contains(mname)) break;
            _processedInvocations.Add(pInv);
            outermost = pInv;
            cur = pInv;
        }

        if (impl == null && services.Count == 0) return;

        var stmt = NewStatement("DI.Register", outermost, sourceId);
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
                payload.AddRange(it2.InstanceConstructors.Select(NodeId).Where(id => id != null)!);
            _graph.AddRule(new object[] { stmt }, payload, "registration instantiates implementation");
        }
    }
}
