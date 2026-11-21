namespace Fixture.Domain;

// Name-based stand-in for AutoMapper's Profile.
public class Profile
{
    protected void CreateMap<TSource, TDestination>() { }
}

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        CreateMap<WidgetEntity, WidgetDto>();
        CreateMap<GadgetEntity, GadgetDto>();
    }
}
