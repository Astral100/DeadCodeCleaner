using Fixture.Domain;

namespace Fixture.Web;

public static class Program
{
    public static void Main()
    {
        var services = new ServiceCollection();
        // Chained with an UNTRACKED call: deleting the registration must keep AddFixtureLogging().
        services.AddScoped<IWidgetService, WidgetService>().AddFixtureLogging();
        services.AddScoped<IGadgetService, GadgetService>();
    }
}

// Name-based stand-in for Microsoft.Extensions.DependencyInjection.
public class ServiceCollection
{
    public ServiceCollection AddScoped<TService, TImplementation>() where TImplementation : TService
    {
        return this;
    }

    public ServiceCollection AddFixtureLogging()
    {
        return this;
    }
}
