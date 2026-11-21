# DeadCodeCleaner

Deterministic cascade dead-code analyzer for .NET solutions. Given one or more symbols you intend
to remove (a controller, an action method, a service — any type or member), it computes **everything
that becomes orphaned as a result**, transitively, in one pass — while leaving code that was
*already* orphaned before the removal untouched.

## How it works

1. The whole solution is loaded with Roslyn and turned into a dependency graph: every type/member
   declared in source is a node; every semantic reference is an edge. Nodes are keyed by
   documentation-comment id, so a symbol seen from source and through a project-reference DLL is
   the same node.
2. Entry points ("roots") are detected: `Main`, controllers (name or base class), hosted services,
   `Startup`/`Program`, AutoMapper profiles, Autofac modules, plus anything matched by the config's
   `keep` patterns.
3. Reachability ("in use") is computed twice: from all roots, and from all roots with the removal
   targets treated as deleted. The difference is the complete cascade. Code unreachable in the
   baseline is pre-existing dead code and is never reported.

### Indirect dependencies

- **MediatR / FluentValidation**: a handler/validator is in use iff its request/subject type is in
  use (matched by implementing `IRequestHandler<,>`, `INotificationHandler<>`, `AbstractValidator<>`
  etc.). While a handler exists, the binder interface's members (`Handle`) count as used.
- **AutoMapper `CreateMap<A,B>()`** and **DI registrations** (`AddScoped/AddTransient/AddSingleton`,
  Autofac `RegisterType<T>().As<I>()` chains, `typeof` overloads) are *co-dependent statements*:
  they do not keep their types in use, but die when their types die — and are then reported as
  individual lines to remove.
- **Empty containers**: an AutoMapper profile or Autofac module whose every mapping/registration
  died (and that nothing surviving references) is deleted whole.
- **Build plumbing cascades too**: a NuGet `PackageReference` or `ProjectReference` whose last
  surviving user was deleted is removed from the csproj; `using` directives that no assembly can
  satisfy any more are removed; a project whose every declaration is deleted is removed from the
  solution and its csproj deleted (directory too, when nothing but code remained). Transitive
  project-reference reachability is respected — a reference is kept when surviving code reaches
  another project only through it. "Already unused before" references stay, like symbols.

### Tests

Test projects (by name, direct test-framework package references, or `<IsTestProject>`) never keep
production code in use. When production code is deleted, any test referencing it is deleted too
("broken-test"), test helpers used only by those tests cascade ("orphaned-test-helper"), and a
deleted test that *also* touches surviving production code is flagged `[REVIEW]`.

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
  them). Removing a type removes all its members and nested types.
- `mark` and `delete` re-run the analysis, so they always operate on current file content; `delete`
  removes marker comments along with the code. Whole-file deletions get one marker at the top of
  the file (or the file itself is deleted).
- `--config cfg.json` supports `{ "keep": ["Ns.Something*", ...], "strictMembers": false }` —
  `keep` patterns are extra entry points that are never orphaned (use for reflection-only code).
- By default, public properties and enum members of in-use types are kept (serializers and model
  binding reach them via reflection); `--strict-members` disables that.

## Known limitations

- References the compiler cannot see are invisible: reflection by string, routes referenced from
  JavaScript, `web.config`/XML wiring, `RedirectToAction("X", "Y")` strings, Razor views. Use the
  config `keep` list for such roots, and treat the report as a very strong draft, not gospel.
- A factory lambda inside a DI registration (`AddScoped<IFoo>(sp => new Foo())`) keeps `Foo` in use
  even when the registration dies.
- A field in a multi-variable declaration (`int a, b;`): when every sibling is orphaned too the
  whole line is deleted; when a sibling is still in use the orphaned field is KEPT in place and a
  "kept in place" warning is emitted (it becomes a pre-existing orphan for future runs).
- Old-style (.NET Framework) csproj files list files explicitly: after whole-file deletion you must
  also remove the `<Compile>` entry (SDK-style projects need nothing). On Windows, run against
  net47 solutions with Visual Studio MSBuild installed; `MSBuildLocator` finds it automatically.

## Tests

`tests/run-fixture-tests.sh` runs known-answer checks against `tests/Fixture` (25 assertions
covering MediatR, AutoMapper, DI, test poisoning, pre-existing orphans, member-level removal).
