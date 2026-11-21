## Code

- No `Async` suffix on async method names. `GetInstalledModels`, not `GetInstalledModelsAsync`.
- Every method has a braced body. Never use expression-bodied methods (`=>`).
- Keep statements on one line, unless the line is genuinely long (roughly over 150-170 characters); past that point, split it structurally. Method signatures stay on one line. A log call stays on one line up to a message of roughly 300-400 characters; past that, split it into multiple log calls.
- Compare strings with `==` and `!=`. Never `string.Equals`, and no `ToLower`/`ToUpper` unless genuinely necessary.
- Don't use `string.Compare`/`string.CompareOrdinal` unless a case genuinely requires it.
- Every enum declares `Unknown` first, so the default value is never a real state.
- Don't inline a non-trivial or `await`ed call into a `return` — assign it to a variable first. Do return a simple synchronous expression (member/null-conditional chain, interpolation, comparison, trivial call like `FirstOrDefault()`) directly, never through a throwaway variable.
- Only NuGet packages compatible with .NET 9.0.
- Prefer `GetFromJsonAsync`/`ReadFromJsonAsync` over reading streams and calling `JsonSerializer` by hand.
- Deserialize JSON into typed models (`GetFromJsonAsync`/`JsonSerializer.Deserialize<T>`); never navigate a `JsonDocument`/`JsonNode`/`JsonElement` by hand.
- Keep methods stateless: return a value rather than mutating another object, and pass what a method needs explicitly.
- Never use reflection.
- Private helpers stay instance methods, never `static`, unless the whole class is `static`.
- Every hardcoded line or constant lives in the project's `Constants` file, except genuinely configurable values, which live in `appsettings.json`.
- Do not enable nullable reference types (`<Nullable>enable</Nullable>`). Leave it off in every project.
- Catch only the exceptions you can describe accurately. No catch-all handlers outside `Program.Main`.

## Testing

- One test class per type under test, named `<TypeName>Tests`.
- Test names follow `<MemberUnderTest>_when_<condition>_then_<outcome>`, with the condition and outcome in snake_case.
- Every test body is split into `// Arrange`, `// Act`, `// Assert` sections, in that order.
- The system under test is the object the Act section invokes the action on, and is always named `sut` (or `_sut` if it's a shared system under test; then it's initialised in the test-class constructor).
- Share via a test-class field and constructor only what more than one test uses.
- Expected values are `expected*` variables declared in Arrange; results captured in Act are `actual*`.
- No low-value tests: nothing that restates the implementation, asserts constants, or duplicates existing coverage.

## Workflow

- Build for substantial changes only: a new class, a refactor across several methods or files, or a behaviour change worth verifying.
- Commit all pending changes in one commit, even unrelated ones, unless told otherwise.

## Documents

- Plan and design documents hold decisions and actions only. Rationale belongs in the conversation.
- Do not describe things that can be read from the code. Descriptions go stale; conventions do not.
- Promote anything durable from a plan-mode plan into the spec, an ADR, or a ticket.
