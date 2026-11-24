using System.Globalization;

namespace Shop;

public static class Program
{
    public static void Main()
    {
        System.Console.WriteLine(Holder.Banner);
    }
}

public class Alpha { }
public class AlphaDto { }
public class Beta { }
public class BetaDto { }

public class AlphaController
{
    public object Get()
    {
        return new AlphaDto().ToString() + new Alpha() + Holder.Banner;
    }
}

public class BetaController
{
    public object Get()
    {
        Holder.Dead();
        return new BetaDto().ToString() + new Beta();
    }
}

public static class Doomed
{
    public static void Touch() { }
}

public static class Holder
{
    public static string Banner = @"first
second"; public static void Dead() { }
}

public class MainProfile : Profile
{
    public MainProfile()
    {
        CreateMap<Alpha, AlphaDto>().IgnoreExtra();
    }
}

public class CultureProfile : Profile
{
    public CultureProfile()
    {
        CreateMap<Beta, BetaDto>();
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
    }
}

// Name-based stand-ins for AutoMapper.
public class Profile
{
    public MappingExpression<TSource, TDest> CreateMap<TSource, TDest>()
    {
        return new MappingExpression<TSource, TDest>();
    }
}

public class MappingExpression<TSource, TDest>
{
}

public static class MappingExtensions
{
    public static MappingExpression<TSource, TDest> IgnoreExtra<TSource, TDest>(this MappingExpression<TSource, TDest> expression)
    {
        Doomed.Touch();
        return expression;
    }
}
