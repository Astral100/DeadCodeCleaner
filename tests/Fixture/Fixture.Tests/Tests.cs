using Fixture.Domain;
using Fixture.Web;

namespace Fixture.Tests;

// Name-based stand-in for xunit's [Fact].
public class FactAttribute : Attribute { }

public class WidgetsControllerTests
{
    [Fact]
    public void GetWidget_Works()
    {
        var controller = new WidgetsController(new Mediator(), new WidgetService());
        controller.GetWidget();
        TestData.Seed();
    }
}

public class SharedServiceTests
{
    [Fact]
    public void Prefix_Works()
    {
        _ = SharedService.Prefix();
    }
}

public class MixedTests
{
    // References dying code (WidgetService) AND surviving code (SharedService): must be deleted
    // but flagged for review.
    [Fact]
    public void Mixed()
    {
        _ = SharedService.Prefix() + new WidgetService().Describe();
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
