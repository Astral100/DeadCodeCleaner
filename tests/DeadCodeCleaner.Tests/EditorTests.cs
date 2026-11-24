using System.Text;
using Xunit;

namespace DeadCodeCleaner.Tests;

public class EditorTests
{
    public EditorTests()
    {
        _ = EditRuns.MarkTwiceThenDeleteWidgetsController.Value;
        _ = EditRuns.DeleteWidgetServiceAndEnumMember.Value;
        _ = EditRuns.DeleteAuditNote.Value;
        _ = EditRuns.MarkShopBetaController.Value;
        _ = EditRuns.DeleteWidgetsControllerFromEncodedFiles.Value;
        _ = EditRuns.DeleteFeatureController.Value;
    }

    // ------------------------------------------------ mark, mark again, delete (tests/Fixture)

    [Fact]
    public async Task Mark_when_a_controller_is_marked_then_markers_are_inserted()
    {
        // Arrange
        var sut = EditRuns.MarkTwiceThenDeleteWidgetsController;
        var expectedFile = Path.Combine("Fixture.Web", "Controllers.cs");

        // Act
        var actualRun = await sut.Value;

        // Assert
        Assert.Contains(expectedFile, actualRun.MarkedFiles);
    }

    [Fact]
    public async Task Mark_when_run_twice_then_the_second_run_changes_nothing()
    {
        // Arrange
        var sut = EditRuns.MarkTwiceThenDeleteWidgetsController;

        // Act
        var actualRun = await sut.Value;

        // Assert
        Assert.Equal(actualRun.AfterFirstMark, actualRun.AfterSecondMark);
    }

    [Fact]
    public async Task Delete_when_marked_code_is_deleted_then_no_marker_is_left()
    {
        // Arrange
        var sut = EditRuns.MarkTwiceThenDeleteWidgetsController;

        // Act
        var actualRun = await sut.Value;

        // Assert
        Assert.Equal(0, actualRun.DeleteExitCode);
        var actualMarked = Directory.EnumerateFiles(actualRun.Directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(TestConstants.Marker))
            .ToList();
        Assert.Empty(actualMarked);
    }

    [Fact]
    public async Task Delete_when_a_controller_is_deleted_then_its_files_and_registration_segment_go()
    {
        // Arrange
        var sut = EditRuns.MarkTwiceThenDeleteWidgetsController;

        // Act
        var actualRun = await sut.Value;

        // Assert
        Assert.False(actualRun.FileExists(Path.Combine("Fixture.Domain", "Widgets.cs")));
        var actualProgram = actualRun.ReadFile(Path.Combine("Fixture.Web", "Program.cs"));
        Assert.DoesNotContain("IWidgetService", actualProgram);
        Assert.Contains("services.AddFixtureLogging();", actualProgram);
    }

    [Theory]
    [MemberData(nameof(DeleteRuns))]
    public async Task Delete_when_the_plan_is_applied_then_the_solution_still_compiles(string runName)
    {
        // Arrange
        var sut = DeleteRun(runName);

        // Act
        var actualRun = await sut.Value;

        // Assert
        Assert.Equal(0, actualRun.DeleteExitCode);
        Assert.Empty(actualRun.CompileErrorsAfter);
    }

    public static IEnumerable<object[]> DeleteRuns()
    {
        yield return new object[] { "WidgetsController" };
        yield return new object[] { "WidgetServiceAndEnumMember" };
        yield return new object[] { "AuditNote" };
    }

    // ------------------------------------------------ an implementation and an enum member (tests/Fixture)

    [Fact]
    public async Task Delete_when_an_enum_member_is_deleted_then_the_following_member_stays_and_a_renumber_warning_is_emitted()
    {
        // Arrange
        var sut = EditRuns.DeleteWidgetServiceAndEnumMember;

        // Act
        var actualRun = await sut.Value;

        // Assert
        var actualShapes = actualRun.ReadFile(Path.Combine("Fixture.Domain", "Shapes.cs"));
        Assert.DoesNotContain("    Fancy", actualShapes);
        Assert.Contains("Legacy,", actualShapes);
        Assert.Contains(actualRun.Report.Warnings, w => w.StartsWith("enum values shift", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_when_an_implementation_is_deleted_then_its_registration_goes_and_untracked_calls_stay()
    {
        // Arrange
        var sut = EditRuns.DeleteWidgetServiceAndEnumMember;

        // Act
        var actualRun = await sut.Value;

        // Assert
        var actualProgram = actualRun.ReadFile(Path.Combine("Fixture.Web", "Program.cs"));
        Assert.DoesNotContain("AddScoped<IWidgetService", actualProgram);
        Assert.Contains("AddFixtureLogging", actualProgram);
    }

    // ------------------------------------------------ a ForMember segment (tests/Fixture)

    [Fact]
    public async Task Delete_when_a_mapped_member_is_deleted_then_only_its_segment_leaves_the_chain()
    {
        // Arrange
        var sut = EditRuns.DeleteAuditNote;

        // Act
        var actualRun = await sut.Value;

        // Assert
        var actualMapping = actualRun.ReadFile(Path.Combine("Fixture.Domain", "Mapping.cs"));
        Assert.DoesNotContain("ForMember(d => d.AuditNote)", actualMapping);
        Assert.Contains("ConvertVia<GadgetConverter>", actualMapping);
    }

    // ------------------------------------------------ a marker inside a multi-line string (scenario ShopProfiles)

    [Fact]
    public async Task Mark_when_a_declaration_starts_inside_a_multi_line_string_then_the_string_stays_intact()
    {
        // Arrange
        var sut = EditRuns.MarkShopBetaController;
        var expectedNextLine = "second\"; public static void Dead";

        // Act
        var actualRun = await sut.Value;

        // Assert
        var actualLines = actualRun.ReadFile(Path.Combine("Shop", "Shop.cs")).Split('\n');
        var actualIndex = Array.FindIndex(actualLines, l => l.Contains("Banner = @\"first"));
        Assert.StartsWith(expectedNextLine, actualLines[actualIndex + 1]);
        Assert.Contains(actualLines, l => l.Contains(TestConstants.Marker));
    }

    // ------------------------------------------------ safety of the edit itself

    [Fact]
    public async Task Delete_when_a_file_changed_after_the_analysis_then_nothing_is_written()
    {
        // Arrange
        var solution = TestSolutions.FreshFixture();
        var dir = Path.GetDirectoryName(solution);
        var report = await EditRuns.AnalyzeFresh(solution, TestSolutions.FixtureConfig(), "Fixture.Web.WidgetsController");
        File.AppendAllText(Path.Combine(dir, TestConstants.FixtureControllersPath), "// edited after the analysis\n");
        var expectedSnapshot = TestSolutions.Snapshot(dir);
        var expectedExit = 1;

        // Act
        var actualExit = Editor.Delete(report, _ => { });

        // Assert
        Assert.Equal(expectedExit, actualExit);
        Assert.Equal(expectedSnapshot, TestSolutions.Snapshot(dir));
    }

    [Fact]
    public async Task Delete_when_files_are_saved_in_windows_1252_or_with_a_bom_then_they_keep_their_encoding()
    {
        // Arrange
        var sut = EditRuns.DeleteWidgetsControllerFromEncodedFiles;
        var expectedAccent = Encoding.Latin1.GetBytes("café");
        var expectedPreamble = Encoding.UTF8.GetPreamble();

        // Act
        var actualRun = await sut.Value;

        // Assert
        Assert.Equal(0, actualRun.DeleteExitCode);
        var actualControllers = File.ReadAllBytes(Path.Combine(actualRun.Directory, TestConstants.FixtureControllersPath));
        Assert.True(actualControllers.AsSpan().IndexOf(expectedAccent) >= 0);
        Assert.DoesNotContain("class WidgetsController", Encoding.Latin1.GetString(actualControllers));
        var actualProgram = File.ReadAllBytes(Path.Combine(actualRun.Directory, TestConstants.FixtureProgramPath));
        Assert.Equal(expectedPreamble, actualProgram.Take(expectedPreamble.Length));
        Assert.DoesNotContain("IWidgetService", Encoding.UTF8.GetString(actualProgram));
        Assert.Empty(actualRun.CompileErrorsAfter);
    }

    [Fact]
    public async Task Delete_when_a_projects_last_user_is_deleted_then_the_project_leaves_the_solution_and_the_disk()
    {
        // Arrange
        var sut = EditRuns.DeleteFeatureController;

        // Act
        var actualRun = await sut.Value;

        // Assert
        Assert.Equal(0, actualRun.DeleteExitCode);
        Assert.False(Directory.Exists(Path.Combine(actualRun.Directory, "Feature")));
        Assert.DoesNotContain("Feature.csproj", actualRun.ReadFile(TestConstants.ScenarioSolutionFileName));
        var actualApp = actualRun.ReadFile(Path.Combine("App", "App.csproj"));
        Assert.DoesNotContain("FeatureController.cs", actualApp);
        Assert.DoesNotContain("Feature.csproj", actualApp);
        Assert.Empty(actualRun.CompileErrorsAfter);
    }

    Lazy<Task<EditRun>> DeleteRun(string name)
    {
        return name switch
        {
            "WidgetsController" => EditRuns.MarkTwiceThenDeleteWidgetsController,
            "WidgetServiceAndEnumMember" => EditRuns.DeleteWidgetServiceAndEnumMember,
            _ => EditRuns.DeleteAuditNote,
        };
    }
}
