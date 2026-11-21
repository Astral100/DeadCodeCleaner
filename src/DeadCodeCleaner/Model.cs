using Microsoft.CodeAnalysis;

namespace DeadCodeCleaner;

/// <summary>A pseudo-node representing a co-dependent statement (DI registration, CreateMap, ...).
/// In use iff all guard antecedents are in use; reported for deletion when it dies in the removal run.</summary>
public sealed class StatementNode
{
    public required string Kind { get; init; }
    public required string FilePath { get; init; }
    public required int StartLine { get; init; }   // 1-based
    public required int EndLine { get; init; }     // 1-based
    public required string Snippet { get; init; }
    /// <summary>Node id of the member containing the statement.</summary>
    public required string ContainingMember { get; init; }
    /// <summary>Char span of the whole anchor statement (expanded over braceless if/loop parents),
    /// with leading #directives excluded so their counterparts are never orphaned.</summary>
    public required int AnchorStart { get; init; }
    public required int AnchorLength { get; init; }
    /// <summary>Char span of just this call's chain segment (".AddScoped&lt;...&gt;(...)"), or -1 when
    /// the call has no member-access receiver. Used when siblings on the same statement survive.</summary>
    public required int SegmentStart { get; init; }
    public required int SegmentLength { get; init; }
    /// <summary>True when automatic deletion is unsafe (the statement is an if-branch with an else).</summary>
    public required bool ManualOnly { get; init; }
}

/// <summary>Conjunctive implication: when ALL antecedents are in use, all consequents become in use.
/// Antecedents/consequents are node ids (string) or StatementNode.</summary>
public sealed class Rule
{
    public required object[] Antecedents { get; init; }
    public required object[] Consequents { get; init; }
    public string Note { get; init; }
}

/// <summary>Equality for graph nodes: ids by value, statements by reference.</summary>
public sealed class NodeComparer : IEqualityComparer<object>
{
    public static readonly NodeComparer Instance = new();

    bool IEqualityComparer<object>.Equals(object x, object y)
    {
        return x is string a && y is string b ? a == b : ReferenceEquals(x, y);
    }

    int IEqualityComparer<object>.GetHashCode(object o)
    {
        return o is string s ? s.GetHashCode() : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }
}

/// <summary>Dependency graph. Nodes are documentation-comment ids ("T:Ns.Type", "M:Ns.Type.M(...)"),
/// which are identical for a symbol seen from source and through a metadata (DLL) reference —
/// this keeps cross-project references intact even when a project loads via its compiled output.</summary>
public sealed class Graph
{
    public HashSet<string> Nodes { get; } = new(StringComparer.Ordinal);
    /// <summary>Node id -> the source declaration symbol (for names, spans, kinds).</summary>
    public Dictionary<string, ISymbol> Decl { get; } = new(StringComparer.Ordinal);
    /// <summary>Member/nested-type id -> containing type id.</summary>
    public Dictionary<string, string> ContainerOf { get; } = new(StringComparer.Ordinal);
    /// <summary>Nodes declared in test projects.</summary>
    public HashSet<string> TestNodes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Edges { get; } = new(StringComparer.Ordinal);
    public List<Rule> Rules { get; } = new();
    public List<StatementNode> Statements { get; } = new();
    /// <summary>Production entry points. Test code is never a production root.</summary>
    public HashSet<string> Roots { get; } = new(StringComparer.Ordinal);
    /// <summary>Entry points of the test world (test methods, fixtures, setup).</summary>
    public HashSet<string> TestRoots { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> RootReasons { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = new();
    /// <summary>Node id -> external (out-of-solution) assembly names it references.</summary>
    public Dictionary<string, HashSet<string>> ExternalRefs { get; } = new(StringComparer.Ordinal);
    /// <summary>Node id -> name of the project that declares it.</summary>
    public Dictionary<string, string> ProjectOf { get; } = new(StringComparer.Ordinal);
    /// <summary>Project name -> csproj facts.</summary>
    public Dictionary<string, ProjectInfo> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every using directive in the solution, with its provider assemblies.</summary>
    public List<UsingRef> UsingRefs { get; } = new();
    /// <summary>Files carrying non-symbol top-level content (global usings, assembly attributes,
    /// extern aliases) that must never be deleted whole.</summary>
    public HashSet<string> FilesWithTopLevelContent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int EdgeCount { get; private set; }

    public void AddExternalRef(string nodeId, string assemblyName)
    {
        if (!ExternalRefs.TryGetValue(nodeId, out var set))
            ExternalRefs[nodeId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(assemblyName);
    }

    /// <summary>Node id for a symbol, or null when the symbol kind has no stable id.</summary>
    public static string Id(ISymbol symbol)
    {
        return symbol?.OriginalDefinition.GetDocumentationCommentId();
    }

    public void AddEdge(string from, string to)
    {
        if (from == to) return;
        if (!Edges.TryGetValue(from, out var set))
            Edges[from] = set = new HashSet<string>(StringComparer.Ordinal);
        if (set.Add(to)) EdgeCount++;
    }

    public void AddRoot(string id, string reason, bool isTest)
    {
        var target = isTest ? TestRoots : Roots;
        if (target.Add(id) && !RootReasons.ContainsKey(id)) RootReasons[id] = reason;
    }

    public void AddRule(IEnumerable<object> antecedents, IEnumerable<object> consequents, string note = null)
    {
        var ants = antecedents.Distinct(NodeComparer.Instance).ToArray();
        var cons = consequents.ToArray();
        if (ants.Length == 0 || cons.Length == 0) return;
        Rules.Add(new Rule { Antecedents = ants, Consequents = cons, Note = note });
    }
}

/// <summary>A using directive and the assemblies able to satisfy it; removable when all of them go.</summary>
public sealed class UsingRef
{
    public required string Project { get; init; }
    public required string File { get; init; }
    public required int Line { get; init; }        // 1-based
    public required string Namespace { get; init; }
    public required HashSet<string> Providers { get; init; }
}

/// <summary>Per-project facts needed for package/reference cleanup.</summary>
public sealed class ProjectInfo
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public string AssemblyName { get; set; } = "";
    public List<string> PackageReferences { get; } = new();
    /// <summary>Referenced project name -> Include path as written in the csproj.</summary>
    public Dictionary<string, string> ProjectReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Package name -> compile-time assembly names it contributes (from project.assets.json).</summary>
    public Dictionary<string, List<string>> PackageAssemblies { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Package name -> names of the packages it depends on (from project.assets.json).</summary>
    public Dictionary<string, List<string>> PackageDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Typed shape of obj/project.assets.json — only what package cleanup needs.</summary>
public sealed class ProjectAssetsFile
{
    /// <summary>Target framework -> ("Package/Version" -> library facts).</summary>
    public Dictionary<string, Dictionary<string, ProjectAssetsLibrary>> Targets { get; set; } = new();
}

public sealed class ProjectAssetsLibrary
{
    /// <summary>Compile-time assembly path -> item facts (only the keys are used).</summary>
    public Dictionary<string, ProjectAssetsCompileItem> Compile { get; set; }
    /// <summary>Dependent package name -> version range (only the keys are used).</summary>
    public Dictionary<string, string> Dependencies { get; set; }
}

public sealed class ProjectAssetsCompileItem
{
}

public sealed class AnalyzerConfig
{
    /// <summary>FQN patterns ('*' wildcard) treated as entry points in both runs — never orphaned.</summary>
    public List<string> Keep { get; set; } = new();
    /// <summary>Synonym for Keep (extra entry points).</summary>
    public List<string> Roots { get; set; } = new();
    /// <summary>When true, public properties / enum members of in-use types are NOT automatically
    /// kept (riskier: serializers and data binding use them via reflection).</summary>
    public bool StrictMembers { get; set; }
}

public sealed class CliOptions
{
    public string Mode = "analyze";           // analyze | mark | delete | find
    public string SolutionPath;
    public List<string> RemoveTargets { get; } = new();
    public string FindSubstring;
    public string JsonPath;
    public string ConfigPath;
    public bool Verbose;
    public bool StrictMembers;
}
