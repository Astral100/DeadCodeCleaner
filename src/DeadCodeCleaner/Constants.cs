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
    public const string DirectoryBuildPropsFileName = "Directory.Build.props";
    public const int WindowsWesternCodePage = 1252;

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
    public const int ShownViewMentions = 10;
    public const int MaxScannedConfigFileBytes = 1_000_000;
    /// <summary>How many nested constructions an initializer side-effect check follows before assuming
    /// the construction acts.</summary>
    public const int MaxConstructionFollowDepth = 8;
    public const int MinMentionNameLength = 3;
    public const int ShownMentionKeeps = 20;
    public const string IdentifierWordPattern = @"[A-Za-z_][A-Za-z0-9_]*";
    /// <summary>Dotted names as config files write types: "Ns.Type", "Ns.Outer+Inner".</summary>
    public const string DottedNameWordPattern = @"[A-Za-z_][A-Za-z0-9_]*(?:[.+][A-Za-z_][A-Za-z0-9_]*)*";
    public const string ScriptExtension = ".js";
    public const string MinifiedScriptSuffix = ".min.js";
    /// <summary>Solution-file project type of an ASP.NET Web Site project (no csproj; App_Code).</summary>
    public const string WebSiteProjectTypeGuid = "E24C65DC-7377-472B-9ABA-BC803B73C61A";

    public const string CategoryRemoved = "removed";
    public const string CategoryOrphaned = "orphaned";
    public const string CategoryBrokenTest = "broken-test";
    public const string CategoryBrokenOrphan = "broken-orphan";
    public const string CategoryOrphanedTestHelper = "orphaned-test-helper";

    public const string ReasonAutoMapperProfile = "AutoMapper profile";
    public const string ReasonAutofacModule = "Autofac module";
    public const string ReasonController = "controller";
    public const string ReasonEfMigration = "EF migration";
    public const string ReasonFrameworkComponent = "framework component";
    public const string ReasonFrameworkSubclass = "framework-discovered subclass";
    public const string ReasonFrameworkImplementation = "framework-discovered implementation";
    public const string ReasonMefExport = "MEF export";

    /// <summary>Root kinds frameworks find by scanning REFERENCED assemblies at runtime (MVC application
    /// parts, profile/module/migration discovery, MEF): a project reference to an assembly still holding
    /// one is kept even when no surviving code uses it at compile time.</summary>
    public static readonly HashSet<string> RuntimeDiscoveredRootReasons = new(StringComparer.Ordinal)
    {
        ReasonController, ReasonEfMigration, ReasonFrameworkComponent, ReasonFrameworkSubclass,
        ReasonFrameworkImplementation, ReasonMefExport, ReasonAutoMapperProfile, ReasonAutofacModule,
    };
    public const string CreateMapMethodName = "CreateMap";
    public const string StatementKindDiRegister = "DI.Register";
    /// <summary>Warning prefix for a still-used service whose only registration dies with a deleted
    /// implementation; delete refuses while any exist.</summary>
    public const string ServiceLeftUnregisteredPrefix = "SERVICE LEFT UNREGISTERED:";
    public const string AutoMapperProfileBaseName = "Profile";
    public const string AutofacModuleBaseName = "Module";

    /// <summary>File paths are case-insensitive on Windows (linked files can be written with another
    /// casing) but case-sensitive on Linux, where Foo.cs and foo.cs are different files.</summary>
    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>StartsWith/EndsWith companion to PathComparer.</summary>
    public static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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

    /// <summary>Runtime-compiled markup whose TEXT is scanned for deleted-symbol mentions: these
    /// files reference C# symbols by name, compile at runtime, and are invisible to the verifier.</summary>
    public static readonly HashSet<string> ViewFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cshtml", ".vbhtml", ".razor", ".aspx", ".ascx", ".master", ".asax",
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
        "RegisterAssemblyTypes", "Scan", "FromThisAssembly", "FromAssemblyContaining", "FromAssemblyOf",
        "FromLoadedAssemblies", "TheCallingAssembly", "AssemblyContainingType", "FromAssembliesMatching",
        "FromApplicationDependencies", "FromAssemblyInThisApplication",
    };

    public const string AutofacScanMethodName = "RegisterAssemblyTypes";

    /// <summary>Scan vocabulary naming the scanned assembly by a type argument: FromAssemblyOf&lt;X&gt;().</summary>
    public static readonly HashSet<string> ScanAssemblyOfTypeNames = new(StringComparer.Ordinal)
    {
        "FromAssemblyOf", "FromAssemblyContaining", "AssemblyContainingType", "AssemblyContaining", "FromAssembliesOf",
    };

    /// <summary>Scan vocabulary meaning "the assembly whose code runs the scan".</summary>
    public static readonly HashSet<string> ScanCurrentAssemblyNames = new(StringComparer.Ordinal)
    {
        "FromThisAssembly", "TheCallingAssembly", "GetExecutingAssembly", "FromExecutingAssembly",
    };

    /// <summary>Scan vocabulary naming assemblies the analysis cannot pin down: every solution
    /// assembly is assumed.</summary>
    public static readonly HashSet<string> ScanUnknownAssemblyNames = new(StringComparer.Ordinal)
    {
        "FromLoadedAssemblies", "FromApplicationDependencies", "FromAssembliesMatching", "FromAssemblyInThisApplication",
        "AssembliesFromApplicationBaseDirectory", "AssembliesFromPath", "FromAssemblyInDirectory", "FromAssemblies",
        "FromAssembly", "AssemblyContainingTypes", "FromEntryAssembly", "FromAssemblyDependencies", "FromDependencyContext",
        "GetCallingAssembly", "FromCallingAssembly", "GetEntryAssembly",
    };

    /// <summary>The unknown-assembly calls whose ARGUMENTS are the scanned assemblies themselves
    /// (FromAssemblies(typeof(X).Assembly) stays precise); the others never are (FromAssemblyDependencies(a)
    /// scans a's dependencies too).</summary>
    public static readonly HashSet<string> ScanAssemblyListNames = new(StringComparer.Ordinal)
    {
        "FromAssemblies", "FromAssembly",
    };

    /// <summary>Class-selection calls of scanning libraries: with them, a statement registers types even
    /// when its assembly source is not recognised.</summary>
    public static readonly HashSet<string> ScanSelectionNames = new(StringComparer.Ordinal)
    {
        "AddClasses", "AddTypes", "BasedOn", "Pick", "IncludeNonPublicTypes", "InSameNamespaceAs", "SelectAllClasses",
        "IncludeAllClasses",
    };

    /// <summary>Registration calls of a scan meaning "under every interface the class implements".</summary>
    public static readonly HashSet<string> ScanAllInterfacesNames = new(StringComparer.Ordinal)
    {
        "AsImplementedInterfaces", "WithServiceAllInterfaces", "BindAllInterfaces", "FromAllInterfaces", "AsAllInterfaces",
        "AsSelfWithInterfaces", "AllInterfaces",
    };

    /// <summary>Registration calls of a scan meaning "under the interface named like the class". Read as
    /// the widest library rule (Windsor, Ninject's plural form): the class name CONTAINS the interface
    /// name without its leading "I", ignoring case — a superset of the exact "I" + name conventions.</summary>
    public static readonly HashSet<string> ScanMatchingInterfaceNames = new(StringComparer.Ordinal)
    {
        "WithDefaultConventions", "FromMatchingInterface", "BindDefaultInterface", "BindDefaultInterfaces",
        "WithServiceDefaultInterfaces", "AsMatchingInterface", "DefaultInterfaces",
    };

    /// <summary>Registration calls of a scan naming the service TYPES (generic or typeof arguments):
    /// As&lt;Base&gt;(), AsClosedTypesOf(typeof(IHandler&lt;&gt;)), Named&lt;IPlugin&gt;(...), Keyed&lt;T&gt;(...).</summary>
    public static readonly HashSet<string> ScanServiceTypeNames = new(StringComparer.Ordinal)
    {
        "As", "AsClosedTypesOf", "AsClosedInterfacesOf", "Named", "Keyed",
    };

    /// <summary>Scan calls whose registration the analysis cannot follow (base-class services, custom
    /// selection, start-up activation): every matched class is kept while the scanning member lives.</summary>
    public static readonly HashSet<string> ScanKeepAllNames = new(StringComparer.Ordinal)
    {
        "WithServiceBase", "WithServiceFirstInterface", "WithServiceFromInterface", "WithServiceSelect", "WithService",
        "WithServices", "BindBase", "BindAllBaseClasses", "BindWith", "BindSelection", "BindUsingRegex", "BindToSelection",
        "AddAllTypesOf", "ConnectImplementationsToTypesClosing", "UsingAttributes", "UsingRegistrationStrategy", "AutoActivate",
        "Configure", "ConfigureFor", "RegisterConcreteTypesAgainstTheFirstInterface", "SingleImplementationsOfInterface",
        "Base", "FirstInterface", "FromInterface", "Select",
    };

    /// <summary>Autofac scan-chain calls that only NARROW the matched set (ignoring them only widens it)
    /// or set a lifetime: anything else in the chain makes the registration unreadable (kept whole).</summary>
    public static readonly HashSet<string> AutofacScanNarrowingNames = new(StringComparer.Ordinal)
    {
        "Where", "AssignableTo", "Except", "InNamespace", "InNamespaceOf", "PublicOnly",
    };

    /// <summary>Autofac registration-builder calls that make the container build the component at
    /// start-up: the registration lives while its containing member does, whoever resolves it.</summary>
    public static readonly HashSet<string> AutofacActivationNames = new(StringComparer.Ordinal)
    {
        "AutoActivate",
    };

    /// <summary>OWIN calls this method of the [assembly: OwinStartup(typeof(X))] class unless the
    /// attribute names another.</summary>
    public const string OwinDefaultStartMethod = "Configuration";
    public const string OwinStartupAttributeName = "OwinStartup";

    /// <summary>Generic-host / WebHost calls naming a startup class whose ConfigureServices/Configure
    /// methods the framework calls by convention.</summary>
    public static readonly HashSet<string> UseStartupMethodNames = new(StringComparer.Ordinal)
    {
        "UseStartup",
    };

    /// <summary>The C# compiler's warning group for every nullable-reference-type warning.</summary>
    public const string NullableWarningGroup = "nullable";

    /// <summary>Registration calls of a scan meaning "as the class itself only".</summary>
    public static readonly HashSet<string> ScanSelfNames = new(StringComparer.Ordinal)
    {
        "AsSelf", "WithServiceSelf", "BindToSelf", "Self",
    };

    /// <summary>Assemblies whose use does not AUTOMATICALLY block deleting an "empty" profile/module
    /// whole. These are only candidates: mscorlib and System.Runtime also hold File, Environment and
    /// Process, so a call into them blocks the deletion anyway unless its TYPE is on the pure-surface
    /// lists below.</summary>
    public static readonly HashSet<string> EmptyContainerBenignAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "netstandard", "System.Private.CoreLib", "System.Runtime", "System", "System.Core",
        "System.Linq", "System.Linq.Expressions", "System.Collections", "System.ObjectModel",
    };

    /// <summary>Namespaces in the benign assemblies whose every member is free of external side
    /// effects: calls here never block the empty-container pass.</summary>
    public static readonly HashSet<string> EmptyContainerPureNamespaces = new(StringComparer.Ordinal)
    {
        "System.Linq", "System.Linq.Expressions", "System.Collections", "System.Collections.Generic",
        "System.Collections.ObjectModel", "System.Collections.Concurrent", "System.Collections.Specialized",
        "System.Text", "System.Text.RegularExpressions", "System.Globalization",
    };

    /// <summary>Individual side-effect-free types (full names) in benign-assembly namespaces that
    /// also hold dangerous ones (System has both String and Environment; System.IO has Path and File).</summary>
    public static readonly HashSet<string> EmptyContainerPureTypes = new(StringComparer.Ordinal)
    {
        "System.String", "System.Object", "System.Math", "System.MathF", "System.Convert", "System.Guid",
        "System.DateTime", "System.DateTimeOffset", "System.TimeSpan", "System.TimeZoneInfo", "System.Uri",
        "System.Version", "System.Random", "System.Char", "System.Boolean", "System.Byte", "System.SByte",
        "System.Int16", "System.Int32", "System.Int64", "System.UInt16", "System.UInt32", "System.UInt64",
        "System.Single", "System.Double", "System.Decimal", "System.Enum", "System.Array", "System.Tuple",
        "System.ValueTuple", "System.Nullable", "System.Lazy", "System.Func", "System.Action",
        "System.Predicate", "System.Comparison", "System.EventHandler", "System.StringComparer",
        "System.Exception", "System.ArgumentException", "System.ArgumentNullException",
        "System.ArgumentOutOfRangeException", "System.InvalidOperationException", "System.NotSupportedException",
        "System.NotImplementedException", "System.FormatException", "System.InvalidCastException",
        "System.IO.Path",
    };

    /// <summary>Chained CreateMap calls configuring ONE member (".ForMember(d => d.X, ...)"): when the
    /// member dies, only the segment is removed and the rest of the mapping stands. Structural chain
    /// calls (ConvertUsing, Include, ReverseMap) still kill the whole mapping instead.</summary>
    public static readonly HashSet<string> MemberLevelMapChainNames = new(StringComparer.Ordinal)
    {
        "ForMember", "ForPath", "ForSourceMember",
    };

    /// <summary>Attributes marking a type whose FIELD set is its marshalled/serialized shape: every
    /// field is kept while the type is in use, whatever its accessibility.</summary>
    public static readonly HashSet<string> LayoutSensitiveAttributeNames = new(StringComparer.Ordinal)
    {
        "StructLayoutAttribute", "DataContractAttribute",
    };

    /// <summary>Top-level property marking this tool's own JSON reports, so a report saved inside a
    /// project directory is never scanned as configuration on the next run.</summary>
    public const string ReportGeneratorName = "DeadCodeCleaner";
    public const string ReportGeneratorJsonFragment = "\"generator\": \"DeadCodeCleaner\"";

    /// <summary>Whole package/assembly ids only (anchored): "xunit.abstractions" alone marks a
    /// LIBRARY that extends a test framework, not a test project, and must not match. Matched
    /// against the id part of parsed PackageReference/Reference Include values, never raw text.</summary>
    public const string TestPackageIdPattern = @"^(xunit(\.v3)?(\.core|\.runner\.[\w.]+|\.extensibility\.[\w.]+)?|nunit|nunit\.framework|nunit3testadapter|nunitlite|mstest(\.testframework|\.testadapter)?|microsoft\.net\.test\.sdk|microsoft\.visualstudio\.qualitytools\.unittestframework|microsoft\.visualstudio\.testplatform\.testframework(\.extensions)?)$";
    /// <summary>The MSBuild SDK of MSTest projects: brings the test framework without a PackageReference.</summary>
    public const string MsTestSdkName = "MSTest.Sdk";
    /// <summary>Legacy csproj ProjectTypeGuids entry marking an MSTest project.</summary>
    public const string TestProjectTypeGuid = "3AC096D0-A1C2-E12C-1390-A8335801FDAB";

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
        "ClassCleanup", "AssemblyInitialize", "AssemblyCleanup",
    };

    /// <summary>Method attributes that make a method an actual TEST (not setup/teardown): a project is
    /// a test project only when it contains at least one, besides referencing a test library.</summary>
    public static readonly HashSet<string> TestMethodAttributeNames = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod",
    };

    /// <summary>Class attributes marking a test fixture (NUnit, MSTest): the class is a test-world root
    /// even when its tests are all inherited from a base fixture.</summary>
    public static readonly HashSet<string> TestClassAttributeNames = new(StringComparer.Ordinal)
    {
        "TestFixture", "TestClass",
    };

    /// <summary>Suffix of the ExternalCallRefs entry recorded for a write to an external member (static
    /// property/field assignment, event subscription): outside the benign set, so it always blocks the
    /// empty-container pass.</summary>
    public const string ExternalWriteSuffix = "!write";

    public static readonly HashSet<string> ProdRootAttributeNames = new(StringComparer.Ordinal)
    {
        "FunctionName", "Function", "Benchmark", "GlobalSetup", "GlobalCleanup", "ModuleInitializer",
    };

    /// <summary>Global.asax event methods are named "&lt;Module&gt;_&lt;Event&gt;" (Application_Start,
    /// FormsAuthentication_OnAuthenticate): ASP.NET wires every such name, whatever its accessibility.</summary>
    public const char HttpApplicationEventSeparator = '_';

    /// <summary>Assembly attributes naming a startup method by STRING next to its type (System.Web,
    /// WebActivatorEx): the type alone is not enough, the named method must be rooted too.</summary>
    public static readonly HashSet<string> StartMethodAttributeNames = new(StringComparer.Ordinal)
    {
        "PreApplicationStartMethod", "PostApplicationStartMethod", "ApplicationShutdownMethod", "OwinStartup",
    };

    /// <summary>Autofac registration-builder calls from integration packages (MVC/Web API): lifetimes
    /// that declare no service, so the chain walk continues past them.</summary>
    public static readonly HashSet<string> AutofacExtraLifetimeNames = new(StringComparer.Ordinal)
    {
        "InstancePerHttpRequest", "InstancePerApiRequest",
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
