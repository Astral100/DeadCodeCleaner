# DeadCodeCleaner

Deterministic cascade dead-code analyzer for .NET solutions. Given one or more symbols you intend
to remove (a controller, an action method, a service — any type or member), it computes **everything
that becomes orphaned as a result**, transitively, in one pass — while leaving code that was
*already* orphaned before the removal untouched.

## How it works

1. The whole solution is loaded with Roslyn and turned into a dependency graph: every type/member
   declared in source is a node; every semantic reference is an edge. Nodes are keyed by
   documentation-comment id, so a symbol seen from source and through a project-reference DLL is
   the same node. References are collected from BOTH the syntax tree and the compiler's bound tree
   (IOperation), so constructs with no identifier in source still count: LINQ query syntax,
   collection-initializer `Add`, `x?[0]`, object/element initializers, user-defined operators and
   conversions, `var`-inferred types, deconstruction, enum-member attributes, and a primary
   constructor's `: Base(x)` call. Compile-only dependencies are edges too: an `override` keeps
   its base member alive, an explicit interface implementation keeps the interface member alive.
   Assembly-level attributes root everything they reference. Pattern members the language reaches
   without identifiers (`Deconstruct`, `Dispose`, `GetEnumerator`, ...) are kept with their type
   whatever their accessibility. A primary constructor is never tracked as a deletable symbol of
   its own — its declaring syntax IS the type declaration, so uses of it count as uses of the type.
2. Entry points ("roots") are detected: `Main`, controllers (name or base class), hosted services,
   `Startup`/`Program`, AutoMapper profiles, Autofac modules, and framework-discovered code that has
   no static references by design — EF migrations, `IEntityTypeConfiguration<>` classes and EF6
   `EntityTypeConfiguration<>`/`ComplexTypeConfiguration<>` subclasses, MVC `AreaRegistration`
   subclasses, Ninject modules, `[ModuleInitializer]` methods,
   ViewComponents, TagHelpers, Razor PageModels, Blazor components, SignalR hubs, middleware
   (`IMiddleware` or a `RequestDelegate` constructor), Quartz jobs, and the WebForms/legacy surface
   (`Page`/`UserControl`/`MasterPage` code-behind with EVERY method — `Page_Load` and markup-wired
   handlers are protected and called by name — `HttpApplication` with its `Application_*`/`Session_*`
   event methods at any accessibility, the method named by `[assembly: PreApplicationStartMethod]`
   and its WebActivatorEx variants, `IHttpModule`, `IHttpHandler`,
   ASMX `WebService` subclasses, WCF `[ServiceContract]` contracts and their implementations, MEF
   `[Export]` types, and config-wired `MembershipProvider`/`RoleProvider`/`ProfileProvider`/
   `ConfigurationSection` subclasses) — plus anything matched by the config's `keep` patterns.
   A field or auto-property whose initializer DOES something (a call, or creating an external
   non-trivial object such as a `Timer`) stays while its type is in use, exactly as the same code
   in a constructor would.
   Base classes and interfaces are recognized by name only when they come from OUTSIDE the
   solution (the real framework): a solution's own `Profile` or `Hub` class is ordinary code, and
   its subclasses are not roots.
3. Reachability ("in use") is computed twice: from all roots, and from all roots with the removal
   targets treated as deleted. The difference is the complete cascade. Code unreachable in the
   baseline is pre-existing dead code and is never reported.
4. **The compiler referees the result**: the whole deletion plan is applied to the sources in
   memory — code spans deleted, files and projects removed, project references dropped, and the
   assemblies of removed packages stripped from the compilations — the affected projects are
   recompiled, and every compile error that was not there before is reported as a `VERIFY`
   warning (errors are counted per message, so a new error identical to a pre-existing one still
   registers). `delete` refuses to touch the disk while any exist (`--no-verify` overrides).
   Over-deletion therefore cannot slip through silently even where the analysis has a blind spot.

### Indirect dependencies

- **MediatR / FluentValidation**: a handler/validator is in use iff its request/subject type is in
  use (matched by implementing `IRequestHandler<,>`, `INotificationHandler<>`, `AbstractValidator<>`
  etc.). While a handler exists, the binder interface's members (`Handle`) count as used.
- **AutoMapper `CreateMap<A,B>()`** and **DI registrations** (`AddScoped/AddTransient/AddSingleton`,
  Autofac `RegisterType<T>().As<I>()` chains, `typeof` overloads) are *co-dependent statements*:
  a registration lives only while its SERVICE type is in use (it cannot revive the service), and
  while it lives it keeps its implementation constructible; when the service dies, the statement
  dies with it and is reported as an individual statement to remove. The whole statement is deleted only when it is a plain expression
  statement whose entire call chain belongs to dead registrations (AutoMapper's `ForMember`/
  `ReverseMap` and Autofac's lifetime calls count as part of theirs); otherwise only the dead call's
  chain segment is removed (`services.AddScoped<IA,A>().AddLogging()` keeps `AddLogging`, and
  `return services.AddScoped<IA,A>();` keeps the `return`). A registration that is the body of a
  braceless `if`/loop takes the whole construct with it — unless the construct's condition or
  header has side effects (a call, assignment, or increment), which, like an if/else branch or a
  lambda, is reported as a warning for manual removal instead. References inside a map's `ForMember` lambdas
  belong to the statement, not the profile, so they cannot keep the mapped types alive forever; so
  does every chained call's own target, so a solution-declared extension like
  `.IgnoreAllNonExisting()` stays in use while a mapping calls it and dies with its last one. A
  statement whose text references deleted code dies with it: removing an
  implementation type takes its registration along even while the service interface stays in use
  (with a warning, since behavior changes). One refinement: member-level map configuration
  (`ForMember`/`ForPath`/`ForSourceMember`) dies ALONE — removing a mapped member removes just that
  `.ForMember(...)` segment and the rest of the mapping stands; structural calls (`ConvertUsing`,
  `Include`) still take the whole mapping. A registration whose SERVICE type lives outside the
  solution (`AddSingleton<IAuthorizationHandler, MinAgeHandler>()`, Autofac `As<IExternal>()`,
  `Keyed<T>`/`Named<T>`, or `AsImplementedInterfaces()` over external interfaces) is resolved by
  the framework at runtime, so no in-solution reference to it can exist: it stays alive (and keeps
  its implementation alive) while its registering member does. Autofac's non-generic forms
  (`As(typeof(IFoo))`, `Named("x", typeof(IFoo))`, `Keyed(k, typeof(IFoo))`) register services
  exactly like the generic ones; a `Type` that is not a `typeof`, or any chained call the tool does
  not know (it may declare services the walk cannot see), keeps the registration alive while its
  registering member is.
- **Empty containers**: an AutoMapper profile or Autofac module whose every mapping/registration
  died (and that nothing surviving references) is deleted whole — unless a config `keep` pattern
  matches it, or any of its members still calls into an external assembly beyond a small benign
  list: an untracked registration (`RegisterInstance`, `Register(c => ...)`, an unresolvable
  `RegisterType(variable)`, assembly scanning) leaves no graph edge, and deleting the container
  would silently drop it. The benign list is type-aware, not assembly-wide: `mscorlib` and
  `System.Runtime` also hold `File`, `Environment` and `Process`, so only calls to a pure surface
  (LINQ, collections, text, and specific `System` types) pass — `File.WriteAllText` in a module's
  `Load` blocks the deletion even on .NET Framework, where everything lives in `mscorlib`. Writes
  to external members count as such side effects too (`ServicePointManager.SecurityProtocol = ...`,
  `CultureInfo.DefaultThreadCurrentCulture = ...`, event subscriptions), except writes to the
  container's own state. Calls into a USER base class between the container and the framework base
  (`OrdersModule : BaseModule` calling `RegisterRepositories(b)`) block it too: such helpers do the
  real registering. Code in nested types counts as part of the container, and the pass repeats
  until nothing more dies. Constructor initializers (`: base("name")`, explicit or the implicit base
  call) do NOT count as such calls — they die with the type. Chained generic type
  arguments (`ConvertUsing<TConverter>()`, `Include<TDerived, TOther>()`) are statement references
  too: they stay alive while the map lives and die with it.
- **Build plumbing cascades too**: a NuGet `PackageReference` or `ProjectReference` whose last
  surviving user was deleted is removed from the csproj (entries under a `Condition` are left in
  place with a warning — only one build configuration is analyzed — and so is a project reference
  whose assembly a config file names, as in `type="Lib.Module, Lib"`, and a package another
  remaining package depends on: it may be listed only to pin a newer version, and removing it would
  silently downgrade); `using` directives that no
  assembly can satisfy any more are removed (matched under their RESOLVED names, so `using Sub;`
  inside `namespace A` means `A.Sub`; a file banner above the first using is never deleted with
  it). A namespace is judged per referenced assembly: another project's types under the same name
  do not keep a `using` alive in a consumer that never references that project. A project whose every declaration is deleted is removed from the
  solution and its csproj deleted (directory too, when nothing but code remained) — unless one of
  its files carries inactive `#if` regions or duplicate type names; then the project stays, with a
  warning, and only its code is deleted file by file. Transitive
  reachability is respected for both kinds and ACROSS projects — SDK-style references are
  transitive, so a reference in project B is kept when a dependent project A reaches code or
  package assemblies only through it (e.g. `EFCore` via `EFCore.SqlServer`, or A → B → C where
  only A uses C). When no kept reference can cover a need (the only path ran through a deleted
  project), a warning asks for a manual direct reference. "Already unused before" references
  stay, like symbols. Removing a symbol that SURVIVING code still references cannot compile:
  such targets are flagged with a `STILL REFERENCED` warning naming the referrers.

### Tests

Test projects never keep production code in use. A project is a test project only when it BOTH
references a test library (a direct test-framework package or assembly reference, `<IsTestProject>`,
or a test `ProjectTypeGuids` entry — parsed from the csproj as XML, so a commented-out package is
not evidence) AND contains at least one actual test (`[Fact]`, `[Theory]`, `[Test]`, `[TestCase]`,
`[TestMethod]`, or a `[TestFixture]`/`[TestClass]` class). The name never counts: "X.Tests" without
tests is production code, and a test-library project without any test (shared test helpers) is
production code with a warning. A class with no test of its own is still a test class when it
carries `[TestFixture]`/`[TestClass]` or inherits tests from a base fixture. When production code is
deleted, any test referencing it is deleted too ("broken-test"), test helpers used only by those
tests cascade ("orphaned-test-helper"), and a deleted test that *also* touches surviving production
code is flagged `[REVIEW]`. One broken test never takes its healthy siblings along: the class is
deleted only when every test in it died. Code in a test project that production uses (and tests
never reach) is treated as production. A file linked into both a production and a test project
belongs to production. A config `keep` pattern protects test code too: kept code that references
deleted code is never deleted — it is reported `STILL REFERENCED` and `delete` refuses.

`delete` REFUSES to run while any `[REVIEW]` test exists, because whether such a test should be
salvaged is a judgment call: run `analyze` or `mark` first, let a human or an LLM rewrite the
flagged tests to drop the deleted references and keep the assertions that still matter (the tool
re-analyzes on every run, so adjusted tests simply survive), then run `delete` again. Pass
`--delete-review-tests` to delete them as-is instead.

### Pre-existing orphans that reference deleted code

Code that was already orphaned before the removal stays untouched — unless it references newly
deleted code, in which case it would no longer compile and is deleted too ("broken-orphan"
category), cascading transitively. A dead member of a dead type escalates to the whole type, so no
half-deleted class is left violating an interface contract — but never when the TEST world still
reaches the type: a sibling member kept alive by a healthy test references nothing deleted, so only
the broken member goes and the class, its test-live members and their tests all stay. The same
holds for the empty-shell pass: a production type tests still construct keeps its (empty) shell.

"Already orphaned" can also mean the analysis cannot see who uses the code: frameworks create
classes by scanning and conventions with no line of the solution naming them. So a broken-orphan
CLASS built on an outside framework type (a base class or interface from outside the solution and
outside the plain BCL — `Exception` or `IDisposable` do not count) that nothing in the solution
references is flagged `[REVIEW]` ("possibly created by a framework at runtime"), and `delete`
refuses while any exist (`--delete-review-orphans` deletes them as-is). Plain leftover classes are
still deleted automatically.

### Assembly scanning

Registrations by assembly scanning name no class, so each scan is read from the code: Autofac's
`RegisterAssemblyTypes(...)` along its fluent chain, and the Scrutor / StructureMap / Lamar /
Windsor / Ninject / Unity forms over their whole statement.

- **Which assemblies**: `typeof(X).Assembly`, `FromAssemblyOf<X>()`, `FromThisAssembly()`,
  `Assembly.GetExecutingAssembly()`, ...; anything else (a variable, `FromLoadedAssemblies()`)
  means every solution assembly, with a warning.
- **Which classes** (Autofac `.Where(t => ...)` and `AssignableTo<T>()`): `t.Name`/`t.Namespace`/
  `t.FullName` with `EndsWith`/`StartsWith`/`Contains`/`==`/`!=`, `t.IsSubclassOf(typeof(B))`,
  `t.IsAssignableTo(typeof(B))`, `typeof(B).IsAssignableFrom(t)`, `t.IsClass`/`IsAbstract`/...,
  combined with `&&`, `||`, `!`. A part that cannot be read widens the filter (warned); other
  libraries' filters are not read at all — every class matches.
- **How they register**: under every interface (`AsImplementedInterfaces()` and equivalents — a
  matching class lives while the scanning code and ANY of its interfaces live), under the interface
  named `I` + class name only (`WithDefaultConventions()`, `FromMatchingInterface`,
  `BindDefaultInterface()`, `WithServiceDefaultInterfaces()`, `AsMatchingInterface()`), or as
  itself only (`AsSelf()`, Autofac's default — the user of such a class names it in code anyway).

So a scanned class whose interface nobody uses any more still dies with it, and a class the filter
excludes stays deletable. Explicitly removing a class a scan registers warns: the container may
have been resolving it (with Autofac the LAST registration of a service wins).

### The exclusion list

Some uses cannot be traced reliably. Code named there is never deleted — it is treated as in use,
and a "kept (not deleted)" warning names it whenever that alone saved it:

1. Razor views (`.cshtml`, `.vbhtml`, `.razor`) — any class or member whose name appears.
2. WebForms markup (`.aspx`, `.ascx`, `.master`, `.asax`) — the same; code-behind classes are roots.
3. Config files (`.config`, `.json`) — any class whose full name appears.
4. Inactive `#if` code — any class or member whose name appears.
5. `dynamic` access — any member with the accessed name.
6. JavaScript (`.js`, minified bundles skipped) — scanned for the removal targets' names (a warning).
7. ASP.NET Web Site projects (no csproj, `App_Code`) — never analyzed or edited; `delete` refuses
   while the solution has one, since what they use elsewhere looks unused
   (`--allow-website-projects` overrides).
8. Generated code (`obj/`, source generators) — never edited.

Matching is by name (3+ characters), so it over-keeps. An explicit removal target is still removed
even when mentioned — with a warning naming where.

## Usage

```bash
dotnet run --project src/DeadCodeCleaner -- analyze --solution path/All.sln \
    --remove Ns.Controllers.AdminController.GetRoleActionList [--remove ...] [--json report.json]

dotnet run --project src/DeadCodeCleaner -- mark   --solution ... --remove ...  # insert "// todo: to be deleted"
dotnet run --project src/DeadCodeCleaner -- delete --solution ... --remove ...  # delete code + markers
dotnet run --project src/DeadCodeCleaner -- find   --solution ... --find Admin  # look up symbol FQNs
```

- `--remove` takes `Namespace.Type` or `Namespace.Type.Member` (all overloads together), or a
  specific overload: `"Namespace.Type.Render(Invoice, int)"` (short type names, as the IDE shows
  them; nullable reference annotations are ignored, so `(string)` matches `(string?)`; `ref`/`out`/
  `in` are part of the signature, so `(ref int)` and `(int)` select different overloads). A generic
  type that shares its name with a non-generic one is selected with an arity suffix:
  ``Ns.Result`2`` for `Result<T1,T2>`. Any other name collision — overloads that differ only by
  generic type parameters included — is reported as ambiguous instead
  of removing both. Removing a type removes all its members and nested types.
- `mark` and `delete` re-run the analysis, so they always operate on current file content; `mark`
  is idempotent, and `delete` removes marker comments along with the code. Whole-file deletions get
  one marker at the top of the file (or the file itself is deleted). `delete` reads and validates
  every file BEFORE touching the disk — whole-file deletions included — and any file that is
  missing or whose decoded text no longer matches what the analysis saw (length AND content hash,
  so a same-length concurrent edit is caught too) aborts the WHOLE delete with nothing changed: the
  verified plan applies in full or not at all. Every write is atomic (temp file in the same
  directory, then a replacing move), so a crash mid-write can never leave a truncated source file.
  A write that fails midway restores the files already edited, and a whole file that cannot be
  deleted (locked, read-only checkout) rolls the entire plan back: the files already deleted and
  every span edit are restored. Exit codes: 0 full success; 1 for every refusal, abort or rollback
  (nothing half-applied); 3 when the source edits applied but a csproj/sln step degraded to a
  "finish manually" warning — scripts never mistake either for success. Case-variant spellings of
  one file (linked files on a case-insensitive filesystem under case-sensitive path rules, e.g.
  WSL `/mnt`) are collapsed to the on-disk spelling, so one file is never edited as two. `mark`
  skips (with a warning) any file — csproj included — whose decoded text no longer matches the
  analysis, any marker whose line begins inside a multi-line string literal (the marker line
  would become part of the string), and any declaration that starts after other code on its line
  (the marker would label that other code; such a deletion also leaves the line break in place, so
  the next line is never joined onto it). Span edits
  are applied before any whole file is deleted. Edits preserve each file's
  encoding (BOMs, UTF-32 included; non-UTF-8 files are decoded with the same legacy code page the
  compiler uses — the system ANSI page, else Windows-1252 — when that round-trips their bytes,
  and byte-preserving via Latin-1 otherwise) and every untouched
  line's own line ending, never swallow a `#region`/`#if` directive whose counterpart survives,
  and never delete a file whole when it carries global usings, assembly attributes, or inactive
  `#if` regions (code the compiler cannot see). Deleting an enum member takes its separator comma;
  deleting a partial method/property deletes BOTH parts; old-style csproj `<Compile>` entries of
  deleted files are removed too (a deletable file in an explicit-listing project whose entry
  cannot be matched textually gets a loud CS2001 warning instead of a silent broken build; an
  SDK-style project with default compile items never needs one, even when it lists a linked file).
  csproj edits get the same safety net as source edits: every csproj to be edited is hashed at
  analysis time and validated, and its new text computed, BEFORE anything touches the disk — a
  stale csproj aborts with nothing changed, a failed write rolls the whole plan back, and a
  `<Compile>` entry that cannot be matched while its source file is planned for deletion aborts up
  front (a dangling entry would break the build). A project planned for removal is validated the
  same way: its csproj must be unchanged, and a C# file that appeared under it since the analysis
  aborts the delete (removing the project would strand it). Only `dotnet sln remove` and the
  removal of a whole project's files stay best-effort (exit code 3 on leftovers). A tracked
  registration nested inside a dying one's arguments or chain is never deleted along with it: the
  dying statement is reported for manual removal instead.
  `delete` refuses to run while verification reports new compile
  errors or was incomplete (a project that could not be rebuilt in memory counts as failed, not as
  clean), while review-flagged tests exist (`--delete-review-tests` overrides the latter), while
  any analyzed project itself has compile errors or any solution project failed to LOAD at all —
  references in them are invisible, so the cascade may over-delete (`--allow-compile-errors`
  overrides both) — and while any removal target is still
  referenced by surviving code, which would break the callers or silently rebind them to another
  overload (`--allow-still-referenced` overrides; deliberately NOT waived by `--no-verify`, which
  only skips the in-memory rebuild), and while no production entry point was detected at all
  (every caller of a target would be deleted with it; `--allow-no-roots` overrides). The
  verification honours each project's REAL warnings-as-errors settings (`TreatWarningsAsErrors`,
  `WarningsAsErrors`, `WarningsNotAsErrors`, read from the csproj and every `Directory.Build.props`
  above it), although the analysis itself neutralizes them to load the solution.
- `--config cfg.json` supports `{ "keep": ["Ns.Something*", ...], "strictMembers": false,
  "inSolutionRegistrations": false }` — `keep` patterns are extra entry points that are never
  orphaned, by any pass (use for reflection-only code). Registration/mapping calls (`AddScoped`,
  `CreateMap`, `RegisterType`, ...) get co-dependent-statement semantics only when the resolved
  method comes from OUTSIDE the solution (or cannot be resolved at all): a domain method that
  happens to share the name keeps plain reference semantics. The same rule applies to the
  framework base classes and interfaces that make roots. `inSolutionRegistrations: true` opts in
  solution-declared wrappers and stand-in frameworks for both.
- By default, public properties, public FIELDS and enum members of in-use types are kept
  (serializers, marshalling and model binding reach them via reflection); `--strict-members`
  disables that. Fields of `[StructLayout]`/`[DataContract]`/`[Serializable]` types are kept in
  EVERY mode, whatever their accessibility — their order and presence IS the marshalled or
  serialized shape.

## Known limitations

- References the compiler cannot see beyond the exclusion list stay invisible: reflection by
  string, `RedirectToAction("X", "Y")` strings, XAML and WCF/handler wiring. A warning is emitted
  when such files exist in the solution; use the config `keep` list for such roots, and treat the
  report as a very strong draft, not gospel. A removal target named in a view, inactive `#if` code,
  config file or script, or accessed through `dynamic`, is removed as requested but warned about;
  a removed package named in a config file is left in place. Files over 1 MB are not scanned (with
  a warning). A constructor deleted while its type stays in use warns (DI or reflection may
  construct through it). A solution in which no production entry points are
  detected warns loudly — everything would count as already orphaned — and `delete` refuses.
  A requested removal inside a type that dies anyway is still listed under the requested removals,
  as "deleted with" that type. On the console, `VERIFY`,
  `STILL REFERENCED` and "registration deleted although still in use" warnings are always printed,
  even past the 100-warning cap.
- Deleting an enum member that is not the last renumbers every later member without an explicit
  `= value`; a warning says so (persisted or serialized numeric values change).
- Declarations in source-generated trees are never edited — the generated text regenerates from
  the surviving sources — and XML doc `cref` references to deleted symbols are not tracked, so
  builds that escalate CS1574 to an error may need a doc-comment pass afterwards.
- A factory lambda inside a DI registration (`AddScoped<IFoo>(sp => new Foo())`) keeps `Foo` in use
  even when the registration dies.
- **The solution is the whole world**: consumers outside it — a NuGet package published from one of
  the projects, another repository referencing the DLLs — are invisible by design. When deleted
  orphans include public API, one aggregate warning says so; add `keep` patterns for any published
  surface before trusting the report.
- Packages declared centrally (`Directory.Packages.props`, `Directory.Build.props`) are not listed
  in the csprojs the tool edits, so they are never removed automatically (over-keep, never
  over-delete).
- A field in a multi-variable declaration (`int a, b;`): when every sibling is orphaned too the
  whole line is deleted; when a sibling is still in use the orphaned field is KEPT in place and a
  "kept in place" warning is emitted (it becomes a pre-existing orphan for future runs). An
  orphaned positional record property (declared by a parameter) is kept in place the same way.
- A csproj element sharing a line with anything else (a sibling element, the ItemGroup tags) is
  never edited — a warning asks for manual removal, since the edits are line-granular. A file
  declaring a type whose full name another project also declares is never deleted whole.
- `packages.config` projects get no package cleanup (a warning says so). Multi-targeting projects
  are analyzed for their first target framework only (warned). On Windows, run against net47
  solutions with Visual Studio MSBuild installed; `MSBuildLocator` finds it automatically.

## Tests

`tests/run-fixture-tests.sh` runs known-answer checks against `tests/Fixture` (141 checks covering
MediatR, AutoMapper (incl. `ForMember` chains), DI (generic and `typeof` forms), test poisoning,
broken-test isolation, pre-existing orphans, member/overload-level removal, project-reference
cleanup, chain-segment deletion, override and partial-method survival, enum-member removal,
collection-initializer, deconstruction, enum-member-attribute and assembly-attribute edges,
primary-constructor and primary-base-call safety, inactive `#if` protection and its mention
warning, the still-referenced warning, the dynamic-access warning, the enum-renumber warning,
chained generic type arguments living and dying with their map, attribute-constructor survival,
the in-memory verification, the review-test delete refusal,
mark idempotency, two delete round-trips that must rebuild, the container-escalation gate for
test-live classes, test-project classification (test library AND a test, never the name),
inherited-test fixtures, assembly-attribute reference needs, the `AsImplementedInterfaces` cascade
with `IDisposable` excluded, keep patterns protecting test code, member names of deleted types in
views, empty profiles kept for external writes, chained in-solution mapping extensions, mark skipping
markers inside multi-line strings, in-solution `Profile` base classes not counting as roots, an
exact-name keep on a test class, and — against EXTERNAL stub assemblies — Global.asax/`Page_Load`/
`PreApplicationStartMethod`/EF6-configuration roots, Autofac `typeof`/unknown-call registrations, a
side-effecting field initializer, a base-module helper blocking the empty-container pass,
warnings-as-errors verification, mid-line deletions before `#if`, per-assembly namespace
liveness for `using` removal, Autofac assembly scanning with a name filter, the framework-built
orphan review and its delete refusal, the exclusion list for `dynamic` and inactive `#if` names,
the Web Site project refusal, and no false `<Compile>` warning in SDK projects).
