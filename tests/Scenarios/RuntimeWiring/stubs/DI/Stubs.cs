namespace Microsoft.Extensions.DependencyInjection
{
    public class ServiceCollection
    {
        public ServiceCollection AddScoped<TService, TImplementation>() where TImplementation : TService { return this; }
    }
}
