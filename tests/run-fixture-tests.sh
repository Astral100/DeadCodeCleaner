#!/usr/bin/env bash
# Known-answer regression tests for DeadCodeCleaner, run against tests/Fixture.
# Usage: tests/run-fixture-tests.sh
set -u
cd "$(dirname "$0")/.."

REPORT=$(mktemp)
SCRATCH=""
SCRATCH2=""
SCRATCH3=""
SCRATCH4=""
SCRATCH5=""
SCRATCH6=""
SCRATCH7=""
SCRATCH8=""
SCRATCH9=""
SCRATCH10=""
SCRATCH11=""
SCRATCH12=""
KEEPCFG=$(mktemp)
trap 'rm -rf "$SCRATCH" "$SCRATCH2" "$SCRATCH3" "$SCRATCH4" "$SCRATCH5" "$SCRATCH6" "$SCRATCH7" "$SCRATCH8" "$SCRATCH9" "$SCRATCH10" "$SCRATCH11" "$SCRATCH12"; rm -f "$REPORT" "$KEEPCFG"' EXIT
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
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
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
check "public field kept with its in-use type" \
    '[.symbols[].fqn] | index("Fixture.Domain.SharedService.WidgetPublicTally") == null'
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
check "class named in inactive #if code is KEPT (exclusion list), with a warning" \
    '([.symbols[].fqn] | map(select(startswith("Fixture.Domain.InactiveUse"))) | length == 0)
     and ([.warnings[]] | map(select(startswith("kept (not deleted): Fixture.Domain.InactiveUse") and contains("inactive #if"))) | length >= 1)'
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
check "member accessed by name through dynamic is KEPT (exclusion list), with a warning" \
    '([.symbols[].fqn] | map(select(startswith("Fixture.Domain.DynamicTarget"))) | length == 0)
     and ([.warnings[]] | map(select(startswith("kept (not deleted): Fixture.Domain.DynamicTarget.Summarize") and contains("dynamic"))) | length >= 1)'
check "attribute ctor survives via surviving method's attribute" \
    '[.symbols[] | select(.fqn == "Fixture.Domain.AuditAttribute..ctor")] | length == 0'
check "chained generic converter NOT orphaned (its map survives)" \
    '[.symbols[].fqn] | map(select(contains("GadgetConverter"))) | length == 0'
check "class with only INHERITED tests is a test class (broken-test, never broken-orphan)" \
    '[.symbols[] | select(.fqn | startswith("Fixture.Tests.WidgetCheckTests")) | .category] | (length >= 1 and all(. == "broken-test"))'
check "base fixture with the test survives" \
    '[.symbols[].fqn] | map(select(startswith("Fixture.Tests.WidgetCheckBase"))) | length == 0'
check "view mentioning an ACTION of a wholly-deleted controller warns" \
    '[.warnings[]] | map(select(contains("WidgetsController.GetWidgetBadge is mentioned in") and contains("Index.cshtml"))) | length == 1'

echo "== Case 2: remove GetSecondary (member-level, narrow cascade)"
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
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
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution tests/Fixture/Fixture.sln \
    --remove "Fixture.Web.WidgetsController.GetSecondary(int)" --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

check "OverloadHelper orphaned"           "$orph | index(\"Fixture.Web.OverloadHelper\") != null"
check "SecondaryHelper NOT orphaned (other overload survives)" \
    "$orph | index(\"Fixture.Web.SecondaryHelper\") == null"
check "exactly one removal"               '[.symbols[] | select(.category=="removed")] | length == 1'

echo "== Case 4: remove BOTH controllers (empty profile + project reference cleanup)"
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
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
check "chained generic converter dies with its mapping" \
    "$orph | index(\"Fixture.Domain.GadgetConverter\") != null"

echo "== Case 5: mark idempotency and delete round-trip (scratch copy)"
SCRATCH=$(mktemp -d)
cp -r tests/Fixture "$SCRATCH/Fixture"
dotnet restore "$SCRATCH/Fixture/Fixture.sln" > /dev/null 2>&1
snapshot() {
    find "$SCRATCH/Fixture" \( -name '*.cs' -o -name '*.csproj' -o -name '*.sln' \) \
        -not -path '*/obj/*' -not -path '*/bin/*' -print0 | sort -z | xargs -0 md5sum
}
dotnet run --project src/DeadCodeCleaner -- mark --config tests/Fixture/dcc-config.json --solution "$SCRATCH/Fixture/Fixture.sln" \
    --remove Fixture.Web.WidgetsController --json "$REPORT" > /dev/null 2>&1 || { echo "MARK FAILED"; exit 1; }
S1=$(snapshot)
if grep -q "todo: to be deleted" "$SCRATCH/Fixture/Fixture.Web/Controllers.cs"; then
    pass "markers inserted"; else failx "markers inserted"; fi
dotnet run --project src/DeadCodeCleaner -- mark --config tests/Fixture/dcc-config.json --solution "$SCRATCH/Fixture/Fixture.sln" \
    --remove Fixture.Web.WidgetsController --json "$REPORT" > /dev/null 2>&1 || { echo "SECOND MARK FAILED"; exit 1; }
S2=$(snapshot)
if [ "$S1" = "$S2" ]; then
    pass "mark is idempotent (second run changes nothing)"; else failx "mark is idempotent (second run changes nothing)"; fi
if dotnet run --project src/DeadCodeCleaner -- delete --config tests/Fixture/dcc-config.json --solution "$SCRATCH/Fixture/Fixture.sln" \
    --remove Fixture.Web.WidgetsController --json "$REPORT" > /dev/null 2>&1; then
    failx "delete refuses while review-flagged tests exist"; else pass "delete refuses while review-flagged tests exist"; fi
if [ -f "$SCRATCH/Fixture/Fixture.Domain/Widgets.cs" ]; then
    pass "refusal left all files in place"; else failx "refusal left all files in place"; fi
dotnet run --project src/DeadCodeCleaner -- delete --config tests/Fixture/dcc-config.json --solution "$SCRATCH/Fixture/Fixture.sln" \
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
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Domain.SharedService --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

check "STILL REFERENCED warning emitted" \
    '[.warnings[]] | map(select(startswith("STILL REFERENCED"))) | length >= 1'
check "warning names a surviving referrer" \
    '[.warnings[] | select(startswith("STILL REFERENCED"))] | map(select(contains("GadgetsController") or contains("GetGadget"))) | length >= 1'
check "verification catches the broken plan" \
    '[.warnings[] | select(startswith("VERIFY"))] | length >= 1'

echo "== Case 7: removing an implementation kills its registration; enum member deletes cleanly"
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Domain.WidgetService --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

check "registrations reported dead although their service survives" \
    '[.statements[] | select(.snippet|contains("IWidgetService"))] | length == 2'
check "deleted-but-in-use registration warnings emitted" \
    '[.warnings[]] | map(select(startswith("registration deleted although still in use"))) | length == 2'
check "the still-used service left without any registration is reported" \
    '(.servicesLeftUnregistered >= 1) and ([.warnings[]] | map(select(startswith("SERVICE LEFT UNREGISTERED:") and contains("Fixture.Domain.IWidgetService"))) | length >= 1)'

SCRATCH2=$(mktemp -d)
cp -r tests/Fixture "$SCRATCH2/Fixture"
dotnet restore "$SCRATCH2/Fixture/Fixture.sln" > /dev/null 2>&1
dotnet run --project src/DeadCodeCleaner -- delete --config tests/Fixture/dcc-config.json --solution "$SCRATCH2/Fixture/Fixture.sln" \
    --remove Fixture.Domain.WidgetService --remove Fixture.Domain.WidgetKind.Fancy \
    --delete-review-tests --json "$REPORT" > /dev/null 2> "$SCRATCH2/del.err"
if [ $? -eq 1 ] && grep -q "lose their only registration" "$SCRATCH2/del.err" && grep -q "class WidgetService" -r "$SCRATCH2/Fixture/Fixture.Domain"; then
    pass "delete refuses while a still-used service would lose its only registration (nothing deleted)"
else
    failx "delete refuses while a still-used service would lose its only registration (nothing deleted)"
fi
dotnet run --project src/DeadCodeCleaner -- delete --config tests/Fixture/dcc-config.json --solution "$SCRATCH2/Fixture/Fixture.sln" \
    --remove Fixture.Domain.WidgetService --remove Fixture.Domain.WidgetKind.Fancy \
    --delete-review-tests --allow-unregistered-services --json "$REPORT" > /dev/null 2>&1 || { echo "DELETE FAILED (verification rejected the plan?)"; exit 1; }
if ! grep -q "    Fancy" "$SCRATCH2/Fixture/Fixture.Domain/Shapes.cs"; then
    pass "enum member deleted"; else failx "enum member deleted"; fi
check "enum renumber warning emitted" \
    '[.warnings[]] | map(select(startswith("enum values shift"))) | length >= 1'
if grep -q "Legacy," "$SCRATCH2/Fixture/Fixture.Domain/Shapes.cs"; then
    pass "following enum member intact"; else failx "following enum member intact"; fi
if ! grep -q "AddScoped<IWidgetService" "$SCRATCH2/Fixture/Fixture.Web/Program.cs"; then
    pass "registration removed with its implementation"; else failx "registration removed with its implementation"; fi
if grep -q "AddFixtureLogging" "$SCRATCH2/Fixture/Fixture.Web/Program.cs"; then
    pass "chained untracked call survives"; else failx "chained untracked call survives"; fi
if dotnet build "$SCRATCH2/Fixture/Fixture.sln" > /dev/null 2>&1; then
    pass "solution builds after delete"; else failx "solution builds after delete"; fi

echo "== Case 8: removing a mapped member removes only its ForMember segment"
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Domain.GadgetDto.AuditNote --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }

check "property removed" \
    '[.symbols[] | select(.category=="removed") | .fqn] | index("Fixture.Domain.GadgetDto.AuditNote") != null'
check "ForMember segment removal planned" \
    '[.statements[] | select(.kind | endswith("(segment)")) | .snippet] | map(select(contains("AuditNote"))) | length == 1'
check "whole mapping NOT removed" \
    '[.statements[] | select(.kind == "AutoMapper.CreateMap")] | length == 0'
check "segment warning emitted" \
    '[.warnings[]] | map(select(startswith("map configuration removed"))) | length == 1'
check "verification clean" '.verificationErrors == 0'

SCRATCH3=$(mktemp -d)
cp -r tests/Fixture "$SCRATCH3/Fixture"
dotnet restore "$SCRATCH3/Fixture/Fixture.sln" > /dev/null 2>&1
dotnet run --project src/DeadCodeCleaner -- delete --config tests/Fixture/dcc-config.json --solution "$SCRATCH3/Fixture/Fixture.sln" \
    --remove Fixture.Domain.GadgetDto.AuditNote --json "$REPORT" > /dev/null 2>&1 || { echo "DELETE FAILED (verification rejected the plan?)"; exit 1; }
if ! grep -q "ForMember(d => d.AuditNote)" "$SCRATCH3/Fixture/Fixture.Domain/Mapping.cs"; then
    pass "segment deleted from the mapping"; else failx "segment deleted from the mapping"; fi
if grep -q "ConvertVia<GadgetConverter>" "$SCRATCH3/Fixture/Fixture.Domain/Mapping.cs"; then
    pass "rest of the mapping chain survives"; else failx "rest of the mapping chain survives"; fi
if dotnet build "$SCRATCH3/Fixture/Fixture.sln" > /dev/null 2>&1; then
    pass "solution builds after segment delete"; else failx "solution builds after segment delete"; fi

echo "== Case 9: broken member of a test-live class dies ALONE (container-escalation gate)"
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Domain.ObsoleteGizmo --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "broken member deleted as broken-orphan" \
    '[.symbols[] | select(.category=="broken-orphan") | .fqn] | index("Fixture.Domain.CollateralHelper.Broken") != null'
check "test-live class NOT deleted" \
    '[.symbols[].fqn] | index("Fixture.Domain.CollateralHelper") == null'
check "test-live sibling member NOT deleted" \
    '[.symbols[].fqn] | index("Fixture.Domain.CollateralHelper.Useful") == null'
check "healthy test NOT deleted" \
    '[.symbols[].fqn] | map(select(startswith("Fixture.Tests.CollateralHelperTests"))) | length == 0'
check "no project deletable" '.deletableProjects | length == 0'
check "verification clean" '.verificationErrors == 0'
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Domain.CollateralHelper --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "explicit class removal still cascades its test" \
    '[.symbols[] | select(.category=="broken-test") | .fqn] | index("Fixture.Tests.CollateralHelperTests") != null'

echo "== Case 10: a test project needs a test library AND a test, never a name (scratch)"
SCRATCH4=$(mktemp -d)
mkdir -p "$SCRATCH4/ProdUtil" "$SCRATCH4/Scratch.Test.Tools" "$SCRATCH4/Scratch.Tests" "$SCRATCH4/Scratch.Helpers"
cat > "$SCRATCH4/ProdUtil/ProdUtil.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <!-- <PackageReference Include="nunit" Version="3.14.0" /> -->
  </ItemGroup>
</Project>
CSPROJ
printf 'namespace ProdUtil;\n\npublic static class Program\n{\n    public static void Main()\n    {\n        System.Console.WriteLine("util");\n    }\n}\n\npublic class Doomed\n{\n    public void Go() { }\n}\n' > "$SCRATCH4/ProdUtil/Program.cs"
cat > "$SCRATCH4/Scratch.Test.Tools/Scratch.Test.Tools.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
CSPROJ
printf 'namespace Scratch.Test.Tools;\n\npublic static class Program\n{\n    public static void Main()\n    {\n        System.Console.WriteLine("tool");\n    }\n}\n' > "$SCRATCH4/Scratch.Test.Tools/Program.cs"
cat > "$SCRATCH4/Scratch.Tests/Scratch.Tests.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>
CSPROJ
printf 'namespace Scratch.Tests;\n\npublic class Helper\n{\n    public void Check() { }\n}\n' > "$SCRATCH4/Scratch.Tests/Helper.cs"
cat > "$SCRATCH4/Scratch.Helpers/Scratch.Helpers.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
</Project>
CSPROJ
printf 'namespace Scratch.Helpers;\n\npublic class Builder\n{\n    public void Build() { }\n}\n' > "$SCRATCH4/Scratch.Helpers/Builder.cs"
(cd "$SCRATCH4" && dotnet new sln -n Scratch --force > /dev/null 2>&1 && dotnet sln Scratch.sln add ProdUtil/ProdUtil.csproj Scratch.Test.Tools/Scratch.Test.Tools.csproj Scratch.Tests/Scratch.Tests.csproj Scratch.Helpers/Scratch.Helpers.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution "$SCRATCH4/Scratch.sln" \
    --remove ProdUtil.Doomed --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "commented-out test package, test-like names, test library without tests: all production (no test roots)" \
    '.stats.productionRoots >= 2 and .stats.testRoots == 0'
check "test-like NAME alone produces no classification warning" \
    '[.warnings[]] | map(select(startswith("Scratch.Tests:") or startswith("Scratch.Test.Tools:"))) | length == 0'
check "test library without a test warns and stays production" \
    '[.warnings[]] | map(select(startswith("Scratch.Helpers: references a test library but contains no test"))) | length == 1'

echo "== Case 11: assembly-level attribute keeps its ProjectReference and using (scratch)"
SCRATCH5=$(mktemp -d)
mkdir -p "$SCRATCH5/App" "$SCRATCH5/Lib"
cat > "$SCRATCH5/Lib/Lib.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>
CSPROJ
printf 'namespace Lib;\n\npublic class MarkerAttribute : System.Attribute\n{\n}\n\npublic class Doomed\n{\n    public void Go() { }\n}\n' > "$SCRATCH5/Lib/Lib.cs"
cat > "$SCRATCH5/App/App.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Lib\Lib.csproj" />
  </ItemGroup>
</Project>
CSPROJ
printf 'using Lib;\n\n[assembly: Marker]\n\nnamespace App;\n\npublic static class Program\n{\n    public static void Main()\n    {\n        System.Console.WriteLine("hello");\n    }\n}\n\npublic class Legacy\n{\n    public void Use()\n    {\n        var d = new Doomed();\n        d.Go();\n    }\n}\n' > "$SCRATCH5/App/Program.cs"
(cd "$SCRATCH5" && dotnet new sln -n Scratch --force > /dev/null 2>&1 && dotnet sln Scratch.sln add App/App.csproj Lib/Lib.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution "$SCRATCH5/Scratch.sln" \
    --remove App.Legacy --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "ProjectReference kept for the assembly attribute" '.projectRefRemovals | length == 0'
check "using kept for the assembly attribute" '.usingRemovals | length == 0'
check "verification clean" '.verificationErrors == 0'

echo "== Case 12: AsImplementedInterfaces ignores IDisposable, cascade completes (scratch)"
SCRATCH6=$(mktemp -d)
mkdir -p "$SCRATCH6/Core"
cat > "$SCRATCH6/Core/Core.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
CSPROJ
cat > "$SCRATCH6/Core/Core.cs" <<'CSFILE'
namespace Core;

public static class Program
{
    public static void Main()
    {
        new Startup().Configure(new ContainerBuilder());
    }
}

public class Startup
{
    public void Configure(ContainerBuilder builder)
    {
        builder.RegisterType<Zeta>().AsImplementedInterfaces();
    }
}

public interface IZeta
{
    void Ping();
}

public class Zeta : IZeta, System.IDisposable
{
    public void Ping() { }
    public void Dispose() { }
}

public class ZetaController
{
    public void Act(IZeta z)
    {
        z.Ping();
    }
}

// Name-based stand-in for Autofac.
public class ContainerBuilder
{
    public RegistrationBuilder<T> RegisterType<T>()
    {
        return new RegistrationBuilder<T>();
    }
}

public class RegistrationBuilder<T>
{
    public RegistrationBuilder<T> AsImplementedInterfaces()
    {
        return this;
    }
}
CSFILE
(cd "$SCRATCH6" && dotnet new sln -n Scratch --force > /dev/null 2>&1 && dotnet sln Scratch.sln add Core/Core.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution "$SCRATCH6/Scratch.sln" \
    --remove Core.ZetaController --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "service interface orphaned with its last consumer" \
    '[.symbols[] | select(.category=="orphaned") | .fqn] | index("Core.IZeta") != null'
check "implementation cascades (IDisposable does not pin it)" \
    '[.symbols[] | select(.category=="orphaned") | .fqn] | index("Core.Zeta") != null'
check "registration statement reported dead" '.statements | length >= 1'
check "verification clean" '.verificationErrors == 0'

echo "== Case 13: a config keep pattern protects test code from the cascade"
printf '{ "inSolutionRegistrations": true, "keep": ["Fixture.Tests.WidgetsControllerTests*"] }\n' > "$KEEPCFG"
dotnet run --project src/DeadCodeCleaner -- analyze --config "$KEEPCFG" \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Web.WidgetsController --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "kept test NOT deleted" \
    '[.symbols[].fqn] | map(select(startswith("Fixture.Tests.WidgetsControllerTests"))) | length == 0'
check "kept test referencing deleted code reported as STILL REFERENCED" \
    '[.warnings[]] | map(select(startswith("STILL REFERENCED: config-kept Fixture.Tests.WidgetsControllerTests"))) | length >= 1'
check "delete would refuse (counted as still referenced)" '.stillReferencedTargets >= 1'
# Exact type name (no '*'): the class is kept WHOLE, its broken test included — this used to loop forever.
printf '{ "inSolutionRegistrations": true, "keep": ["Fixture.Tests.WidgetsControllerTests"] }\n' > "$KEEPCFG"
timeout 600 dotnet run --project src/DeadCodeCleaner -- analyze --config "$KEEPCFG" \
    --solution tests/Fixture/Fixture.sln \
    --remove Fixture.Web.WidgetsController --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED (or hung)"; exit 1; }
check "exact-name keep: class kept whole, its broken test reported STILL REFERENCED" \
    '([.symbols[].fqn] | map(select(startswith("Fixture.Tests.WidgetsControllerTests"))) | length == 0) and ([.warnings[]] | map(select(startswith("STILL REFERENCED: config-kept Fixture.Tests.WidgetsControllerTests"))) | length >= 1)'

echo "== Case 14: empty-profile side effects, chained in-solution extension, marker inside a string (scratch)"
SCRATCH7=$(mktemp -d)
mkdir -p "$SCRATCH7/Shop"
cat > "$SCRATCH7/Shop/Shop.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
CSPROJ
cat > "$SCRATCH7/Shop/Shop.cs" <<'CSFILE'
using System.Globalization;

namespace Shop;

public static class Program
{
    public static void Main()
    {
        System.Console.WriteLine(Holder.Banner);
    }
}

public class Alpha { }
public class AlphaDto { }
public class Beta { }
public class BetaDto { }

public class AlphaController
{
    public object Get()
    {
        return new AlphaDto().ToString() + new Alpha() + Holder.Banner;
    }
}

public class BetaController
{
    public object Get()
    {
        Holder.Dead();
        return new BetaDto().ToString() + new Beta();
    }
}

public static class Doomed
{
    public static void Touch() { }
}

public static class Holder
{
    public static string Banner = @"first
second"; public static void Dead() { }
}

public class MainProfile : Profile
{
    public MainProfile()
    {
        CreateMap<Alpha, AlphaDto>().IgnoreExtra();
    }
}

public class CultureProfile : Profile
{
    public CultureProfile()
    {
        CreateMap<Beta, BetaDto>();
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
    }
}

// Name-based stand-ins for AutoMapper.
public class Profile
{
    public MappingExpression<TSource, TDest> CreateMap<TSource, TDest>()
    {
        return new MappingExpression<TSource, TDest>();
    }
}

public class MappingExpression<TSource, TDest>
{
}

public static class MappingExtensions
{
    public static MappingExpression<TSource, TDest> IgnoreExtra<TSource, TDest>(this MappingExpression<TSource, TDest> expression)
    {
        Doomed.Touch();
        return expression;
    }
}
CSFILE
(cd "$SCRATCH7" && dotnet new sln -n Scratch --force > /dev/null 2>&1 && dotnet sln Scratch.sln add Shop/Shop.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution "$SCRATCH7/Scratch.sln" \
    --remove Shop.Doomed --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "extension method called only from a mapping chain is in use (STILL REFERENCED, not deleted)" \
    '([.warnings[]] | map(select(startswith("STILL REFERENCED") and contains("IgnoreExtra"))) | length >= 1) and ([.symbols[].fqn] | map(select(contains("IgnoreExtra"))) | length == 0)'
dotnet run --project src/DeadCodeCleaner -- analyze --config tests/Fixture/dcc-config.json \
    --solution "$SCRATCH7/Scratch.sln" \
    --remove Shop.BetaController --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "dead mapping reported" '[.statements[].snippet] | map(select(contains("CreateMap<Beta, BetaDto>"))) | length == 1'
check "profile with a static property write is NOT deleted as empty" \
    '[.symbols[].fqn] | map(select(startswith("Shop.CultureProfile"))) | length == 0'
check "declaration starting inside a multi-line string is flagged for mark" \
    '[.symbols[] | select(.fqn=="Shop.Holder.Dead") | .parts[0].markerLineInsideToken] == [true]'
dotnet run --project src/DeadCodeCleaner -- mark --config tests/Fixture/dcc-config.json --solution "$SCRATCH7/Scratch.sln" \
    --remove Shop.BetaController --json "$REPORT" > /dev/null 2>&1 || { echo "MARK FAILED"; exit 1; }
if grep -A1 'Banner = @"first' "$SCRATCH7/Shop/Shop.cs" | tail -1 | grep -q '^second"; public static void Dead'; then
    pass "mark left the multi-line string intact"; else failx "mark left the multi-line string intact"; fi
if grep -q "todo: to be deleted" "$SCRATCH7/Shop/Shop.cs"; then
    pass "other markers still inserted"; else failx "other markers still inserted"; fi

echo "== Case 15: a solution's own 'Profile' base class is not an AutoMapper root (scratch, default config)"
SCRATCH8=$(mktemp -d)
mkdir -p "$SCRATCH8/Plain"
cat > "$SCRATCH8/Plain/Plain.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
CSPROJ
cat > "$SCRATCH8/Plain/Plain.cs" <<'CSFILE'
namespace Plain;

public static class Program
{
    public static void Main()
    {
        System.Console.WriteLine("plain");
    }
}

public class Profile
{
    public string Name = "";
}

public class AdminProfile : Profile
{
    public void Touch()
    {
        Doomed.Go();
    }
}

public static class Doomed
{
    public static void Go() { }
}
CSFILE
(cd "$SCRATCH8" && dotnet new sln -n Scratch --force > /dev/null 2>&1 && dotnet sln Scratch.sln add Plain/Plain.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution "$SCRATCH8/Scratch.sln" \
    --remove Plain.Doomed --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "in-solution Profile subclass is ordinary (pre-existing orphan deleted with its broken reference)" \
    '[.symbols[] | select(.fqn=="Plain.AdminProfile") | .category] == ["broken-orphan"]'
check "no STILL REFERENCED (it was never a root)" '.stillReferencedTargets == 0'

echo "== Case 16: legacy framework entry points and registrations with EXTERNAL stub assemblies (scratch)"
SCRATCH9=$(mktemp -d)
mkdir -p "$SCRATCH9/stubs/SysWeb" "$SCRATCH9/stubs/Autofac" "$SCRATCH9/Web"
cat > "$SCRATCH9/stubs/SysWeb/SysWeb.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>System.Web.Stubs</AssemblyName></PropertyGroup>
</Project>
CSPROJ
cat > "$SCRATCH9/stubs/SysWeb/Stubs.cs" <<'CSFILE'
using System;
namespace System.Web { public class HttpApplication { } }
namespace System.Web { [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)] public sealed class PreApplicationStartMethodAttribute : Attribute { public PreApplicationStartMethodAttribute(Type t, string m) { } } }
namespace System.Web.UI { public class Page { } }
namespace System.Web.Mvc { public abstract class Controller { } }
namespace System.Data.Entity.ModelConfiguration { public class EntityTypeConfiguration<T> { } }
namespace Some.Bus { public interface IHandleMessages<T> { void Handle(T message); } }
CSFILE
cat > "$SCRATCH9/stubs/Autofac/Autofac.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Autofac</AssemblyName></PropertyGroup>
</Project>
CSPROJ
cat > "$SCRATCH9/stubs/Autofac/Stubs.cs" <<'CSFILE'
using System;
namespace Autofac
{
    public class Reg<T>
    {
        public Reg<T> As<TS>() { return this; }
        public Reg<T> As(params Type[] t) { return this; }
        public Reg<T> Named(string name, Type t) { return this; }
        public Reg<T> WithMetadata(string key, object value) { return this; }
        public Reg<T> Where(Func<Type, bool> predicate) { return this; }
        public Reg<T> AsImplementedInterfaces() { return this; }
        public Reg<T> AsSelf() { return this; }
    }
    public class ContainerBuilder
    {
        public Reg<T> RegisterType<T>() { return new Reg<T>(); }
        public Reg<object> RegisterAssemblyTypes(params System.Reflection.Assembly[] assemblies) { return new Reg<object>(); }
        public object Build() { return null; }
    }
    public abstract class Module { protected virtual void Load(ContainerBuilder b) { } }
}
CSFILE
dotnet build "$SCRATCH9/stubs/SysWeb/SysWeb.csproj" -o "$SCRATCH9/stubs/out" > /dev/null 2>&1 \
    && dotnet build "$SCRATCH9/stubs/Autofac/Autofac.csproj" -o "$SCRATCH9/stubs/out" > /dev/null 2>&1 \
    || { echo "STUB BUILD FAILED"; exit 1; }
cat > "$SCRATCH9/Web/Web.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup>
    <Reference Include="System.Web.Stubs"><HintPath>$SCRATCH9/stubs/out/System.Web.Stubs.dll</HintPath></Reference>
    <Reference Include="Autofac"><HintPath>$SCRATCH9/stubs/out/Autofac.dll</HintPath></Reference>
  </ItemGroup>
</Project>
CSPROJ
cat > "$SCRATCH9/Web/App.cs" <<'CSFILE'
[assembly: System.Web.PreApplicationStartMethod(typeof(Web.Bootstrap), "Start")]
namespace Web
{
    // Every helper is used by Admin AND by exactly one framework-reached place: removing Admin must
    // delete none of them — each would die if its place were not recognised as live.
    public class Global : System.Web.HttpApplication
    {
        public static Heartbeat Beat;
        protected void Application_Start() { HelperG.Go(); Beat = new Heartbeat(); AutofacConfig.Register(); ScanConfig.Register(); }
    }
    public static class Bootstrap { public static void Start() { HelperS.Go(); } }
    public class Page1 : System.Web.UI.Page { protected void Page_Load(object s, System.EventArgs e) { HelperP.Go(); } }
    public class Order { public string LegacyCode = ""; }
    public class OrderConfig : System.Data.Entity.ModelConfiguration.EntityTypeConfiguration<Order> { public OrderConfig() { HelperE.Go(); } }
    public interface IFoo { } public class Foo : IFoo { public Foo() { HelperF.Go(); } }
    public interface IBar { } public class Bar : IBar { public Bar() { HelperB.Go(); } }
    public interface IBaz { } public class Baz : IBaz { public Baz() { HelperZ.Go(); } }
    public static class AutofacConfig
    {
        public static void Register()
        {
            var b = new Autofac.ContainerBuilder();
            b.RegisterType<Foo>().Named("foo", typeof(IFoo));
            b.RegisterType<Bar>().As(typeof(IBar));
            b.RegisterType<Baz>().WithMetadata("k", 1).As<IBaz>();
            b.Build();
        }
    }
    // The timer's initializer starts a background job: losing Stop() must not delete it.
    public class Heartbeat
    {
        private readonly System.Threading.Timer _timer = new System.Threading.Timer(Tick, null, 1000, 1000);
        static void Tick(object s) { HelperT.Go(); }
        public void Stop() { _timer.Dispose(); }
    }
    // Losing Set leaves _n never assigned: CS0649, an ERROR under TreatWarningsAsErrors.
    public static class Counter
    {
        private static int _n;
        public static void Set(int v) { _n = v; }
        public static int Read() { return _n; }
    }
    public static class Mid
    {
        public static void Keep() { } public static void Drop() { }
#if NEVER_DEFINED
        public static void Hidden() { }
#endif
    }
    public class HomeController : System.Web.Mvc.Controller
    {
        public HomeController(IFoo f, IBar b, IBaz z) { }
        public void Admin() { HelperG.Go(); HelperS.Go(); HelperP.Go(); HelperE.Go(); HelperF.Go(); HelperB.Go(); HelperZ.Go(); HelperT.Go(); Global.Beat.Stop(); Counter.Set(5); Mid.Drop(); }
        public int Other() { Mid.Keep(); return Counter.Read(); }
    }
    public static class HelperG { public static void Go() { } }
    public static class HelperS { public static void Go() { } }
    public static class HelperP { public static void Go() { } }
    public static class HelperE { public static void Go() { } }
    public static class HelperF { public static void Go() { } }
    public static class HelperB { public static void Go() { } }
    public static class HelperZ { public static void Go() { } }
    public static class HelperT { public static void Go() { } }
    // A user base module whose helper registers: the derived module must not die as "empty".
    public interface IRepo { } public class Repo : IRepo { }
    public interface IOrderSvc { } public class OrderSvc : IOrderSvc { }
    public abstract class BaseModule : Autofac.Module
    {
        protected void RegisterRepos(Autofac.ContainerBuilder b) { b.RegisterType<Repo>().As<IRepo>(); }
    }
    public class OrdersModule : BaseModule
    {
        protected override void Load(Autofac.ContainerBuilder b) { b.RegisterType<OrderSvc>().As<IOrderSvc>(); RegisterRepos(b); }
    }
    public class OrdersController : System.Web.Mvc.Controller { public OrdersController(IOrderSvc s) { } }
    public class ReposController : System.Web.Mvc.Controller { public ReposController(IRepo r) { } }
    // Assembly scanning: classes whose name ends with "Service" register under all their interfaces.
    public static class ScanConfig
    {
        public static void Register()
        {
            var b = new Autofac.ContainerBuilder();
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.Name.EndsWith("Service")).AsImplementedInterfaces();
        }
    }
    public interface IAuditService { }
    public class AuditService : IAuditService { }        // matches the filter: kept while IAuditService is used
    public class OldAuditService : IAuditService { }     // matches too: Autofac registers it as well
    public class AuditWriter : IAuditService { }         // filtered out: deletable
    public interface IReportService { }
    public class ReportService : IReportService { }      // matches, but its interface dies: deletable
    public class ScanController : System.Web.Mvc.Controller
    {
        public ScanController(IAuditService a) { }
        public void Build() { _ = new AuditService(); _ = new OldAuditService(); _ = new AuditWriter(); IReportService r = new ReportService(); _ = r; }
    }
    // A pre-existing orphan built on an OUTSIDE framework interface, referenced nowhere: probably
    // created by that framework at runtime — flagged for review, never deleted unconfirmed.
    public class OrderPlacedHandler : Some.Bus.IHandleMessages<string> { public void Handle(string message) { HelperJ.Go(); } }
    // A plain pre-existing orphan: still deleted with its broken reference.
    public class PlainOld { public void Touch() { HelperJ.Go(); } }
    public class JobsController : System.Web.Mvc.Controller { public void Run() { HelperJ.Go(); } }
    public static class HelperJ { public static void Go() { } }
}
CSFILE
(cd "$SCRATCH9" && dotnet new sln -n Scratch --force > /dev/null 2>&1 && dotnet sln Scratch.sln add Web/Web.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution "$SCRATCH9/Scratch.sln" \
    --remove Web.HomeController.Admin --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "Global.asax/Page_Load/start method/EF config/Autofac typeof+unknown-call registrations keep their code" \
    '[.symbols[].fqn] | sort == ["Web.Counter.Set", "Web.Heartbeat.Stop", "Web.HomeController.Admin", "Web.Mid.Drop"]'
check "no registration statement deleted" '.statements | length == 0'
check "warning-as-error CS0649 caught by verification" \
    '[.warnings[]] | map(select(startswith("VERIFY") and contains("CS0649"))) | length >= 1'
check "mid-line deletion does not join the next line onto #if (no CS1040)" \
    '[.warnings[]] | map(select(contains("CS1040"))) | length == 0'
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution "$SCRATCH9/Scratch.sln" \
    --remove Web.OrdersController --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "module calling a base-module helper is not deleted as empty" \
    '[.symbols[].fqn] | (index("Web.OrdersModule") == null and index("Web.Repo") == null)'
check "the dead service and its registration go" \
    '([.symbols[].fqn] | index("Web.OrderSvc") != null) and (.statements | length == 1)'
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution "$SCRATCH9/Scratch.sln" \
    --remove Web.ScanController.Build --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "scanned classes matching the filter, with a used interface, are kept" \
    '[.symbols[].fqn] | (index("Web.AuditService") == null and index("Web.OldAuditService") == null)'
check "a class the scan filter excludes is deleted" '[.symbols[].fqn] | index("Web.AuditWriter") != null'
check "a scanned class whose interface died is deleted with it" \
    '[.symbols[].fqn] | (index("Web.ReportService") != null and index("Web.IReportService") != null)'
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution "$SCRATCH9/Scratch.sln" \
    --remove Web.JobsController --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "framework-built pre-existing orphan is flagged [REVIEW]" \
    '[.symbols[] | select(.fqn=="Web.OrderPlacedHandler") | (.category == "broken-orphan" and .review)] == [true]'
check "plain pre-existing orphan is deleted without review" \
    '[.symbols[] | select(.fqn=="Web.PlainOld") | (.category == "broken-orphan" and (.review | not))] == [true]'
cp -r "$SCRATCH9/Web" "$SCRATCH9/WebDel" && rm -rf "$SCRATCH9/WebDel/obj" "$SCRATCH9/WebDel/bin"
(cd "$SCRATCH9" && dotnet new sln -n Del --force > /dev/null 2>&1 && dotnet sln Del.sln add WebDel/Web.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- delete \
    --solution "$SCRATCH9/Del.sln" --remove Web.JobsController --json "$REPORT" > /dev/null 2> "$SCRATCH9/del.err"
if [ $? -eq 1 ] && grep -q "may be created or called by a framework" "$SCRATCH9/del.err" && [ -f "$SCRATCH9/WebDel/App.cs" ] && grep -q "class OrderPlacedHandler" "$SCRATCH9/WebDel/App.cs"; then
    pass "delete refuses while a framework-built orphan awaits review (nothing deleted)"
else
    failx "delete refuses while a framework-built orphan awaits review (nothing deleted)"
fi

echo "== Case 17: a namespace shared across projects dies in the referenced assembly (scratch)"
SCRATCH10=$(mktemp -d)
mkdir -p "$SCRATCH10/A" "$SCRATCH10/B" "$SCRATCH10/C"
for p in B C; do
    printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>\n</Project>\n' > "$SCRATCH10/$p/$p.csproj"
done
# One explicitly linked file: an SDK-style project still needs no <Compile> entry for the others.
printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>\n  <ItemGroup><ProjectReference Include="../B/B.csproj" /><Compile Include="../Shared/Version.cs" Link="Version.cs" /></ItemGroup>\n</Project>\n' > "$SCRATCH10/A/A.csproj"
mkdir -p "$SCRATCH10/Shared"
printf 'namespace AApp { public static class AppVersion { public const int V = 1; } }\n' > "$SCRATCH10/Shared/Version.cs"
cat > "$SCRATCH10/B/B.cs" <<'CSFILE'
namespace Shared.Helpers { public static class BHelper { public static int X() { return 1; } } }
namespace BOther { public static class Keep { public static int Z() { return 3; } } }
CSFILE
# C also declares Shared.Helpers, but A never references C.
cat > "$SCRATCH10/C/C.cs" <<'CSFILE'
namespace Shared.Helpers { public static class CHelper { public static int Y() { return 2; } } }
namespace CApp { public class CController { public int Go() { return Shared.Helpers.CHelper.Y(); } } }
CSFILE
cat > "$SCRATCH10/A/Program.cs" <<'CSFILE'
using Shared.Helpers;
namespace AApp
{
    public static class Program { public static void Main() { BOther.Keep.Z(); } }
}
CSFILE
cat > "$SCRATCH10/A/FeatureController.cs" <<'CSFILE'
namespace AApp
{
    public class FeatureController { public int Run() { return Shared.Helpers.BHelper.X(); } }
}
CSFILE
(cd "$SCRATCH10" && dotnet new sln -n Scratch --force > /dev/null 2>&1 && dotnet sln Scratch.sln add A/A.csproj B/B.csproj C/C.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution "$SCRATCH10/Scratch.sln" \
    --remove AApp.FeatureController --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "B's last Shared.Helpers type dies" '[.symbols[].fqn] | index("Shared.Helpers.BHelper") != null'
check "A's using Shared.Helpers is removed although C still has the namespace" \
    '[.usingRemovals[] | select(.namespace=="Shared.Helpers") | .file] | map(endswith("A/Program.cs")) | any'
check "no false missing-<Compile>-entry warning in an SDK project with one linked file" \
    '[.warnings[]] | map(select(contains("no textually matching <Compile> entry"))) | length == 0'
# An ASP.NET Web Site project (no csproj) in the solution: excluded, and delete refuses.
sed -i 's/^Global\r\?$/Project("{E24C65DC-7377-472B-9ABA-BC803B73C61A}") = "OldSite", "http:\/\/localhost\/OldSite", "{11111111-2222-3333-4444-555555555555}"\r\nEndProject\r\nGlobal\r/' "$SCRATCH10/Scratch.sln"
dotnet run --project src/DeadCodeCleaner -- delete \
    --solution "$SCRATCH10/Scratch.sln" --remove AApp.FeatureController --json "$REPORT" > /dev/null 2> "$SCRATCH10/del.err"
if [ $? -eq 1 ] && grep -q "Web Site project" "$SCRATCH10/del.err" && [ -f "$SCRATCH10/A/FeatureController.cs" ]; then
    pass "delete refuses with a Web Site project in the solution (nothing deleted)"
else
    failx "delete refuses with a Web Site project in the solution (nothing deleted)"
fi
check "the Web Site project is reported" '.websiteProjects == 1'

echo "== Case 18: scan styles, convention entry points and construction side effects with EXTERNAL stubs (scratch)"
SCRATCH11=$(mktemp -d)
mkdir -p "$SCRATCH11/stubs/SysWeb" "$SCRATCH11/stubs/Autofac" "$SCRATCH11/stubs/Scrutor" "$SCRATCH11/stubs/Hosting" "$SCRATCH11/Web"
cat > "$SCRATCH11/stubs/SysWeb/SysWeb.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>System.Web.Stubs</AssemblyName></PropertyGroup>
</Project>
CSPROJ
cat > "$SCRATCH11/stubs/SysWeb/Stubs.cs" <<'CSFILE'
namespace System.Web { public class HttpApplication { } }
namespace System.Web.Mvc { public abstract class Controller { } public abstract class ActionFilterAttribute : System.Attribute { } }
CSFILE
cat > "$SCRATCH11/stubs/Autofac/Autofac.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Autofac</AssemblyName></PropertyGroup>
</Project>
CSPROJ
cat > "$SCRATCH11/stubs/Autofac/Stubs.cs" <<'CSFILE'
using System;
namespace Autofac
{
    public class Reg<T>
    {
        public Reg<T> As<TS>() { return this; }
        public Reg<T> Named<TS>(Func<Type, string> name) { return this; }
        public Reg<T> AsClosedTypesOf(Type open) { return this; }
        public Reg<T> Where(Func<Type, bool> predicate) { return this; }
        public Reg<T> AsImplementedInterfaces() { return this; }
        public Reg<T> AsSelf() { return this; }
        public Reg<T> AutoActivate() { return this; }
    }
    public class ContainerBuilder
    {
        public Reg<T> RegisterType<T>() { return new Reg<T>(); }
        public Reg<object> RegisterAssemblyTypes(params System.Reflection.Assembly[] assemblies) { return new Reg<object>(); }
    }
}
CSFILE
cat > "$SCRATCH11/stubs/Scrutor/Scrutor.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Scrutor</AssemblyName></PropertyGroup>
</Project>
CSPROJ
cat > "$SCRATCH11/stubs/Scrutor/Stubs.cs" <<'CSFILE'
using System;
namespace Scrutor
{
    public class Selector
    {
        public Selector FromAssembliesOf(params Type[] types) { return this; }
        public Selector AddClasses() { return this; }
        public Selector AsMatchingInterface() { return this; }
    }
    public class Services { }
    public static class ScanExtensions { public static Services Scan(this Services s, Action<Selector> a) { return s; } }
}
CSFILE
cat > "$SCRATCH11/stubs/Hosting/Hosting.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Microsoft.Owin.Stubs</AssemblyName></PropertyGroup>
</Project>
CSPROJ
cat > "$SCRATCH11/stubs/Hosting/Stubs.cs" <<'CSFILE'
using System;
namespace Microsoft.Owin { [AttributeUsage(AttributeTargets.Assembly)] public sealed class OwinStartupAttribute : Attribute { public OwinStartupAttribute(Type t) { } } }
namespace Hosting { public class HostBuilder { public HostBuilder UseStartup<T>() { return this; } } }
CSFILE
for s in SysWeb Autofac Scrutor Hosting; do
    dotnet build "$SCRATCH11/stubs/$s/$s.csproj" -o "$SCRATCH11/stubs/out" > /dev/null 2>&1 || { echo "STUB BUILD FAILED ($s)"; exit 1; }
done
cat > "$SCRATCH11/Web/Web.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <Reference Include="System.Web.Stubs"><HintPath>$SCRATCH11/stubs/out/System.Web.Stubs.dll</HintPath></Reference>
    <Reference Include="Autofac"><HintPath>$SCRATCH11/stubs/out/Autofac.dll</HintPath></Reference>
    <Reference Include="Scrutor"><HintPath>$SCRATCH11/stubs/out/Scrutor.dll</HintPath></Reference>
    <Reference Include="Microsoft.Owin.Stubs"><HintPath>$SCRATCH11/stubs/out/Microsoft.Owin.Stubs.dll</HintPath></Reference>
  </ItemGroup>
</Project>
CSPROJ
cat > "$SCRATCH11/Web/App.cs" <<'CSFILE'
[assembly: Microsoft.Owin.OwinStartup(typeof(Web.AuthConfig))]
namespace Web
{
    // Every helper is used by Admin AND by exactly one place only a framework reaches: removing
    // Admin must delete none of them.
    public class Global : System.Web.HttpApplication
    {
        public static Heartbeat Beat = new Heartbeat();
        public static UserRepo Users = new UserRepo();
        public static AuditFilter Filter = new AuditFilter();
        protected void Application_Start() { ScanConfig.Register(); new Hosting.HostBuilder().UseStartup<ApiStartup>(); }
        protected void FormsAuthentication_OnAuthenticate(object s, System.EventArgs e) { HelperA.Go(); }
    }
    public class AuthConfig { public void Configuration(object app) { HelperO.Go(); } }
    public class ApiStartup { public void ConfigureServices(object services) { HelperU.Go(); } }
    public interface ICommandHandler<T> { void Handle(T c); }
    public class CreateOrder { }
    public class CreateOrderHandler : ICommandHandler<CreateOrder> { public void Handle(CreateOrder c) { HelperC.Go(); } }
    public interface IPlugin { }
    public class ZipPlugin : IPlugin { public ZipPlugin() { HelperN.Go(); } }
    public abstract class HandlerBase { }
    public class EmailHandler : HandlerBase { public EmailHandler() { HelperB.Go(); } }
    public interface IFooService { }
    public class FooService : IFooService { public FooService() { HelperI.Go(); } }
    public interface IOrderRepository { }
    public class SqlOrderRepository : IOrderRepository { public SqlOrderRepository() { HelperM.Go(); } }
    public class CacheWarmer { public CacheWarmer() { HelperW.Go(); } }
    public class ReportBuilder { public ReportBuilder(IFooService f) { HelperS.Go(); } public ReportBuilder() { } }
    public static class ScanConfig
    {
        public static void Register()
        {
            var b = new Autofac.ContainerBuilder();
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).AsClosedTypesOf(typeof(ICommandHandler<>));
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.Name.EndsWith("Plugin")).Named<IPlugin>(t => t.Name);
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.IsSubclassOf(typeof(HandlerBase))).As<HandlerBase>();
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.Name.EndsWith("service", true, System.Globalization.CultureInfo.InvariantCulture)).AsImplementedInterfaces();
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.Namespace != null && t.Namespace.StartsWith("Web") && t.Name.StartsWith("Report")).AsSelf();
            b.RegisterType<CacheWarmer>().AsSelf().AutoActivate();
            b.RegisterType<NightlyTask>().AsSelf().AutoActivate();
            new Scrutor.Services().Scan(s => s.FromAssembliesOf(typeof(ScanConfig)).AddClasses().AsMatchingInterface());
        }
    }
    // A timer started by a SOLUTION class's constructor, created in a field initializer.
    public class Poller { private readonly System.Threading.Timer _t; public Poller(int ms) { _t = new System.Threading.Timer(_ => { }, null, ms, ms); } public void Stop() { _t.Dispose(); } }
    public class Heartbeat { private readonly Poller _poller = new Poller(1000); public void Stop() { _poller.Stop(); } }
    // UserRepo has no constructor: it implicitly calls the base constructor with its default argument.
    public class RepoBase { public string Conn; public RepoBase(string conn = "db") { Conn = conn; HelperR.Go(); } }
    public class UserRepo : RepoBase { }
    // Built by the container, never by the solution: a method nothing calls may be a convention the
    // tool does not know — held for review, not deleted.
    public class NightlyTask { public void Execute() { HelperX.Go(); } }
    // Created by the solution's own code: its dead method is deleted as usual.
    public class AuditFilter : System.Web.Mvc.ActionFilterAttribute { public void OldLog() { HelperY.Go(); } }
    // Kept by exact name in the config: reached by reflection, so its members stay too.
    public class ExportPlugin { public void Run() { HelperK.Go(); } }
    public class HomeController : System.Web.Mvc.Controller
    {
        public HomeController(ICommandHandler<CreateOrder> h, IPlugin p, HandlerBase hb, IFooService f, IOrderRepository r, ReportBuilder rb) { h.Handle(new CreateOrder()); }
        public void Admin()
        {
            HelperA.Go(); HelperO.Go(); HelperU.Go(); HelperC.Go(); HelperN.Go(); HelperB.Go(); HelperI.Go(); HelperM.Go(); HelperW.Go();
            HelperS.Go(); HelperR.Go(); HelperK.Go(); HelperX.Go(); HelperY.Go(); Global.Beat.Stop(); _ = new RepoBase("x"); new ExportPlugin().Run(); _ = new ReportBuilder();
        }
    }
    public static class HelperA { public static void Go() { } }
    public static class HelperO { public static void Go() { } }
    public static class HelperU { public static void Go() { } }
    public static class HelperC { public static void Go() { } }
    public static class HelperN { public static void Go() { } }
    public static class HelperB { public static void Go() { } }
    public static class HelperI { public static void Go() { } }
    public static class HelperM { public static void Go() { } }
    public static class HelperW { public static void Go() { } }
    public static class HelperS { public static void Go() { } }
    public static class HelperR { public static void Go() { } }
    public static class HelperK { public static void Go() { } }
    public static class HelperX { public static void Go() { } }
    public static class HelperY { public static void Go() { } }
}
// A class outside any namespace: the scan filter reading t.Namespace must not crash on it.
public class GlobalNamespaceThing { }
CSFILE
printf '{ "keep": ["Web.ExportPlugin"] }\n' > "$SCRATCH11/keep.json"
(cd "$SCRATCH11" && dotnet new sln -n Scratch --force > /dev/null 2>&1 && dotnet sln Scratch.sln add Web/Web.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution "$SCRATCH11/Scratch.sln" --config "$SCRATCH11/keep.json" \
    --remove Web.HomeController.Admin --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "scan styles, Global.asax module events, OwinStartup, UseStartup, AutoActivate, base ctor defaults, initializer ctors and kept-type members keep their code" \
    '[.symbols[].fqn] | sort == ["Web.AuditFilter.OldLog", "Web.Heartbeat.Stop", "Web.HelperX", "Web.HelperY", "Web.HomeController.Admin", "Web.NightlyTask.Execute", "Web.Poller.Stop"]'
check "no registration statement deleted" '.statements | length == 0'
check "a dead method of a container-built class is held for review" \
    '[.symbols[] | select(.fqn=="Web.NightlyTask.Execute") | (.category == "broken-orphan" and .review)] == [true]'
check "a dead method of a class the code creates with new is deleted without review" \
    '[.symbols[] | select(.fqn=="Web.AuditFilter.OldLog") | (.category == "broken-orphan" and (.review | not))] == [true]'

echo "== Case 19: project references, linked files, nullable warnings-as-errors and props-declared tests (scratch)"
SCRATCH12=$(mktemp -d)
mkdir -p "$SCRATCH12/stubs/Xunit" "$SCRATCH12/App" "$SCRATCH12/Lib" "$SCRATCH12/LibA" "$SCRATCH12/LibB" "$SCRATCH12/Cons" "$SCRATCH12/Shared" "$SCRATCH12/tests/Tst"
printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>xunit.core</AssemblyName></PropertyGroup>\n</Project>\n' > "$SCRATCH12/stubs/Xunit/Xunit.csproj"
printf 'namespace Xunit { public sealed class FactAttribute : System.Attribute { } }\n' > "$SCRATCH12/stubs/Xunit/Stubs.cs"
dotnet build "$SCRATCH12/stubs/Xunit/Xunit.csproj" -o "$SCRATCH12/stubs/out" > /dev/null 2>&1 || { echo "STUB BUILD FAILED"; exit 1; }
# App references Lib; its only compile-time use of Lib dies, but Lib still holds a controller.
cat > "$SCRATCH12/App/App.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Exe</OutputType><Nullable>enable</Nullable><WarningsAsErrors>nullable</WarningsAsErrors></PropertyGroup>
  <ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>
</Project>
CSPROJ
cat > "$SCRATCH12/App/App.cs" <<'CSFILE'
namespace App
{
    public static class Program { public static void Main() { System.Console.WriteLine(new HomeController().Index(null!)); } }
    public class Svc
    {
        private readonly string _name;
        public Svc(string n) { _name = n; }
        public string Name() { return _name; }
    }
    public class HomeController
    {
        public int Index(Svc s) { return s.Name().Length; }
        // Losing the only constructor leaves _name unassigned: CS8618, an error under WarningsAsErrors=nullable.
        public int Old() { return new Svc("x").Name().Length; }
    }
    public class FeatureController { public int Run() { return Lib.LibUtil.Do(); } }
    public static class Calc { public static int Net(int g) { return g - 1; } }
}
CSFILE
printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>\n</Project>\n' > "$SCRATCH12/Lib/Lib.csproj"
printf 'namespace Lib\n{\n    public static class LibUtil { public static int Do() { return 1; } }\n    public class LibAdminController { public int Index() { return 2; } }\n}\n' > "$SCRATCH12/Lib/Lib.cs"
# One file linked into LibA and LibB; Cons references only LibB and needs its using.
for p in LibA LibB; do
    printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>\n  <ItemGroup><Compile Include="../Shared/Linked.cs" Link="Linked.cs" /></ItemGroup>\n</Project>\n' > "$SCRATCH12/$p/$p.csproj"
done
printf 'namespace LinkedNs { public static class SharedThing { public static int V() { return 1; } } }\n' > "$SCRATCH12/Shared/Linked.cs"
printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>\n  <ItemGroup><ProjectReference Include="../LibB/LibB.csproj" /></ItemGroup>\n</Project>\n' > "$SCRATCH12/Cons/Cons.csproj"
cat > "$SCRATCH12/Cons/Cons.cs" <<'CSFILE'
using LinkedNs;
namespace Cons
{
    public class UseController { public int Go() { return SharedThing.V(); } }
    public class OldController { public int Go() { return 2; } }
}
CSFILE
# The test library comes from tests/Directory.Build.props, not from the test project itself.
cat > "$SCRATCH12/tests/Directory.Build.props" <<PROPS
<Project>
  <ItemGroup><Reference Include="xunit.core"><HintPath>$SCRATCH12/stubs/out/xunit.core.dll</HintPath></Reference></ItemGroup>
</Project>
PROPS
printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>\n  <ItemGroup><ProjectReference Include="../../App/App.csproj" /></ItemGroup>\n</Project>\n' > "$SCRATCH12/tests/Tst/Tst.csproj"
printf 'namespace Tst\n{\n    public class FeatureTests { [Xunit.Fact] public void Both() { new App.FeatureController().Run(); _ = new App.HomeController(); } }\n}\n' > "$SCRATCH12/tests/Tst/Tests.cs"
(cd "$SCRATCH12" && dotnet new sln -n Scratch --force > /dev/null 2>&1 \
    && dotnet sln Scratch.sln add App/App.csproj Lib/Lib.csproj LibA/LibA.csproj LibB/LibB.csproj Cons/Cons.csproj tests/Tst/Tst.csproj > /dev/null 2>&1)
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution "$SCRATCH12/Scratch.sln" \
    --remove App.FeatureController --remove Cons.OldController --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "Lib's helper dies with the removed controller" '[.symbols[].fqn] | index("Lib.LibUtil") != null'
check "the reference to a project still holding entry points is kept, with a warning" \
    '(.projectRefRemovals | length == 0) and ([.warnings[]] | map(select(contains("ProjectReference Lib") and contains("entry points"))) | length == 1)'
check "a using of a linked file's namespace stays for the project that only references the second copy" \
    '[.usingRemovals[] | select(.namespace=="LinkedNs")] | length == 0'
check "a test project whose test library comes from Directory.Build.props is a test project" \
    '[.symbols[] | select(.fqn=="Tst.FeatureTests") | (.category == "broken-test" and .review)] == [true]'
dotnet run --project src/DeadCodeCleaner -- analyze \
    --solution "$SCRATCH12/Scratch.sln" \
    --remove App.HomeController.Old --json "$REPORT" > /dev/null 2>&1 || { echo "ANALYZE FAILED"; exit 1; }
check "WarningsAsErrors=nullable: the new CS8618 is caught by verification" \
    '[.warnings[]] | map(select(startswith("VERIFY") and contains("CS8618"))) | length >= 1'

if [ "$FAIL" = 1 ]; then echo "RESULT: FAILURES PRESENT"; exit 1; fi
echo "RESULT: all checks passed"
