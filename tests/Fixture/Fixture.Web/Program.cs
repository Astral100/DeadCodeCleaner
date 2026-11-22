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
        // typeof form: the types must not be pinned by the bound-tree walk, or this can never be cleaned up.
        services.AddScoped(typeof(IWidgetService), typeof(WidgetService));
    }
}

// Name-based stand-in for Microsoft.Extensions.DependencyInjection.
public class ServiceCollection
{
    public ServiceCollection AddScoped<TService, TImplementation>() where TImplementation : TService
    {
        return this;
    }

    public ServiceCollection AddScoped(Type service, Type implementation)
    {
        return this;
    }

    public ServiceCollection AddFixtureLogging()
    {
        return this;
    }
}
