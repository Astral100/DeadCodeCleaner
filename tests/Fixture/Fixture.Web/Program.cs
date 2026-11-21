using Fixture.Domain;

namespace Fixture.Web;

public static class Program
{
    public static void Main()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWidgetService, WidgetService>();
        services.AddScoped<IGadgetService, GadgetService>();
    }
}

// Name-based stand-in for Microsoft.Extensions.DependencyInjection.
public class ServiceCollection
{
    public void AddScoped<TService, TImplementation>() where TImplementation : TService { }
}
