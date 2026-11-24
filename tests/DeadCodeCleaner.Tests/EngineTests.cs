using Xunit;

namespace DeadCodeCleaner.Tests;

public class EngineTests
{
    static readonly Analysis WidgetsControllerRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Web.WidgetsController");
    static readonly Analysis GetSecondaryRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Web.WidgetsController.GetSecondary");
    static readonly Analysis GetSecondaryIntRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Web.WidgetsController.GetSecondary(int)");
    static readonly Analysis BothControllersRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Web.WidgetsController", "Fixture.Web.GadgetsController");
    static readonly Analysis SharedServiceRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Domain.SharedService");
    static readonly Analysis WidgetServiceRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Domain.WidgetService");
    static readonly Analysis AuditNoteRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Domain.GadgetDto.AuditNote");
    static readonly Analysis ObsoleteGizmoRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Domain.ObsoleteGizmo");
    static readonly Analysis CollateralHelperRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Domain.CollateralHelper");
    static readonly Analysis WildcardKeepOfTests = new(() => TestSolutions.Fixture, () => TestSolutions.KeepConfig("Fixture.Tests.WidgetsControllerTests*"), "Fixture.Web.WidgetsController");
    static readonly Analysis ExactKeepOfTests = new(() => TestSolutions.Fixture, () => TestSolutions.KeepConfig("Fixture.Tests.WidgetsControllerTests"), "Fixture.Web.WidgetsController");
    static readonly Analysis TestClassificationScenario = new(() => TestSolutions.Scenario("TestClassification"), TestSolutions.FixtureConfig, "ProdUtil.Doomed");
    static readonly Analysis AssemblyAttributeScenario = new(() => TestSolutions.Scenario("AssemblyAttribute"), TestSolutions.FixtureConfig, "App.Legacy");
    static readonly Analysis AutofacDisposableScenario = new(() => TestSolutions.Scenario("AutofacDisposable"), TestSolutions.FixtureConfig, "Core.ZetaController");
    static readonly Analysis ShopDoomedRemoved = new(() => TestSolutions.Scenario("ShopProfiles"), TestSolutions.FixtureConfig, "Shop.Doomed");
    static readonly Analysis ShopBetaControllerRemoved = new(() => TestSolutions.Scenario("ShopProfiles"), TestSolutions.FixtureConfig, "Shop.BetaController");
    static readonly Analysis PlainProfileScenario = new(() => TestSolutions.Scenario("PlainProfile"), () => new AnalyzerConfig(), "Plain.Doomed");
    static readonly Analysis LegacyAdminRemoved = new(() => TestSolutions.Scenario("LegacyExternal"), () => new AnalyzerConfig(), "Web.HomeController.Admin");
    static readonly Analysis LegacyOrdersControllerRemoved = new(() => TestSolutions.Scenario("LegacyExternal"), () => new AnalyzerConfig(), "Web.OrdersController");
    static readonly Analysis LegacyScanBuildRemoved = new(() => TestSolutions.Scenario("LegacyExternal"), () => new AnalyzerConfig(), "Web.ScanController.Build");
    static readonly Analysis LegacyJobsControllerRemoved = new(() => TestSolutions.Scenario("LegacyExternal"), () => new AnalyzerConfig(), "Web.JobsController");
    static readonly Analysis SharedNamespaceScenario = new(() => TestSolutions.Scenario("SharedNamespace"), () => new AnalyzerConfig(), "AApp.FeatureController");
    static readonly Analysis SharedNamespaceWithWebSite = new(() => TestSolutions.ScenarioWithWebSite("SharedNamespace"), () => new AnalyzerConfig(), "AApp.FeatureController");
    static readonly Analysis ScanStylesScenario = new(() => TestSolutions.Scenario("ScanStyles"), () => TestSolutions.KeepConfig("Web.ExportPlugin", inSolutionRegistrations: false), "Web.HomeController.Admin");
    static readonly Analysis ProjectPlumbingScenario = new(() => TestSolutions.Scenario("ProjectPlumbing"), () => new AnalyzerConfig(), "App.FeatureController", "Cons.OldController");
    static readonly Analysis NullableScenario = new(() => TestSolutions.Scenario("ProjectPlumbing"), () => new AnalyzerConfig(), "App.HomeController.Old");
    static readonly Analysis GadgetConverterRemoved = new(() => TestSolutions.Fixture, TestSolutions.FixtureConfig, "Fixture.Domain.GadgetConverter");
    static readonly Analysis MapSourceRemoved = new(() => TestSolutions.Scenario("RuntimeWiring"), () => new AnalyzerConfig(), "Api.Order.ComputeTotal");
    static readonly Analysis MapDestinationRemoved = new(() => TestSolutions.Scenario("RuntimeWiring"), () => new AnalyzerConfig(), "Api.OrderDto.Total");
    static readonly Analysis RuntimeWiringRemovals = new(() => TestSolutions.Scenario("RuntimeWiring"), () => new AnalyzerConfig(),
        "Api.AB", "Api.C1", "Api.CustomerRepository", "Api.LegacyController", "Api.SvcController", "Api.ReportsController.Run", "Api.DoomedController", "Web.OldController");

    public EngineTests()
    {
        Analysis.StartAll();
    }

    // ------------------------------------------------ the fixture: removing WidgetsController

    [Theory]
    [InlineData("Fixture.Domain.WidgetService")]
    [InlineData("Fixture.Domain.IWidgetService")]
    [InlineData("Fixture.Domain.GetWidgetHandler")]
    [InlineData("Fixture.Domain.GetWidgetQuery")]
    [InlineData("Fixture.Domain.WidgetRepository")]
    [InlineData("Fixture.Domain.WidgetEntity")]
    [InlineData("Fixture.Domain.WidgetDto")]
    [InlineData("Fixture.Web.SecondaryHelper")]
    [InlineData("Fixture.Domain.WidgetExtra")]
    public async Task Analyze_when_widgets_controller_is_removed_then_its_cascade_is_orphaned(string expectedFqn)
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(expectedFqn, Fqns(actualReport, Constants.CategoryOrphaned));
    }

    [Theory]
    [InlineData("Fixture.Domain.SharedService")]
    [InlineData("Fixture.Domain.GadgetService")]
    public async Task Analyze_when_widgets_controller_is_removed_then_code_still_in_use_is_not_deleted(string expectedSurvivor)
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.False(TestSolutions.IsDeleted(actualReport, expectedSurvivor));
    }

    [Theory]
    [InlineData("Fixture.Domain.DeadLegacyHelper")]
    [InlineData("Fixture.Domain.SharedService.WidgetCounter")]
    [InlineData("Fixture.Domain.SharedService.GadgetCounter")]
    [InlineData("Fixture.Domain.SharedService.WidgetPublicTally")]
    [InlineData("Fixture.Tests.SharedServiceTests")]
    [InlineData("Fixture.Tests.UnusedTestUtil")]
    [InlineData("Fixture.Domain.ShapeBase.Render")]
    [InlineData("Fixture.Domain.WidgetFormatter")]
    [InlineData("Fixture.Domain.GadgetBag.Add")]
    [InlineData("Fixture.Domain.FixtureMarkerAttribute")]
    [InlineData("Fixture.Domain.EnumNoteAttribute")]
    [InlineData("Fixture.Domain.GadgetDto.Deconstruct")]
    [InlineData("Fixture.Domain.AuditAttribute..ctor")]
    public async Task Analyze_when_widgets_controller_is_removed_then_surviving_symbol_is_not_deleted(string expectedSurvivor)
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.False(TestSolutions.IsDeleted(actualReport, expectedSurvivor));
    }

    [Theory]
    [InlineData("Fixture.Domain.InactiveUse")]
    [InlineData("Fixture.Domain.WidgetTag")]
    [InlineData("Fixture.Domain.TagHolder")]
    [InlineData("Fixture.Domain.DynamicTarget")]
    [InlineData("Fixture.Tests.WidgetCheckBase")]
    public async Task Analyze_when_widgets_controller_is_removed_then_nothing_under_the_type_is_reported(string expectedPrefix)
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain(Fqns(actualReport), f => f.StartsWith(expectedPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_when_a_pre_existing_orphan_references_deleted_code_then_it_is_a_broken_orphan()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedFqn = "Fixture.Domain.DeadWidgetAdmirer";

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(expectedFqn, Fqns(actualReport, Constants.CategoryBrokenOrphan));
    }

    [Fact]
    public async Task Analyze_when_a_shared_declaration_sibling_stays_in_use_then_the_unused_sibling_lives_without_a_warning()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedCount = 0;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.Warnings.Count(w => w.Contains("WidgetCounter")));
    }

    [Fact]
    public async Task Analyze_when_every_variable_of_a_declaration_dies_then_the_whole_line_is_one_part()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedParts = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(actualReport.Symbols, s => s.Fqn == "Fixture.Domain.SharedService.WidgetOnlyA" && s.Parts.Count == expectedParts);
    }

    [Fact]
    public async Task Analyze_when_a_test_class_references_only_deleted_code_then_it_is_a_broken_test()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedFqn = "Fixture.Tests.WidgetsControllerTests";

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(expectedFqn, Fqns(actualReport, Constants.CategoryBrokenTest));
    }

    [Fact]
    public async Task Analyze_when_a_deleted_test_also_touches_surviving_code_then_it_is_flagged_for_review()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(actualReport.Symbols, s => s.Fqn == "Fixture.Tests.WidgetServiceTests" && s.Review);
    }

    [Fact]
    public async Task Analyze_when_a_test_helper_was_used_only_by_deleted_tests_then_it_cascades()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedFqn = "Fixture.Tests.TestData";

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(expectedFqn, Fqns(actualReport, Constants.CategoryOrphanedTestHelper));
    }

    [Fact]
    public async Task Analyze_when_widgets_controller_is_removed_then_its_create_map_is_reported_once()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedCount = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.Statements.Count(s => s.Kind == "AutoMapper.CreateMap" && s.Snippet.Contains("WidgetEntity")));
    }

    [Fact]
    public async Task Analyze_when_widgets_controller_is_removed_then_the_gadget_create_map_is_not_reported()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain(actualReport.Statements, s => s.Snippet.Contains("GadgetEntity"));
    }

    [Fact]
    public async Task Analyze_when_widgets_controller_is_removed_then_both_registrations_are_reported()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedCount = 2;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.Statements.Count(s => s.Kind == Constants.StatementKindDiRegister && s.Snippet.Contains("IWidgetService")));
    }

    [Fact]
    public async Task Analyze_when_registrations_die_then_the_chained_one_is_a_segment_and_the_typeof_one_whole()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedWhole = new[] { false, true };

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedWhole, actualReport.Statements.Where(s => s.Snippet.Contains("IWidgetService")).Select(s => s.WholeStatement));
    }

    [Fact]
    public async Task Analyze_when_widgets_controller_is_removed_then_the_typeof_registration_is_reported_dead()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedCount = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.Statements.Count(s => s.Snippet.Contains("typeof(IWidgetService)")));
    }

    [Fact]
    public async Task Analyze_when_every_declaration_of_a_file_dies_then_the_file_is_deletable()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Single(actualReport.DeletableFiles, f => f.EndsWith("Widgets.cs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Controllers.cs")]
    [InlineData("Conditional.cs")]
    public async Task Analyze_when_a_file_keeps_code_or_inactive_regions_then_it_is_not_deletable(string expectedFileName)
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain(actualReport.DeletableFiles, f => f.EndsWith(expectedFileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_when_a_partial_method_dies_then_both_parts_are_deleted()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedParts = 2;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedParts, actualReport.Symbols.Single(s => s.Fqn == "Fixture.Domain.WidgetFormatter.FormatWidget").Parts.Count);
    }

    [Fact]
    public async Task Analyze_when_a_file_with_inactive_code_loses_code_then_a_file_kept_warning_is_emitted()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedCount = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.Warnings.Count(w => w.Contains("file kept (contains inactive")));
    }

    [Theory]
    [InlineData("kept (not deleted): Fixture.Domain.InactiveUse", "inactive #if")]
    [InlineData("kept (not deleted): Fixture.Domain.DynamicTarget.Summarize", "dynamic")]
    public async Task Analyze_when_code_is_named_on_the_exclusion_list_then_a_kept_warning_names_it(string expectedPrefix, string expectedSource)
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(actualReport.Warnings, w => w.StartsWith(expectedPrefix, StringComparison.Ordinal) && w.Contains(expectedSource));
    }

    [Fact]
    public async Task Analyze_when_one_test_of_a_mixed_class_breaks_then_only_that_test_is_deleted()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedFqn = "Fixture.Tests.MixedTests.GetWidget_when_controller_is_removed_then_this_test_breaks";

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(new[] { expectedFqn }, Fqns(actualReport).Where(f => f.StartsWith("Fixture.Tests.MixedTests", StringComparison.Ordinal)));
        Assert.Contains(expectedFqn, Fqns(actualReport, Constants.CategoryBrokenTest));
    }

    [Fact]
    public async Task Analyze_when_widgets_controller_is_removed_then_verification_is_clean()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain(actualReport.Warnings, w => w.StartsWith("VERIFY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_when_a_converter_map_survives_then_the_chained_converter_is_not_orphaned()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain(Fqns(actualReport), f => f.Contains("GadgetConverter"));
    }

    [Fact]
    public async Task Analyze_when_a_class_only_inherits_broken_tests_then_it_is_a_broken_test()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedCategory = Constants.CategoryBrokenTest;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        var actualCategories = actualReport.Symbols.Where(s => s.Fqn.StartsWith("Fixture.Tests.WidgetCheckTests", StringComparison.Ordinal)).Select(s => s.Category).ToList();
        Assert.NotEmpty(actualCategories);
        Assert.All(actualCategories, c => Assert.Equal(expectedCategory, c));
    }

    [Fact]
    public async Task Analyze_when_a_view_mentions_an_action_of_a_deleted_controller_then_it_warns()
    {
        // Arrange
        var sut = WidgetsControllerRemoved;
        var expectedCount = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.Warnings.Count(w => w.Contains("WidgetsController.GetWidgetBadge is mentioned in") && w.Contains("Index.cshtml")));
    }

    // ------------------------------------------------ the fixture: member-level and overload removal

    [Theory]
    [InlineData("Fixture.Web.SecondaryHelper")]
    [InlineData("Fixture.Web.OverloadHelper")]
    public async Task Analyze_when_every_get_secondary_overload_is_removed_then_their_helpers_are_orphaned(string expectedFqn)
    {
        // Arrange
        var sut = GetSecondaryRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(expectedFqn, Fqns(actualReport, Constants.CategoryOrphaned));
    }

    [Theory]
    [InlineData("Fixture.Domain.WidgetService")]
    [InlineData("Fixture.Domain.GetWidgetHandler")]
    public async Task Analyze_when_get_secondary_is_removed_then_the_controllers_other_code_is_not_orphaned(string expectedFqn)
    {
        // Arrange
        var sut = GetSecondaryRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain(expectedFqn, Fqns(actualReport, Constants.CategoryOrphaned));
    }

    [Fact]
    public async Task Analyze_when_get_secondary_is_removed_then_only_it_is_listed_as_removed()
    {
        // Arrange
        var sut = GetSecondaryRemoved;
        var expectedRemoved = new[] { "Fixture.Web.WidgetsController.GetSecondary" };

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedRemoved, Fqns(actualReport, Constants.CategoryRemoved).Distinct());
    }

    [Fact]
    public async Task Analyze_when_one_overload_is_removed_by_signature_then_only_its_helper_is_orphaned()
    {
        // Arrange
        var sut = GetSecondaryIntRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains("Fixture.Web.OverloadHelper", Fqns(actualReport, Constants.CategoryOrphaned));
        Assert.DoesNotContain("Fixture.Web.SecondaryHelper", Fqns(actualReport, Constants.CategoryOrphaned));
        Assert.Single(actualReport.Symbols, s => s.Category == Constants.CategoryRemoved);
    }

    // ------------------------------------------------ the fixture: both controllers, plumbing cleanup

    [Theory]
    [InlineData("Fixture.Domain.MappingProfile")]
    [InlineData("Fixture.Domain.SharedService")]
    [InlineData("Fixture.Domain.GadgetConverter")]
    public async Task Analyze_when_both_controllers_are_removed_then_their_last_users_cascade(string expectedFqn)
    {
        // Arrange
        var sut = BothControllersRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(expectedFqn, Fqns(actualReport, Constants.CategoryOrphaned));
    }

    [Theory]
    [InlineData("Fixture.Web", "Fixture.Domain")]
    [InlineData("Fixture.Tests", "Fixture.Web")]
    public async Task Analyze_when_both_controllers_are_removed_then_unused_project_references_are_removable(string expectedProject, string expectedReference)
    {
        // Arrange
        var sut = BothControllersRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(actualReport.ProjectRefRemovals, r => r.Project == expectedProject && r.Reference == expectedReference);
    }

    [Fact]
    public async Task Analyze_when_both_controllers_are_removed_then_no_package_or_project_is_deleted()
    {
        // Arrange
        var sut = BothControllersRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Empty(actualReport.PackageRefRemovals);
        Assert.Empty(actualReport.DeletableProjects);
        Assert.DoesNotContain("Fixture.Domain.DeadLegacyHelper", Fqns(actualReport));
    }

    // ------------------------------------------------ the fixture: targets that stay referenced

    [Fact]
    public async Task Analyze_when_a_removal_target_is_still_used_then_still_referenced_names_the_referrer()
    {
        // Arrange
        var sut = SharedServiceRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        var actualWarnings = actualReport.Warnings.Where(w => w.StartsWith("STILL REFERENCED", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(actualWarnings);
        Assert.Contains(actualWarnings, w => w.Contains("GadgetsController") || w.Contains("GetGadget"));
    }

    [Fact]
    public async Task Analyze_when_a_removal_target_is_still_used_then_verification_catches_the_broken_plan()
    {
        // Arrange
        var sut = SharedServiceRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(actualReport.Warnings, w => w.StartsWith("VERIFY", StringComparison.Ordinal));
    }

    // ------------------------------------------------ the fixture: removing an implementation

    [Fact]
    public async Task Analyze_when_an_implementation_is_removed_then_its_registrations_die_with_warnings()
    {
        // Arrange
        var sut = WidgetServiceRemoved;
        var expectedCount = 2;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.Statements.Count(s => s.Snippet.Contains("IWidgetService")));
        Assert.Equal(expectedCount, actualReport.Warnings.Count(w => w.StartsWith("registration deleted although still in use", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Analyze_when_a_still_used_service_loses_its_only_registration_then_it_is_reported()
    {
        // Arrange
        var sut = WidgetServiceRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.True(actualReport.ServicesLeftUnregistered >= 1);
        Assert.Contains(actualReport.Warnings, w => w.StartsWith(Constants.ServiceLeftUnregisteredPrefix, StringComparison.Ordinal) && w.Contains("Fixture.Domain.IWidgetService"));
    }

    // ------------------------------------------------ the fixture: ForMember segments

    [Fact]
    public async Task Analyze_when_a_mapped_member_is_removed_then_only_its_for_member_segment_goes()
    {
        // Arrange
        var sut = AuditNoteRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains("Fixture.Domain.GadgetDto.AuditNote", Fqns(actualReport, Constants.CategoryRemoved));
        Assert.Single(actualReport.Statements, s => s.Kind.EndsWith("(segment)", StringComparison.Ordinal) && s.Snippet.Contains("AuditNote"));
        Assert.DoesNotContain(actualReport.Statements, s => s.Kind == "AutoMapper.CreateMap");
        Assert.Single(actualReport.Warnings, w => w.StartsWith("map configuration removed", StringComparison.Ordinal));
        Assert.Equal(0, actualReport.VerificationErrors);
        Assert.Equal(0, actualReport.StillReferencedTargets);
    }

    [Fact]
    public async Task Analyze_when_a_converter_a_live_mapping_uses_is_removed_then_it_is_still_referenced()
    {
        // Arrange
        var sut = GadgetConverterRemoved;
        var expectedCount = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.StillReferencedTargets);
        Assert.Contains(actualReport.Warnings, w => w.StartsWith("STILL REFERENCED: removing Fixture.Domain.GadgetConverter breaks the live mapping", StringComparison.Ordinal));
    }

    // ------------------------------------------------ the fixture: container escalation

    [Fact]
    public async Task Analyze_when_a_member_of_a_test_live_class_breaks_then_it_dies_alone()
    {
        // Arrange
        var sut = ObsoleteGizmoRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains("Fixture.Domain.CollateralHelper.Broken", Fqns(actualReport, Constants.CategoryBrokenOrphan));
        Assert.DoesNotContain("Fixture.Domain.CollateralHelper", Fqns(actualReport));
        Assert.DoesNotContain("Fixture.Domain.CollateralHelper.Useful", Fqns(actualReport));
        Assert.DoesNotContain(Fqns(actualReport), f => f.StartsWith("Fixture.Tests.CollateralHelperTests", StringComparison.Ordinal));
        Assert.Empty(actualReport.DeletableProjects);
        Assert.Equal(0, actualReport.VerificationErrors);
    }

    [Fact]
    public async Task Analyze_when_a_test_live_class_is_removed_explicitly_then_its_test_cascades()
    {
        // Arrange
        var sut = CollateralHelperRemoved;
        var expectedFqn = "Fixture.Tests.CollateralHelperTests";

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(expectedFqn, Fqns(actualReport, Constants.CategoryBrokenTest));
    }

    // ------------------------------------------------ the fixture: config keep patterns

    [Theory]
    [MemberData(nameof(KeepAnalyses))]
    public async Task Analyze_when_a_keep_pattern_matches_tests_then_they_survive_as_still_referenced(string keepKind)
    {
        // Arrange
        var sut = keepKind == "wildcard" ? WildcardKeepOfTests : ExactKeepOfTests;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain(Fqns(actualReport), f => f.StartsWith("Fixture.Tests.WidgetsControllerTests", StringComparison.Ordinal));
        Assert.Contains(actualReport.Warnings, w => w.StartsWith("STILL REFERENCED: config-kept Fixture.Tests.WidgetsControllerTests", StringComparison.Ordinal));
        Assert.True(actualReport.StillReferencedTargets >= 1);
    }

    public static IEnumerable<object[]> KeepAnalyses()
    {
        yield return new object[] { "wildcard" };
        yield return new object[] { "exact" };
    }

    // ------------------------------------------------ scenario: test-project classification

    [Fact]
    public async Task Analyze_when_projects_only_look_like_tests_then_all_are_production()
    {
        // Arrange
        var sut = TestClassificationScenario;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.True(actualReport.Stats.ProductionRoots >= 2);
        Assert.Equal(0, actualReport.Stats.TestRoots);
        Assert.DoesNotContain(actualReport.Warnings, w => w.StartsWith("Scratch.Tests:", StringComparison.Ordinal) || w.StartsWith("Scratch.Test.Tools:", StringComparison.Ordinal));
        Assert.Single(actualReport.Warnings, w => w.StartsWith("Scratch.Helpers: references a test library but contains no test", StringComparison.Ordinal));
    }

    // ------------------------------------------------ scenario: assembly attribute

    [Fact]
    public async Task Analyze_when_an_assembly_attribute_uses_a_library_then_its_reference_and_using_stay()
    {
        // Arrange
        var sut = AssemblyAttributeScenario;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Empty(actualReport.ProjectRefRemovals);
        Assert.Empty(actualReport.UsingRemovals);
        Assert.Equal(0, actualReport.VerificationErrors);
    }

    // ------------------------------------------------ scenario: AsImplementedInterfaces with IDisposable

    [Fact]
    public async Task Analyze_when_the_last_consumer_of_a_disposable_service_is_removed_then_the_cascade_completes()
    {
        // Arrange
        var sut = AutofacDisposableScenario;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains("Core.IZeta", Fqns(actualReport, Constants.CategoryOrphaned));
        Assert.Contains("Core.Zeta", Fqns(actualReport, Constants.CategoryOrphaned));
        Assert.NotEmpty(actualReport.Statements);
        Assert.Equal(0, actualReport.VerificationErrors);
    }

    // ------------------------------------------------ scenario: profiles and mapping extensions

    [Fact]
    public async Task Analyze_when_an_extension_is_called_only_from_a_mapping_chain_then_it_is_still_referenced()
    {
        // Arrange
        var sut = ShopDoomedRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(actualReport.Warnings, w => w.StartsWith("STILL REFERENCED", StringComparison.Ordinal) && w.Contains("IgnoreExtra"));
        Assert.DoesNotContain(Fqns(actualReport), f => f.Contains("IgnoreExtra"));
    }

    [Fact]
    public async Task Analyze_when_a_profile_writes_static_state_then_it_is_not_deleted_as_empty()
    {
        // Arrange
        var sut = ShopBetaControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Single(actualReport.Statements, s => s.Snippet.Contains("CreateMap<Beta, BetaDto>"));
        Assert.DoesNotContain(Fqns(actualReport), f => f.StartsWith("Shop.CultureProfile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_when_a_declaration_starts_inside_a_multi_line_string_then_its_marker_line_is_flagged()
    {
        // Arrange
        var sut = ShopBetaControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.True(actualReport.Symbols.Single(s => s.Fqn == "Shop.Holder.Dead").Parts[0].MarkerLineInsideToken);
    }

    [Fact]
    public async Task Analyze_when_a_solution_declares_its_own_profile_base_then_subclasses_are_ordinary()
    {
        // Arrange
        var sut = PlainProfileScenario;
        var expectedCategory = Constants.CategoryBrokenOrphan;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCategory, actualReport.Symbols.Single(s => s.Fqn == "Plain.AdminProfile").Category);
        Assert.Equal(0, actualReport.StillReferencedTargets);
    }

    // ------------------------------------------------ scenario: legacy frameworks as external assemblies

    [Fact]
    public async Task Analyze_when_legacy_framework_entry_points_use_a_helper_then_none_of_them_is_deleted()
    {
        // Arrange
        var sut = LegacyAdminRemoved;
        var expectedFqns = new[] { "Web.Counter.Set", "Web.Heartbeat.Stop", "Web.HomeController.Admin", "Web.Mid.Drop" };

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedFqns, Fqns(actualReport).OrderBy(f => f, StringComparer.Ordinal));
        Assert.Empty(actualReport.Statements);
    }

    [Fact]
    public async Task Analyze_when_a_field_is_left_unassigned_under_warnings_as_errors_then_verification_catches_it()
    {
        // Arrange
        var sut = LegacyAdminRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(actualReport.Warnings, w => w.StartsWith("VERIFY", StringComparison.Ordinal) && w.Contains("CS0649"));
        Assert.DoesNotContain(actualReport.Warnings, w => w.Contains("CS1040"));
    }

    [Fact]
    public async Task Analyze_when_a_module_calls_a_base_module_helper_then_it_is_not_deleted_as_empty()
    {
        // Arrange
        var sut = LegacyOrdersControllerRemoved;
        var expectedStatements = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain("Web.OrdersModule", Fqns(actualReport));
        Assert.DoesNotContain("Web.Repo", Fqns(actualReport));
        Assert.Contains("Web.OrderSvc", Fqns(actualReport));
        Assert.Equal(expectedStatements, actualReport.Statements.Count);
    }

    [Fact]
    public async Task Analyze_when_a_scan_registers_by_name_filter_then_matching_classes_with_used_interfaces_stay()
    {
        // Arrange
        var sut = LegacyScanBuildRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain("Web.AuditService", Fqns(actualReport));
        Assert.DoesNotContain("Web.OldAuditService", Fqns(actualReport));
        Assert.Contains("Web.AuditWriter", Fqns(actualReport));
        Assert.Contains("Web.ReportService", Fqns(actualReport));
        Assert.Contains("Web.IReportService", Fqns(actualReport));
    }

    [Fact]
    public async Task Analyze_when_a_framework_built_orphan_breaks_then_it_is_flagged_and_a_plain_one_is_not()
    {
        // Arrange
        var sut = LegacyJobsControllerRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        var actualHandler = actualReport.Symbols.Single(s => s.Fqn == "Web.OrderPlacedHandler");
        var actualPlain = actualReport.Symbols.Single(s => s.Fqn == "Web.PlainOld");
        Assert.Equal(Constants.CategoryBrokenOrphan, actualHandler.Category);
        Assert.True(actualHandler.Review);
        Assert.Equal(Constants.CategoryBrokenOrphan, actualPlain.Category);
        Assert.False(actualPlain.Review);
    }

    // ------------------------------------------------ scenario: shared namespace across projects

    [Fact]
    public async Task Analyze_when_a_namespace_dies_in_the_referenced_assembly_then_the_consumers_using_goes()
    {
        // Arrange
        var sut = SharedNamespaceScenario;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains("Shared.Helpers.BHelper", Fqns(actualReport));
        Assert.Contains(actualReport.UsingRemovals, u => u.Namespace == "Shared.Helpers" && u.File.EndsWith("A/Program.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(actualReport.Warnings, w => w.Contains("no textually matching <Compile> entry"));
    }

    [Fact]
    public async Task Analyze_when_the_solution_holds_a_web_site_project_then_it_is_counted()
    {
        // Arrange
        var sut = SharedNamespaceWithWebSite;
        var expectedCount = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.WebsiteProjects);
    }

    // ------------------------------------------------ scenario: scan styles and conventions

    [Fact]
    public async Task Analyze_when_scans_conventions_and_construction_side_effects_use_helpers_then_they_keep_their_code()
    {
        // Arrange
        var sut = ScanStylesScenario;
        var expectedFqns = new[] { "Web.AuditFilter.OldLog", "Web.Heartbeat.Stop", "Web.HelperX", "Web.HelperY", "Web.HomeController.Admin", "Web.NightlyTask.Execute", "Web.Poller.Stop" };

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedFqns, Fqns(actualReport).OrderBy(f => f, StringComparer.Ordinal));
        Assert.Empty(actualReport.Statements);
    }

    [Fact]
    public async Task Analyze_when_a_container_built_class_has_a_dead_method_then_it_is_held_for_review()
    {
        // Arrange
        var sut = ScanStylesScenario;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        var actualSymbol = actualReport.Symbols.Single(s => s.Fqn == "Web.NightlyTask.Execute");
        Assert.Equal(Constants.CategoryBrokenOrphan, actualSymbol.Category);
        Assert.True(actualSymbol.Review);
    }

    [Fact]
    public async Task Analyze_when_a_class_the_code_creates_has_a_dead_method_then_it_is_deleted_without_review()
    {
        // Arrange
        var sut = ScanStylesScenario;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        var actualSymbol = actualReport.Symbols.Single(s => s.Fqn == "Web.AuditFilter.OldLog");
        Assert.Equal(Constants.CategoryBrokenOrphan, actualSymbol.Category);
        Assert.False(actualSymbol.Review);
    }

    // ------------------------------------------------ scenario: project plumbing

    [Fact]
    public async Task Analyze_when_a_referenced_project_still_holds_entry_points_then_its_reference_is_kept()
    {
        // Arrange
        var sut = ProjectPlumbingScenario;
        var expectedWarnings = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains("Lib.LibUtil", Fqns(actualReport));
        Assert.Empty(actualReport.ProjectRefRemovals);
        Assert.Equal(expectedWarnings, actualReport.Warnings.Count(w => w.Contains("ProjectReference Lib") && w.Contains("entry points")));
    }

    [Fact]
    public async Task Analyze_when_a_linked_file_provides_a_namespace_through_its_second_project_then_the_using_stays()
    {
        // Arrange
        var sut = ProjectPlumbingScenario;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain(actualReport.UsingRemovals, u => u.Namespace == "LinkedNs");
    }

    [Fact]
    public async Task Analyze_when_the_test_library_comes_from_directory_build_props_then_the_project_is_a_test_project()
    {
        // Arrange
        var sut = ProjectPlumbingScenario;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        var actualSymbol = actualReport.Symbols.Single(s => s.Fqn == "Tst.FeatureTests");
        Assert.Equal(Constants.CategoryBrokenTest, actualSymbol.Category);
        Assert.True(actualSymbol.Review);
    }

    [Fact]
    public async Task Analyze_when_warnings_as_errors_is_nullable_then_a_new_cs8618_fails_verification()
    {
        // Arrange
        var sut = NullableScenario;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains(actualReport.Warnings, w => w.StartsWith("VERIFY", StringComparison.Ordinal) && w.Contains("CS8618"));
    }

    // ------------------------------------------------ scenario: runtime wiring

    [Fact]
    public async Task Analyze_when_a_service_loses_its_only_registration_then_each_uncovered_service_is_reported()
    {
        // Arrange
        var sut = RuntimeWiringRemovals;
        var expectedCount = 2;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        var actualWarnings = actualReport.Warnings.Where(w => w.StartsWith(Constants.ServiceLeftUnregisteredPrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(expectedCount, actualReport.ServicesLeftUnregistered);
        Assert.Contains(actualWarnings, w => w.Contains("Api.IA "));
        Assert.Contains(actualWarnings, w => w.Contains("Api.IRepository"));
    }

    [Theory]
    [InlineData("Api.IB")]
    [InlineData("Api.IC")]
    [InlineData("Api.IValidator")]
    public async Task Analyze_when_another_live_registration_registers_the_same_service_then_it_is_not_reported(string expectedCovered)
    {
        // Arrange
        var sut = RuntimeWiringRemovals;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.DoesNotContain(actualReport.Warnings, w => w.StartsWith(Constants.ServiceLeftUnregisteredPrefix, StringComparison.Ordinal) && w.Contains(expectedCovered));
    }

    [Theory]
    [InlineData("Api.GreeterService.SayHello")]
    [InlineData("Api.GreetingFormatter")]
    [InlineData("Api.Greeter.BindService")]
    public async Task Analyze_when_a_generated_binder_is_named_in_an_attribute_then_the_service_override_stays(string expectedSurvivor)
    {
        // Arrange
        var sut = RuntimeWiringRemovals;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.False(TestSolutions.IsDeleted(actualReport, expectedSurvivor));
    }

    [Fact]
    public async Task Analyze_when_an_unused_field_shares_a_declaration_with_a_used_one_then_what_it_references_stays()
    {
        // Arrange
        var sut = RuntimeWiringRemovals;
        var expectedSurvivor = "Api.Old";

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.False(TestSolutions.IsDeleted(actualReport, expectedSurvivor));
        Assert.DoesNotContain(actualReport.Warnings, w => w.StartsWith("STILL REFERENCED", StringComparison.Ordinal));
        Assert.Equal(0, actualReport.StillReferencedTargets);
    }

    [Fact]
    public async Task Analyze_when_a_view_names_a_type_of_a_referenced_project_then_the_reference_stays()
    {
        // Arrange
        var sut = RuntimeWiringRemovals;
        var expectedWarning = "Web: ProjectReference Lib lost its last compile-time user but a view names Lib.Models.MenuVm";

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Contains("Lib.Helper", Fqns(actualReport));
        Assert.DoesNotContain(actualReport.ProjectRefRemovals, r => r.Project == "Web");
        Assert.Contains(actualReport.Warnings, w => w.StartsWith(expectedWarning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_when_tests_use_a_derived_test_attribute_then_they_count_as_tests()
    {
        // Arrange
        var sut = RuntimeWiringRemovals;
        var expectedCategory = Constants.CategoryBrokenTest;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        var actualBroken = actualReport.Symbols.Single(s => s.Fqn == "T.CustomTests.Uses_doomed_and_keep");
        Assert.Equal(expectedCategory, actualBroken.Category);
        Assert.True(actualBroken.Review);
        Assert.False(TestSolutions.IsDeleted(actualReport, "T.CustomTests.Healthy"));
    }

    [Fact]
    public async Task Analyze_when_a_container_built_class_is_created_only_by_tests_or_dead_code_then_its_dead_method_is_held_for_review()
    {
        // Arrange
        var sut = RuntimeWiringRemovals;
        var expectedCategory = Constants.CategoryBrokenOrphan;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        var actualSymbol = actualReport.Symbols.Single(s => s.Fqn == "Api.Reporter.Old");
        Assert.Equal(expectedCategory, actualSymbol.Category);
        Assert.True(actualSymbol.Review);
    }

    [Fact]
    public async Task Analyze_when_a_map_from_source_of_a_live_mapping_is_removed_then_it_is_still_referenced()
    {
        // Arrange
        var sut = MapSourceRemoved;
        var expectedCount = 1;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(expectedCount, actualReport.StillReferencedTargets);
        Assert.Contains(actualReport.Warnings, w => w.StartsWith("STILL REFERENCED: removing Api.Order.ComputeTotal breaks the live mapping", StringComparison.Ordinal) && w.Contains(".ForMember"));
    }

    [Fact]
    public async Task Analyze_when_the_destination_member_a_mapping_configures_is_removed_then_only_its_segment_goes()
    {
        // Arrange
        var sut = MapDestinationRemoved;

        // Act
        var actualReport = await sut.Analyze();

        // Assert
        Assert.Equal(0, actualReport.StillReferencedTargets);
        Assert.Single(actualReport.Statements, s => s.Kind.EndsWith("(segment)", StringComparison.Ordinal) && s.Snippet.Contains("Total"));
        Assert.Equal(0, actualReport.VerificationErrors);
    }

    [Fact]
    public async Task Analyze_when_a_dying_namespace_is_imported_by_a_generated_global_using_then_the_generated_file_is_not_edited()
    {
        // Arrange
        var solution = TestSolutions.FreshScenario("RuntimeWiring");
        var generated = Path.Combine(Path.GetDirectoryName(solution), TestConstants.GeneratedGlobalUsingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(generated));
        File.WriteAllText(generated, "global using global::Api.Feature;\n");
        var expectedWarning = "manual: the using of Api.Feature in generated";

        // Act
        var actualReport = await EditRuns.AnalyzeFresh(solution, new AnalyzerConfig(), "Api.Feature.FeatureController");

        // Assert
        Assert.DoesNotContain(actualReport.UsingRemovals, u => u.File.Contains("/obj/", StringComparison.Ordinal));
        Assert.Contains(actualReport.Warnings, w => w.StartsWith(expectedWarning, StringComparison.Ordinal));
        Assert.True(actualReport.VerificationErrors > 0);
    }

    // ------------------------------------------------ helpers

    List<string> Fqns(DeletionReport report, string category = null)
    {
        return report.Symbols.Where(s => category == null || s.Category == category).Select(s => s.Fqn).ToList();
    }
}
