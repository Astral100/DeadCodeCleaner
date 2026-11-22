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
   `Startup`/`Program`, AutoMapper profiles, Autofac modules, plus anything matched by the config's
   `keep` patterns.
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
  they do not keep their types in use, but die when their types die — and are then reported as
  individual statements to remove. The whole statement is deleted only when it is a plain expression
  statement whose entire call chain belongs to dead registrations (AutoMapper's `ForMember`/
  `ReverseMap` and Autofac's lifetime calls count as part of theirs); otherwise only the dead call's
  chain segment is removed (`services.AddScoped<IA,A>().AddLogging()` keeps `AddLogging`, and
  `return services.AddScoped<IA,A>();` keeps the `return`). A registration that is the body of a
  braceless `if`/loop takes the whole construct with it — unless the construct's condition or
  header has side effects (a call, assignment, or increment), which, like an if/else branch or a
  lambda, is reported as a warning for manual removal instead. References inside a map's `ForMember` lambdas
  belong to the statement, not the profile, so they cannot keep the mapped types alive forever. A
  statement whose text references deleted code dies with it in every case: removing an
  implementation type takes its registration along even while the service interface stays in use
  (with a warning, since behavior changes).
- **Empty containers**: an AutoMapper profile or Autofac module whose every mapping/registration
  died (and that nothing surviving references) is deleted whole — unless a config `keep` pattern
  matches it, or any of its members still calls into an external assembly beyond a small benign
  list: an untracked registration (`RegisterInstance`, `Register(c => ...)`, assembly scanning)
  leaves no graph edge, and deleting the container would silently drop it. Chained generic type
  arguments (`ConvertUsing<TConverter>()`, `Include<TDerived, TOther>()`) are statement references
  too: they stay alive while the map lives and die with it.
- **Build plumbing cascades too**: a NuGet `PackageReference` or `ProjectReference` whose last
  surviving user was deleted is removed from the csproj (entries under a `Condition` are left in
  place with a warning — only one build configuration is analyzed); `using` directives that no
  assembly can satisfy any more are removed (matched under their RESOLVED names, so `using Sub;`
  inside `namespace A` means `A.Sub`; a file banner above the first using is never deleted with
  it); a project whose every declaration is deleted is removed from the
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

Test projects (by name, direct test-framework package references, or `<IsTestProject>`) never keep
production code in use. When production code is deleted, any test referencing it is deleted too
("broken-test"), test helpers used only by those tests cascade ("orphaned-test-helper"), and a
deleted test that *also* touches surviving production code is flagged `[REVIEW]`. One broken test
never takes its healthy siblings along: the class is deleted only when every test in it died. Code
in a project the heuristics classify as test (e.g. "TestCommon") that production actually uses is
treated as production.

`delete` REFUSES to run while any `[REVIEW]` test exists, because whether such a test should be
salvaged is a judgment call: run `analyze` or `mark` first, let a human or an LLM rewrite the
flagged tests to drop the deleted references and keep the assertions that still matter (the tool
re-analyzes on every run, so adjusted tests simply survive), then run `delete` again. Pass
`--delete-review-tests` to delete them as-is instead.

### Pre-existing orphans that reference deleted code

Code that was already orphaned before the removal stays untouched — unless it references newly
deleted code, in which case it would no longer compile and is deleted too ("broken-orphan"
category), cascading transitively. A dead member of a dead type escalates to the whole type, so no
half-deleted class is left violating an interface contract.

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
  missing or whose decoded text length no longer matches what the analysis saw (encoding mismatch,
  concurrent edit) aborts the WHOLE delete with nothing changed: the verified plan applies in full
  or not at all. A write that fails midway restores the files already edited. Span edits are
  applied before any whole file is deleted. Edits preserve each file's
  encoding (BOMs, UTF-32 included; non-UTF-8 files are handled byte-preserving via Latin-1) and every untouched
  line's own line ending, never swallow a `#region`/`#if` directive whose counterpart survives,
  and never delete a file whole when it carries global usings, assembly attributes, or inactive
  `#if` regions (code the compiler cannot see). Deleting an enum member takes its separator comma;
  deleting a partial method/property deletes BOTH parts; old-style csproj `<Compile>` entries of
  deleted files are removed too. `delete` refuses to run while verification reports new compile
  errors, while review-flagged tests exist (`--delete-review-tests` overrides the latter), while
  any analyzed project itself has compile errors — references in it are invisible, so the cascade
  may over-delete (`--allow-compile-errors` overrides) — and while any removal target is still
  referenced by surviving code, which would break the callers or silently rebind them to another
  overload (`--no-verify` overrides).
- `--config cfg.json` supports `{ "keep": ["Ns.Something*", ...], "strictMembers": false }` —
  `keep` patterns are extra entry points that are never orphaned, by any pass (use for
  reflection-only code).
- By default, public properties and enum members of in-use types are kept (serializers and model
  binding reach them via reflection); `--strict-members` disables that.

## Known limitations

- References the compiler cannot see are invisible: reflection by string, routes referenced from
  JavaScript, `web.config`/XML wiring, `RedirectToAction("X", "Y")` strings, Razor views, XAML and
  WCF/handler wiring. A warning
  is emitted when such files exist in the solution. Use the config `keep` list for such roots, and
  treat the report as a very strong draft, not gospel. Code inside inactive `#if` regions is
  equally invisible (to the analysis AND the verifier, which compiles with the same symbols): when
  a deleted symbol's name still appears in such a region, a warning names it.
- Runtime-only references get targeted warnings, since neither the analysis nor the verifier can
  decide them: a deleted member whose name is accessed through `dynamic` anywhere; a deleted type
  or removed package whose name appears in a `.config`/`.json` file under a project directory
  (such packages are left in place; a config file over 1 MB is not scanned, with a warning); DI
  registration by assembly scanning (`RegisterAssemblyTypes`,
  `Scan`, ...) anywhere in the solution; and a constructor deleted while its type stays in use
  (DI or reflection may construct through it). A solution in which no production entry points are
  detected warns loudly — everything would count as already orphaned. On the console, `VERIFY` and
  `STILL REFERENCED` warnings are always printed, even past the 100-warning cap.
- Deleting an enum member that is not the last renumbers every later member without an explicit
  `= value`; a warning says so (persisted or serialized numeric values change).
- Declarations in source-generated trees are never edited — the generated text regenerates from
  the surviving sources — and XML doc `cref` references to deleted symbols are not tracked, so
  builds that escalate CS1574 to an error may need a doc-comment pass afterwards.
- A factory lambda inside a DI registration (`AddScoped<IFoo>(sp => new Foo())`) keeps `Foo` in use
  even when the registration dies.
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

`tests/run-fixture-tests.sh` runs known-answer checks against `tests/Fixture` (83 checks covering
MediatR, AutoMapper (incl. `ForMember` chains), DI (generic and `typeof` forms), test poisoning,
broken-test isolation, pre-existing orphans, member/overload-level removal, project-reference
cleanup, chain-segment deletion, override and partial-method survival, enum-member removal,
collection-initializer, deconstruction, enum-member-attribute and assembly-attribute edges,
primary-constructor and primary-base-call safety, inactive `#if` protection and its mention
warning, the still-referenced warning, the dynamic-access warning, the enum-renumber warning,
chained generic type arguments living and dying with their map, attribute-constructor survival,
the in-memory verification, the review-test delete refusal,
mark idempotency, and two delete round-trips that must rebuild).
