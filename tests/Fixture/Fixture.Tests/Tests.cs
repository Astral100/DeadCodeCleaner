using Fixture.Domain;
using Fixture.Web;

namespace Fixture.Tests;

// Name-based stand-in for xunit's [Fact].
public class FactAttribute : Attribute { }

public class WidgetsControllerTests
{
    [Fact]
    public void GetWidget_when_called_then_widget_is_returned()
    {
        // Arrange
        TestData.Seed();
        var sut = new WidgetsController(new Mediator(), new WidgetService());

        // Act
        var actualWidget = sut.GetWidget();

        // Assert
        _ = actualWidget;
    }
}

public class SharedServiceTests
{
    [Fact]
    public void Prefix_when_called_then_prefix_is_returned()
    {
        // Arrange
        var expectedPrefix = "shared:";

        // Act
        var actualPrefix = SharedService.Prefix();

        // Assert
        _ = expectedPrefix == actualPrefix;
    }
}

public class WidgetServiceTests
{
    // References dying code (WidgetService) AND surviving code (SharedService): must be deleted
    // but flagged for review.
    [Fact]
    public void Describe_when_called_then_description_has_prefix()
    {
        // Arrange
        var expectedPrefix = SharedService.Prefix();
        var sut = new WidgetService();

        // Act
        var actualDescription = sut.Describe();

        // Assert
        _ = actualDescription.StartsWith(expectedPrefix);
    }
}

// One broken test must not delete its healthy sibling or the class.
public class MixedTests
{
    [Fact]
    public void GetWidget_when_controller_is_removed_then_this_test_breaks()
    {
        // Arrange
        var sut = new WidgetsController(new Mediator(), new WidgetService());

        // Act
        var actualWidget = sut.GetWidget();

        // Assert
        _ = actualWidget;
    }

    [Fact]
    public void Prefix_when_called_then_this_test_stays_healthy()
    {
        // Arrange
        var expectedPrefix = "shared:";

        // Act
        var actualPrefix = SharedService.Prefix();

        // Assert
        _ = expectedPrefix == actualPrefix;
    }
}

// Used only by WidgetsControllerTests: must cascade when that test dies.
public static class TestData
{
    public static void Seed() { }
}

// Pre-existing dead test utility: referenced by nothing, must stay untouched.
public static class UnusedTestUtil
{
    public static void Noop() { }
}
