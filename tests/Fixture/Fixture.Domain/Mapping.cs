namespace Fixture.Domain;

// Name-based stand-in for AutoMapper's Profile.
public class Profile
{
    protected MapBuilder<TSource, TDestination> CreateMap<TSource, TDestination>()
    {
        return new MapBuilder<TSource, TDestination>();
    }
}

public class MapBuilder<TSource, TDestination>
{
    public MapBuilder<TSource, TDestination> ForMember(Func<TDestination, object> selector)
    {
        return this;
    }

    public MapBuilder<TSource, TDestination> ConvertVia<TConverter>()
    {
        return this;
    }
}

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // The ForMember lambda references WidgetDto.Name: those references belong to the map
        // statement, so they must NOT keep the widget types alive through the rooted profile.
        CreateMap<WidgetEntity, WidgetDto>().ForMember(d => d.Name);
        // The chained call's generic type argument is the ONLY reference to GadgetConverter: it
        // must live while this map lives and die with it.
        CreateMap<GadgetEntity, GadgetDto>().ConvertVia<GadgetConverter>();
    }
}

public class GadgetConverter
{
}
