using Xunit;

namespace DeadCodeCleaner.Tests;

/// <summary>delete's refusals and overrides through the command line entry point, each on a private
/// copy. Output goes to per-test writers, never the process console, so these run in parallel.</summary>
public class ProgramTests
{
    [Fact]
    public async Task Run_when_review_flagged_tests_exist_then_delete_refuses_and_deletes_nothing()
    {
        // Arrange
        var solution = TestSolutions.FreshFixture();
        var expectedSnapshot = TestSolutions.Snapshot(Path.GetDirectoryName(solution));
        var args = DeleteArgs(solution, TestSolutions.FixtureConfigPath, "Fixture.Web.WidgetsController");
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunProgram(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("also reference surviving code", actualError);
        Assert.Equal(expectedSnapshot, TestSolutions.Snapshot(Path.GetDirectoryName(solution)));
    }

    [Fact]
    public async Task Run_when_a_still_used_service_would_lose_its_only_registration_then_delete_refuses()
    {
        // Arrange
        var solution = TestSolutions.FreshFixture();
        var dir = Path.GetDirectoryName(solution);
        var args = DeleteArgs(solution, TestSolutions.FixtureConfigPath, "Fixture.Domain.WidgetService", "Fixture.Domain.WidgetKind.Fancy").Append("--delete-review-tests").ToArray();
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunProgram(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("lose their only registration", actualError);
        Assert.Contains("class WidgetService", File.ReadAllText(Path.Combine(dir, "Fixture.Domain", "Widgets.cs")));
    }

    [Fact]
    public async Task Run_when_unregistered_services_and_review_tests_are_allowed_then_delete_applies_the_plan()
    {
        // Arrange
        var solution = TestSolutions.FreshFixture();
        var dir = Path.GetDirectoryName(solution);
        var args = DeleteArgs(solution, TestSolutions.FixtureConfigPath, "Fixture.Domain.WidgetService", "Fixture.Domain.WidgetKind.Fancy")
            .Append("--delete-review-tests").Append("--allow-unregistered-services").ToArray();
        var expectedExit = 0;

        // Act
        var (actualExit, _) = await RunProgram(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.DoesNotContain("class WidgetService", File.ReadAllText(Path.Combine(dir, "Fixture.Domain", "Widgets.cs")));
        Assert.Contains("\"Fixture.Domain.WidgetService\"", File.ReadAllText(Path.Combine(dir, TestConstants.ReportFileName)));
        Assert.Empty(await TestSolutions.CompileErrors(solution));
    }

    [Fact]
    public async Task Run_when_a_framework_built_orphan_awaits_review_then_delete_refuses()
    {
        // Arrange
        var solution = TestSolutions.FreshScenario("LegacyExternal");
        var dir = Path.GetDirectoryName(solution);
        var args = DeleteArgs(solution, null, "Web.JobsController");
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunProgram(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("may be created or called by a framework", actualError);
        Assert.Contains("class OrderPlacedHandler", File.ReadAllText(Path.Combine(dir, "Web", "App.cs")));
    }

    [Fact]
    public async Task Run_when_review_orphans_are_allowed_then_delete_removes_them()
    {
        // Arrange
        var solution = TestSolutions.FreshScenario("LegacyExternal");
        var dir = Path.GetDirectoryName(solution);
        var args = DeleteArgs(solution, null, "Web.JobsController").Append("--delete-review-orphans").ToArray();
        var expectedExit = 0;

        // Act
        var (actualExit, actualError) = await RunProgram(args);

        // Assert
        Assert.True(expectedExit == actualExit, actualError);
        Assert.DoesNotContain("class OrderPlacedHandler", File.ReadAllText(Path.Combine(dir, "Web", "App.cs")));
    }

    [Fact]
    public async Task Run_when_the_solution_holds_a_web_site_project_then_delete_refuses()
    {
        // Arrange
        var solution = TestSolutions.ScenarioWithWebSite("SharedNamespace");
        var dir = Path.GetDirectoryName(solution);
        var args = DeleteArgs(solution, null, "AApp.FeatureController");
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunProgram(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("Web Site project", actualError);
        Assert.True(File.Exists(Path.Combine(dir, "A", "FeatureController.cs")));
    }

    [Fact]
    public async Task Run_when_a_removal_target_is_still_referenced_then_delete_refuses()
    {
        // Arrange
        var solution = TestSolutions.FreshFixture();
        var expectedSnapshot = TestSolutions.Snapshot(Path.GetDirectoryName(solution));
        var args = DeleteArgs(solution, TestSolutions.FixtureConfigPath, "Fixture.Domain.SharedService");
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunProgram(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("still referenced by surviving code", actualError);
        Assert.Equal(expectedSnapshot, TestSolutions.Snapshot(Path.GetDirectoryName(solution)));
    }

    [Fact]
    public async Task Run_when_a_project_has_compile_errors_then_delete_refuses()
    {
        // Arrange
        var solution = TestSolutions.FreshScenario("PlainProfile");
        var dir = Path.GetDirectoryName(solution);
        File.WriteAllText(Path.Combine(dir, "Plain", "Broken.cs"), "namespace Plain { public class Broken { public void M() { Undefined(); } } }\n");
        var expectedSnapshot = TestSolutions.Snapshot(dir);
        var args = DeleteArgs(solution, null, "Plain.Doomed");
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunProgram(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("had compile errors during analysis", actualError);
        Assert.Equal(expectedSnapshot, TestSolutions.Snapshot(dir));
    }

    [Fact]
    public async Task Run_when_no_production_entry_point_exists_then_delete_refuses()
    {
        // Arrange
        var solution = TestSolutions.FreshScenario("NoRoots");
        var dir = Path.GetDirectoryName(solution);
        var expectedSnapshot = TestSolutions.Snapshot(dir);
        var args = DeleteArgs(solution, null, "Lib.Alpha");
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunProgram(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("no production entry points were detected", actualError);
        Assert.Equal(expectedSnapshot, TestSolutions.Snapshot(dir));
    }

    string[] DeleteArgs(string solution, string configPath, params string[] targets)
    {
        var args = new List<string> { "delete", "--solution", solution, "--json", Path.Combine(Path.GetDirectoryName(solution), TestConstants.ReportFileName) };
        if (configPath != null) { args.AddRange(new[] { "--config", configPath }); }
        foreach (var t in targets) { args.AddRange(new[] { "--remove", t }); }
        return args.ToArray();
    }

    async Task<(int Exit, string Error)> RunProgram(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Program.Run(args, output, error);
        return (exit, error.ToString());
    }
}
