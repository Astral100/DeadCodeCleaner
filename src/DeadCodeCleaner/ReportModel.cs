namespace DeadCodeCleaner;

public sealed class DeletionReport
{
    public required string Solution { get; init; }
    public required string[] RemoveTargets { get; init; }
    public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;
    public List<string> DeletableFiles { get; } = new();
    public List<SymbolDeletion> Symbols { get; } = new();
    public List<StatementDeletion> Statements { get; } = new();
    public List<ProjectDeletion> DeletableProjects { get; } = new();
    public List<ReferenceRemoval> PackageRefRemovals { get; } = new();
    public List<ReferenceRemoval> ProjectRefRemovals { get; } = new();
    public List<UsingRemoval> UsingRemovals { get; } = new();
    public List<string> Warnings { get; } = new();
    public ReportStats Stats { get; set; } = new();
}

public sealed class SymbolDeletion
{
    public required string Fqn { get; init; }
    public required string Kind { get; init; }
    /// <summary>removed | orphaned | broken-test | orphaned-test-helper</summary>
    public required string Category { get; init; }
    public required List<SourcePart> Parts { get; init; }
    /// <summary>Set when a deleted test also references surviving production code — review before deleting.</summary>
    public bool Review { get; init; }
    public string? ReviewNote { get; init; }
    public bool InDeletableFile { get; set; }
}

public sealed class SourcePart
{
    public required string File { get; init; }
    public required int StartLine { get; init; }   // 1-based
    public required int EndLine { get; init; }
    public required int SpanStart { get; init; }   // char offset incl. leading trivia
    public required int SpanLength { get; init; }
}

/// <summary>A whole project whose every declaration is deleted: remove it from the solution.</summary>
public sealed class ProjectDeletion
{
    public required string Project { get; init; }
    public required string CsprojPath { get; init; }
}

/// <summary>A using directive whose every provider assembly is being removed.</summary>
public sealed class UsingRemoval
{
    public required string File { get; init; }
    public required int Line { get; init; }
    public required string Namespace { get; init; }
}

/// <summary>A PackageReference or ProjectReference that loses its last surviving user.</summary>
public sealed class ReferenceRemoval
{
    public required string Project { get; init; }
    public required string CsprojPath { get; init; }
    /// <summary>Package name, or referenced project name.</summary>
    public required string Reference { get; init; }
}

public sealed class StatementDeletion
{
    public required string Kind { get; init; }
    public required string File { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required string Snippet { get; init; }
}

public sealed class ReportStats
{
    public int Projects { get; set; }
    public int Nodes { get; set; }
    public int Edges { get; set; }
    public int Rules { get; set; }
    public int ProductionRoots { get; set; }
    public int TestRoots { get; set; }
    public int InUseBaseline { get; set; }
    public int InUseAfterRemoval { get; set; }
    public int AlreadyOrphanedBefore { get; set; }
}
