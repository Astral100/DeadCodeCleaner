using System.Text.Json;

namespace DeadCodeCleaner;

/// <summary>Hardcoded values of the tool: markers, lookup tables, patterns and limits.</summary>
public static class Constants
{
    public const string Marker = "// todo: to be deleted";
    public const string XmlMarker = "<!-- todo: to be deleted -->";
    /// <summary>XmlMarker without the closing "-->", for prefix checks against already-marked lines.</summary>
    public const string XmlMarkerPrefix = "<!-- todo: to be deleted";
    public const string DefaultReportFileName = "deadcode-report.json";

    public const int ConsoleListLimit = 100;
    public const int FindMatchLimit = 200;
    public const int WorkspaceWarningLimit = 10;
    public const int SuggestionLimit = 5;
    public const int ShownCompileErrors = 3;
    public const int ShownSurvivingRefs = 3;
    public const int SnippetMaxLength = 160;
    public const int ReviewNoteMaxLength = 120;
    public const int SlnRemoveTimeoutMs = 60_000;

    public const string TestProjectNamePattern = @"(^|[._])Tests?([._]|$)";
    public const string TestPackagePattern = @"Include=""(xunit|nunit|NUnit|MSTest|Microsoft\.NET\.Test\.Sdk)";

    public static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Build settings that break design-time builds but don't affect analysis.</summary>
    public static readonly Dictionary<string, string> NeutralizedBuildProperties = new()
    {
        ["RunAnalyzers"] = "false",
        ["RunAnalyzersDuringBuild"] = "false",
        ["TreatWarningsAsErrors"] = "false",
        ["MSBuildTreatWarningsAsErrors"] = "false",
        ["WarningsAsErrors"] = "",
        ["NuGetAudit"] = "false",
    };

    public static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestMethod", "TestCase", "TestCaseSource", "SetUp", "TearDown",
        "OneTimeSetUp", "OneTimeTearDown", "TestInitialize", "TestCleanup", "ClassInitialize",
        "ClassCleanup", "AssemblyInitialize", "AssemblyCleanup", "TestFixture",
    };

    public static readonly HashSet<string> ProdRootAttributeNames = new(StringComparer.Ordinal)
    {
        "FunctionName", "Function", "Benchmark", "GlobalSetup", "GlobalCleanup",
    };

    /// <summary>Members kept while their type is in use: pattern-based language features and framework
    /// callbacks reference these without a resolvable identifier in syntax.</summary>
    public static readonly HashSet<string> KeepAliveMemberNames = new(StringComparer.Ordinal)
    {
        "GetEnumerator", "MoveNext", "Current", "GetAwaiter", "GetResult", "IsCompleted",
        "OnCompleted", "UnsafeOnCompleted", "Deconstruct", "Dispose", "DisposeAsync", "Add",
    };

    /// <summary>Implementing/inheriting these (matched by simple name) binds type-argument[Index] to the
    /// implementor: when the bound type is in use, the implementor is in use (MediatR handlers,
    /// FluentValidation validators).</summary>
    public static readonly (string Name, int ArgIndex, bool IsInterface)[] Binders =
    {
        ("IRequestHandler", 0, true), ("INotificationHandler", 0, true),
        ("IStreamRequestHandler", 0, true), ("IRequestExceptionHandler", 0, true),
        ("IRequestExceptionAction", 0, true),
        ("AbstractValidator", 0, false), ("IValidator", 0, true),
    };

    public static readonly HashSet<string> MsDiMethodNames = new(StringComparer.Ordinal)
    {
        "AddScoped", "AddTransient", "AddSingleton", "TryAddScoped", "TryAddTransient", "TryAddSingleton",
    };

    public static readonly HashSet<string> AutofacChainContinuers = new(StringComparer.Ordinal)
    {
        "SingleInstance", "InstancePerLifetimeScope", "InstancePerDependency", "InstancePerRequest",
        "InstancePerMatchingLifetimeScope", "InstancePerOwned", "ExternallyOwned", "OwnedByLifetimeScope",
        "WithParameter", "WithParameters", "WithProperty", "Named", "Keyed", "PreserveExistingDefaults",
        "IfNotRegistered", "AutoActivate", "PropertiesAutowired", "UsingConstructor", "OnActivating",
        "OnActivated", "OnPreparing", "OnRelease",
    };
}
