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
}

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // The ForMember lambda references WidgetDto.Name: those references belong to the map
        // statement, so they must NOT keep the widget types alive through the rooted profile.
        CreateMap<WidgetEntity, WidgetDto>().ForMember(d => d.Name);
        CreateMap<GadgetEntity, GadgetDto>();
    }
}
