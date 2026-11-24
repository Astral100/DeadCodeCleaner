namespace Core;

public static class Program
{
    public static void Main()
    {
        new Startup().Configure(new ContainerBuilder());
    }
}

public class Startup
{
    public void Configure(ContainerBuilder builder)
    {
        builder.RegisterType<Zeta>().AsImplementedInterfaces();
    }
}

public interface IZeta
{
    void Ping();
}

public class Zeta : IZeta, System.IDisposable
{
    public void Ping() { }
    public void Dispose() { }
}

public class ZetaController
{
    public void Act(IZeta z)
    {
        z.Ping();
    }
}

// Name-based stand-in for Autofac.
public class ContainerBuilder
{
    public RegistrationBuilder<T> RegisterType<T>()
    {
        return new RegistrationBuilder<T>();
    }
}

public class RegistrationBuilder<T>
{
    public RegistrationBuilder<T> AsImplementedInterfaces()
    {
        return this;
    }
}
