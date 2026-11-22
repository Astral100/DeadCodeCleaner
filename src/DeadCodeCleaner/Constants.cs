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
    public const int VerifyShownErrors = 20;
    public const int ShownInactiveMentions = 10;
    public const int ShownDynamicMentions = 10;
    public const int ShownConfigMentions = 10;
    public const int MaxScannedConfigFileBytes = 1_000_000;

    public const string CategoryRemoved = "removed";
    public const string CategoryOrphaned = "orphaned";
    public const string CategoryBrokenTest = "broken-test";
    public const string CategoryBrokenOrphan = "broken-orphan";
    public const string CategoryOrphanedTestHelper = "orphaned-test-helper";

    public const string ReasonAutoMapperProfile = "AutoMapper profile";
    public const string ReasonAutofacModule = "Autofac module";
    public const string CreateMapMethodName = "CreateMap";

    /// <summary>File paths are case-insensitive on Windows (linked files can be written with another
    /// casing) but case-sensitive on Linux, where Foo.cs and foo.cs are different files.</summary>
    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static readonly HashSet<string> AutofacRegisterMethodNames = new(StringComparer.Ordinal)
    {
        "RegisterType", "RegisterGeneric",
    };

    /// <summary>File types compiled outside the solution's C# compilations (views, WebForms, config
    /// wiring): references from them are invisible, so their presence warrants a warning.</summary>
    public static readonly HashSet<string> NonCompiledContentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cshtml", ".razor", ".aspx", ".ascx", ".master", ".asax", ".xaml", ".axaml", ".svc", ".asmx", ".ashx",
    };

    /// <summary>Directories never descended into when scanning project folders for content files.</summary>
    public static readonly HashSet<string> SkippedScanDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs",
    };

    /// <summary>Invocation names that register types by scanning assemblies at runtime (Autofac,
    /// Scrutor): the scanned implementations are invisible to the analysis, so their presence
    /// warrants a warning.</summary>
    public static readonly HashSet<string> AssemblyScanMethodNames = new(StringComparer.Ordinal)
    {
        "RegisterAssemblyTypes", "RegisterAssemblyModules", "Scan",
    };

    /// <summary>Assemblies whose use does not block deleting an "empty" profile/module whole:
    /// pure-data framework code with no observable side effects worth preserving.</summary>
    public static readonly HashSet<string> EmptyContainerBenignAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "netstandard", "System.Private.CoreLib", "System.Runtime", "System", "System.Core",
        "System.Linq", "System.Linq.Expressions", "System.Collections", "System.ObjectModel",
    };

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
    /// callbacks reference these without a resolvable identifier in syntax. The language patterns
    /// require accessibility, not "public", so accessibility is not checked here.</summary>
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
