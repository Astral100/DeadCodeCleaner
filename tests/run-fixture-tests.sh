#!/usr/bin/env bash
# Known-answer regression tests for DeadCodeCleaner, run against tests/Fixture.
# Usage: tests/run-fixture-tests.sh
set -u
cd "$(dirname "$0")/.."

REPORT=$(mktemp)
FAIL=0

check() { # check <description> <jq filter that must output true>
    local desc="$1" filter="$2"
    local result
    result=$(jq -e "$filter" "$REPORT" > /dev/null 2>&1 && echo ok || echo FAIL)
    if [ "$result" = "FAIL" ]; then FAIL=1; fi
    printf '  %-4s %s\n' "$result" "$desc"
}

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
check "mixed test flagged for review" \
    '[.symbols[] | select(.fqn=="Fixture.Tests.MixedTests") | .review] | index(true) != null'
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
    '[.statements[] | select(.kind=="DI.Register") | .snippet] | map(select(contains("IWidgetService"))) | length == 1'
check "Widgets.cs deletable in full" \
    '[.deletableFiles[]] | map(select(endswith("Widgets.cs"))) | length == 1'
check "Controllers.cs NOT deletable" \
    '[.deletableFiles[]] | map(select(endswith("Controllers.cs"))) | length == 0'

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

rm -f "$REPORT"
if [ "$FAIL" = 1 ]; then echo "RESULT: FAILURES PRESENT"; exit 1; fi
echo "RESULT: all checks passed"
