using Xunit;

namespace DeadCodeCleaner.Tests;

/// <summary>delete's refusals through the real entry point. Console output is captured, so this is the
/// only class that runs the program; the other test classes never write to the console.</summary>
public class ProgramTests
{
    [Fact]
    public async Task Main_when_review_flagged_tests_exist_then_delete_refuses_and_deletes_nothing()
    {
        // Arrange
        var solution = TestSolutions.FreshFixture();
        var dir = Path.GetDirectoryName(solution);
        var args = DeleteArgs(solution, TestSolutions.FixtureConfigPath, "Fixture.Web.WidgetsController");

        // Act
        var (actualExit, _) = await RunMain(args);

        // Assert
        Assert.NotEqual(0, actualExit);
        Assert.True(File.Exists(Path.Combine(dir, "Fixture.Domain", "Widgets.cs")));
    }

    [Fact]
    public async Task Main_when_a_still_used_service_would_lose_its_only_registration_then_delete_refuses()
    {
        // Arrange
        var solution = TestSolutions.FreshFixture();
        var dir = Path.GetDirectoryName(solution);
        var args = DeleteArgs(solution, TestSolutions.FixtureConfigPath, "Fixture.Domain.WidgetService", "Fixture.Domain.WidgetKind.Fancy").Append("--delete-review-tests").ToArray();
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunMain(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("lose their only registration", actualError);
        Assert.Contains("class WidgetService", File.ReadAllText(Path.Combine(dir, "Fixture.Domain", "Widgets.cs")));
    }

    [Fact]
    public async Task Main_when_a_framework_built_orphan_awaits_review_then_delete_refuses()
    {
        // Arrange
        var solution = TestSolutions.FreshScenario("LegacyExternal");
        var dir = Path.GetDirectoryName(solution);
        var args = DeleteArgs(solution, null, "Web.JobsController");
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunMain(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("may be created or called by a framework", actualError);
        Assert.Contains("class OrderPlacedHandler", File.ReadAllText(Path.Combine(dir, "Web", "App.cs")));
    }

    [Fact]
    public async Task Main_when_the_solution_holds_a_web_site_project_then_delete_refuses()
    {
        // Arrange
        var solution = TestSolutions.ScenarioWithWebSite("SharedNamespace");
        var dir = Path.GetDirectoryName(solution);
        var args = DeleteArgs(solution, null, "AApp.FeatureController");
        var expectedExit = 1;

        // Act
        var (actualExit, actualError) = await RunMain(args);

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Contains("Web Site project", actualError);
        Assert.True(File.Exists(Path.Combine(dir, "A", "FeatureController.cs")));
    }

    string[] DeleteArgs(string solution, string configPath, params string[] targets)
    {
        var args = new List<string> { "delete", "--solution", solution, "--json", Path.Combine(Path.GetDirectoryName(solution), "report.json") };
        if (configPath != null) { args.AddRange(new[] { "--config", configPath }); }
        foreach (var t in targets) { args.AddRange(new[] { "--remove", t }); }
        return args.ToArray();
    }

    async Task<(int Exit, string Error)> RunMain(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exit = await Program.Main(args);
            return (exit, error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
