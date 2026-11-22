#!/usr/bin/env bash
# Known-answer regression tests for DeadCodeCleaner, run against tests/Fixture.
# Usage: tests/run-fixture-tests.sh
set -u
cd "$(dirname "$0")/.."

REPORT=$(mktemp)
SCRATCH=""
SCRATCH2=""
trap 'rm -rf "$SCRATCH" "$SCRATCH2"; rm -f "$REPORT"' EXIT
FAIL=0

check() { # check <description> <jq filter that must output true>
    local desc="$1" filter="$2"
    local result
    result=$(jq -e "$filter" "$REPORT" > /dev/null 2>&1 && echo ok || echo FAIL)
    if [ "$result" = "FAIL" ]; then FAIL=1; fi
    printf '  %-4s %s\n' "$result" "$desc"
}
pass() { printf '  ok   %s\n' "$1"; }
failx() { printf '  FAIL %s\n' "$1"; FAIL=1; }

echo "== Case 1: remove WidgetsController (type-level, full cascade)"
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Web.WidgetsController --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

orph='[.symbols[] | select(.category=="orphaned") | .fqn]'
check "WidgetService orphaned"            "$orph | index(\"Fixture.Domain.WidgetService\") != null"
check "IWidgetService orphaned"           "$orph | index(\"Fixture.Domain.IWidgetService\") != null"
check "MediatR handler orphaned"          "$orph | index(\"Fixture.Domain.GetWidgetHandler\") != null"
check "query orphaned"                    "$orph | index(\"Fixture.Domain.GetWidgetQuery\") != null"
check "repository cascade orphaned"       "$orph | index(\"Fixture.Domain.WidgetRepository\") != null"
check "entity cascade orphaned"           "$orph | index(\"Fixture.Domain.WidgetEntity\") != null"
check "dto orphaned"                      "$orph | index(\"Fixture.Domain.WidgetDto\") != null"
check "SecondaryHelper orphaned"          "$orph | index(\"Fixture.Web.SecondaryHelper\") != null"
check "SharedService NOT orphaned"        "$orph | index(\"Fixture.Domain.SharedService\") == null"
check "GadgetService NOT orphaned"        "$orph | index(\"Fixture.Domain.GadgetService\") == null"
check "pre-existing DeadLegacyHelper NOT reported" \
    "[.symbols[].fqn] | index(\"Fixture.Domain.DeadLegacyHelper\") == null"
check "pre-existing orphan referencing deleted code IS deleted" \
    '[.symbols[] | select(.category=="broken-orphan") | .fqn] | index("Fixture.Domain.DeadWidgetAdmirer") != null'
check "shared-declaration field KEPT (sibling in use)" \
    '[.symbols[].fqn] | index("Fixture.Domain.SharedService.WidgetCounter") == null'
check "kept-field warning emitted" \
    '[.warnings[]] | map(select(contains("WidgetCounter"))) | length == 1'
check "other shared field NOT orphaned" \
    '[.symbols[].fqn] | index("Fixture.Domain.SharedService.GadgetCounter") == null'
check "fully-dead shared declaration deleted (whole line)" \
    '[.symbols[] | select(.fqn=="Fixture.Domain.SharedService.WidgetOnlyA") | .parts | length] | index(1) != null'
check "broken test class deleted" \
    '[.symbols[] | select(.category=="broken-test") | .fqn] | index("Fixture.Tests.WidgetsControllerTests") != null'
check "mixed-reference test flagged for review" \
    '[.symbols[] | select(.fqn=="Fixture.Tests.WidgetServiceTests") | .review] | index(true) != null'
check "test helper cascade" \
    '[.symbols[] | select(.category=="orphaned-test-helper") | .fqn] | index("Fixture.Tests.TestData") != null'
check "surviving test untouched" \
    '[.symbols[].fqn] | index("Fixture.Tests.SharedServiceTests") == null'
check "pre-existing dead test util untouched" \
    '[.symbols[].fqn] | index("Fixture.Tests.UnusedTestUtil") == null'
check "CreateMap statement reported" \
    '[.statements[] | select(.kind=="AutoMapper.CreateMap") | .snippet] | map(select(contains("WidgetEntity"))) | length == 1'
check "gadget CreateMap NOT reported" \
    '[.statements[].snippet] | map(select(contains("GadgetEntity"))) | length == 0'
check "DI registration reported" \
    '[.statements[] | select(.kind=="DI.Register") | .snippet] | map(select(contains("IWidgetService"))) | length == 2'
check "chained registration is segment-only, typeof registration whole" \
    '[.statements[] | select(.snippet|contains("IWidgetService")) | .wholeStatement] == [false, true]'
check "typeof registration reported dead (not pinned by the bound tree)" \
    '[.statements[].snippet] | map(select(contains("typeof(IWidgetService)"))) | length == 1'
check "Widgets.cs deletable in full" \
    '[.deletableFiles[]] | map(select(endswith("Widgets.cs"))) | length == 1'
check "Controllers.cs NOT deletable" \
    '[.deletableFiles[]] | map(select(endswith("Controllers.cs"))) | length == 0'
check "overridden base member survives (override still compiles)" \
    '[.symbols[].fqn] | index("Fixture.Domain.ShapeBase.Render") == null'
check "partial method deleted with BOTH parts" \
    '[.symbols[] | select(.fqn=="Fixture.Domain.WidgetFormatter.FormatWidget") | .parts | length] == [2]'
check "partial type itself survives (other member in use)" \
    '[.symbols[].fqn] | index("Fixture.Domain.WidgetFormatter") == null'
check "collection-initializer Add survives (bound-tree edge)" \
    '[.symbols[].fqn] | index("Fixture.Domain.GadgetBag.Add") == null'
check "assembly-attribute class survives" \
    '[.symbols[].fqn] | index("Fixture.Domain.FixtureMarkerAttribute") == null'
check "WidgetExtra orphaned (inactive-region trap armed)" \
    "$orph | index(\"Fixture.Domain.WidgetExtra\") != null"
check "file with inactive #if region NOT deletable" \
    '[.deletableFiles[]] | map(select(endswith("Conditional.cs"))) | length == 0'
check "inactive-region file-kept warning emitted" \
    '[.warnings[]] | map(select(contains("file kept (contains inactive"))) | length == 1'
check "deleted symbol mentioned in inactive #if code warns" \
    '[.warnings[]] | map(select(contains("mentioned in inactive #if"))) | length >= 1'
check "record with orphaned primary ctor survives whole" \
    '[.symbols[].fqn] | map(select(startswith("Fixture.Domain.WidgetTag"))) | length == 0'
check "base ctor called via primary base call survives" \
    '[.symbols[].fqn] | map(select(startswith("Fixture.Domain.TagHolder"))) | length == 0'
check "attribute used on enum member survives" \
    '[.symbols[].fqn] | index("Fixture.Domain.EnumNoteAttribute") == null'
check "internal Deconstruct survives (pattern member kept with type)" \
    '[.symbols[].fqn] | index("Fixture.Domain.GadgetDto.Deconstruct") == null'
check "broken test method deleted from mixed class" \
    '[.symbols[] | select(.category=="broken-test") | .fqn] | index("Fixture.Tests.MixedTests.GetWidget_when_controller_is_removed_then_this_test_breaks") != null'
check "healthy sibling test and its class survive" \
    '[.symbols[].fqn] | map(select(startswith("Fixture.Tests.MixedTests"))) | length == 1'
check "verification clean (plan introduces no compile errors)" \
    '[.warnings[] | select(startswith("VERIFY"))] | length == 0'

echo "== Case 2: remove GetSecondary (member-level, narrow cascade)"
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Web.WidgetsController.GetSecondary --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

check "SecondaryHelper orphaned"          "$orph | index(\"Fixture.Web.SecondaryHelper\") != null"
check "OverloadHelper orphaned too (all overloads removed)" \
    "$orph | index(\"Fixture.Web.OverloadHelper\") != null"
check "WidgetService NOT orphaned"        "$orph | index(\"Fixture.Domain.WidgetService\") == null"
check "handler NOT orphaned"              "$orph | index(\"Fixture.Domain.GetWidgetHandler\") == null"
check "controller itself only in removed" \
    '[.symbols[] | select(.category=="removed") | .fqn] | unique == ["Fixture.Web.WidgetsController.GetSecondary"]'

echo "== Case 3: remove GetSecondary(int) only (signature-targeted overload)"
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution tests/Fixture/Fixture.sln \
    --remove "Fixture.Web.WidgetsController.GetSecondary(int)" --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

check "OverloadHelper orphaned"           "$orph | index(\"Fixture.Web.OverloadHelper\") != null"
check "SecondaryHelper NOT orphaned (other overload survives)" \
    "$orph | index(\"Fixture.Web.SecondaryHelper\") == null"
check "exactly one removal"               '[.symbols[] | select(.category=="removed")] | length == 1'

echo "== Case 4: remove BOTH controllers (empty profile + project reference cleanup)"
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Web.WidgetsController --remove Fixture.Web.GadgetsController \
    --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

check "empty mapping profile deleted"     "$orph | index(\"Fixture.Domain.MappingProfile\") != null"
check "SharedService orphaned (no users left)" \
    "$orph | index(\"Fixture.Domain.SharedService\") != null"
check "Web->Domain project reference removable" \
    '[.projectRefRemovals[] | select(.project=="Fixture.Web") | .reference] | index("Fixture.Domain") != null'
check "Tests->Web project reference removable" \
    '[.projectRefRemovals[] | select(.project=="Fixture.Tests") | .reference] | index("Fixture.Web") != null'
check "no NuGet removals (fixture has none)" '.packageRefRemovals | length == 0'
check "no whole project deletable (pre-existing orphans remain)" '.deletableProjects | length == 0'
check "DeadLegacyHelper still untouched"  '[.symbols[].fqn] | index("Fixture.Domain.DeadLegacyHelper") == null'

echo "== Case 5: mark idempotency and delete round-trip (scratch copy)"
SCRATCH=$(mktemp -d)
cp -r tests/Fixture "$SCRATCH/Fixture"
dotnet restore "$SCRATCH/Fixture/Fixture.sln" > /dev/null 2>&1
snapshot() {
    find "$SCRATCH/Fixture" \( -name '*.cs' -o -name '*.csproj' -o -name '*.sln' \) \
        -not -path '*/obj/*' -not -path '*/bin/*' -print0 | sort -z | xargs -0 md5sum
}
dotnet run --project src/DeadCodeCleaner -- mark --solution "$SCRATCH/Fixture/Fixture.sln" \
    --remove Fixture.Web.WidgetsController --json "$REPORT" > /dev/null 2>&1 || { echo "MARK FAILED"; exit 1; }
S1=$(snapshot)
if grep -q "todo: to be deleted" "$SCRATCH/Fixture/Fixture.Web/Controllers.cs"; then
    pass "markers inserted"; else failx "markers inserted"; fi
dotnet run --project src/DeadCodeCleaner -- mark --solution "$SCRATCH/Fixture/Fixture.sln" \
    --remove Fixture.Web.WidgetsController --json "$REPORT" > /dev/null 2>&1 || { echo "SECOND MARK FAILED"; exit 1; }
S2=$(snapshot)
if [ "$S1" = "$S2" ]; then
    pass "mark is idempotent (second run changes nothing)"; else failx "mark is idempotent (second run changes nothing)"; fi
if dotnet run --project src/DeadCodeCleaner -- delete --solution "$SCRATCH/Fixture/Fixture.sln" \
    --remove Fixture.Web.WidgetsController --json "$REPORT" > /dev/null 2>&1; then
    failx "delete refuses while review-flagged tests exist"; else pass "delete refuses while review-flagged tests exist"; fi
if [ -f "$SCRATCH/Fixture/Fixture.Domain/Widgets.cs" ]; then
    pass "refusal left all files in place"; else failx "refusal left all files in place"; fi
dotnet run --project src/DeadCodeCleaner -- delete --solution "$SCRATCH/Fixture/Fixture.sln" \
    --remove Fixture.Web.WidgetsController --delete-review-tests --json "$REPORT" > /dev/null 2>&1 || { echo "DELETE FAILED"; exit 1; }
if ! grep -rq --include='*.cs' --include='*.csproj' "todo: to be deleted" "$SCRATCH/Fixture"; then
    pass "no markers left after delete"; else failx "no markers left after delete"; fi
if [ ! -f "$SCRATCH/Fixture/Fixture.Domain/Widgets.cs" ]; then
    pass "Widgets.cs deleted"; else failx "Widgets.cs deleted"; fi
if ! grep -q "IWidgetService" "$SCRATCH/Fixture/Fixture.Web/Program.cs"; then
    pass "dead registration segment removed"; else failx "dead registration segment removed"; fi
if grep -q "services.AddFixtureLogging();" "$SCRATCH/Fixture/Fixture.Web/Program.cs"; then
    pass "untracked chained call survives delete"; else failx "untracked chained call survives delete"; fi
if dotnet build "$SCRATCH/Fixture/Fixture.sln" > /dev/null 2>&1; then
    pass "solution builds after delete"; else failx "solution builds after delete"; fi

echo "== Case 6: removing a target that surviving code still uses warns loudly"
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Domain.SharedService --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

check "STILL REFERENCED warning emitted" \
    '[.warnings[]] | map(select(startswith("STILL REFERENCED"))) | length >= 1'
check "warning names a surviving referrer" \
    '[.warnings[] | select(startswith("STILL REFERENCED"))] | map(select(contains("GadgetsController") or contains("GetGadget"))) | length >= 1'
check "verification catches the broken plan" \
    '[.warnings[] | select(startswith("VERIFY"))] | length >= 1'

echo "== Case 7: removing an implementation kills its registration; enum member deletes cleanly"
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Domain.WidgetService --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

check "registrations reported dead although their service survives" \
    '[.statements[] | select(.snippet|contains("IWidgetService"))] | length == 2'
check "deleted-but-in-use registration warnings emitted" \
    '[.warnings[]] | map(select(startswith("registration deleted although still in use"))) | length == 2'

SCRATCH2=$(mktemp -d)
cp -r tests/Fixture "$SCRATCH2/Fixture"
dotnet restore "$SCRATCH2/Fixture/Fixture.sln" > /dev/null 2>&1
dotnet run --project src/DeadCodeCleaner -- delete --solution "$SCRATCH2/Fixture/Fixture.sln" \
    --remove Fixture.Domain.WidgetService --remove Fixture.Domain.WidgetKind.Fancy \
    --delete-review-tests --json "$REPORT" > /dev/null 2>&1 || { echo "DELETE FAILED (verification rejected the plan?)"; exit 1; }
if ! grep -q "    Fancy" "$SCRATCH2/Fixture/Fixture.Domain/Shapes.cs"; then
    pass "enum member deleted"; else failx "enum member deleted"; fi
if grep -q "Legacy," "$SCRATCH2/Fixture/Fixture.Domain/Shapes.cs"; then
    pass "following enum member intact"; else failx "following enum member intact"; fi
if ! grep -q "AddScoped<IWidgetService" "$SCRATCH2/Fixture/Fixture.Web/Program.cs"; then
    pass "registration removed with its implementation"; else failx "registration removed with its implementation"; fi
if grep -q "AddFixtureLogging" "$SCRATCH2/Fixture/Fixture.Web/Program.cs"; then
    pass "chained untracked call survives"; else failx "chained untracked call survives"; fi
if dotnet build "$SCRATCH2/Fixture/Fixture.sln" > /dev/null 2>&1; then
    pass "solution builds after delete"; else failx "solution builds after delete"; fi

if [ "$FAIL" = 1 ]; then echo "RESULT: FAILURES PRESENT"; exit 1; fi
echo "RESULT: all checks passed"
