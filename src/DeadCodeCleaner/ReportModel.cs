using System.Text.Json.Serialization;

namespace DeadCodeCleaner;

public sealed class DeletionReport
{
    /// <summary>Marks the file as this tool's own output, so the config scan of a later run never
    /// mistakes a report saved inside a project directory for runtime configuration.</summary>
    public string Generator { get; init; } = Constants.ReportGeneratorName;
    public required string Solution { get; init; }
    public required string[] RemoveTargets { get; init; }
    public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;
    public List<string> DeletableFiles { get; } = new();
    public List<SymbolDeletion> Symbols { get; } = new();
    public List<StatementDeletion> Statements { get; } = new();
    public List<ProjectDeletion> DeletableProjects { get; } = new();
    public List<ReferenceRemoval> PackageRefRemovals { get; } = new();
    public List<ReferenceRemoval> ProjectRefRemovals { get; } = new();
    /// <summary>Explicit &lt;Compile&gt; entries (old-style csproj) of files deleted in full.</summary>
    public List<ReferenceRemoval> CompileEntryRemovals { get; } = new();
    public List<UsingRemoval> UsingRemovals { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>New compile errors the in-memory verification found in the deletion plan (0 = safe).</summary>
    public int VerificationErrors { get; set; }
    /// <summary>Removal targets that surviving code still references (the STILL REFERENCED
    /// warnings): deleting them breaks or silently rebinds the callers.</summary>
    public int StillReferencedTargets { get; set; }
    /// <summary>Projects whose analysis compilation had errors — references in them are invisible.</summary>
    public int ProjectsWithCompileErrors { get; set; }
    /// <summary>Workspace load failures plus solution projects the analysis could not see at all
    /// (non-C#, failed evaluation): references from them are invisible, so delete refuses.</summary>
    public int ProjectsFailedToLoad { get; set; }
    /// <summary>File -> length of the text the spans were computed against: the editor refuses to
    /// touch a file whose on-disk text decodes to a different length (encoding mismatch, or the
    /// file changed since the analysis).</summary>
    [JsonIgnore]
    public Dictionary<string, int> FileTextLengths { get; } = new(Constants.PathComparer);
    /// <summary>File -> SHA-256 of the analyzed text: catches same-length concurrent edits the
    /// length check cannot see.</summary>
    [JsonIgnore]
    public Dictionary<string, string> FileTextHashes { get; } = new(Constants.PathComparer);
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
    public string ReviewNote { get; init; }
    public bool InDeletableFile { get; set; }
}

public sealed class SourcePart
{
    public required string File { get; init; }
    public required int StartLine { get; init; }   // 1-based
    public required int EndLine { get; init; }
    public required int SpanStart { get; init; }   // char offset incl. leading trivia
    public required int SpanLength { get; init; }
    /// <summary>True when StartLine begins inside a multi-line token (a string literal ending there):
    /// mark mode must not insert a marker line at it.</summary>
    public bool MarkerLineInsideToken { get; init; }
}

/// <summary>A whole project whose every declaration is deleted: remove it from the solution.</summary>
public sealed class ProjectDeletion
{
    public required string Project { get; init; }
    public required string CsprojPath { get; init; }
    /// <summary>Every C# file under the project directory at analysis time: a file added since then
    /// is code the plan never saw, so delete refuses instead of stranding it.</summary>
    [JsonIgnore]
    public List<string> SourceFiles { get; init; } = new();
}

/// <summary>A using directive whose every provider assembly is being removed.</summary>
public sealed class UsingRemoval
{
    public required string File { get; init; }
    public required int Line { get; init; }
    public required string Namespace { get; init; }
    /// <summary>Char range to delete (the directive with its leading trivia, minus leading #directives).</summary>
    public required int SpanStart { get; init; }
    public required int SpanLength { get; init; }
}

/// <summary>A PackageReference or ProjectReference that loses its last surviving user.</summary>
public sealed class ReferenceRemoval
{
    public required string Project { get; init; }
    public required string CsprojPath { get; init; }
    /// <summary>Package name, referenced project name, or a Compile item's include path.</summary>
    public required string Reference { get; init; }
    /// <summary>For project references: the Include attribute value as written in the csproj (a
    /// solution can display the project under a different name). Null for packages.</summary>
    public string ReferenceFile { get; init; }
}

public sealed class StatementDeletion
{
    public required string Kind { get; init; }
    public required string File { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required string Snippet { get; init; }
    /// <summary>Char range to delete: the whole statement, or just this call's chain segment.</summary>
    public required int SpanStart { get; init; }
    public required int SpanLength { get; init; }
    public required bool WholeStatement { get; init; }
    /// <summary>See SourcePart.MarkerLineInsideToken.</summary>
    public bool MarkerLineInsideToken { get; init; }
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
