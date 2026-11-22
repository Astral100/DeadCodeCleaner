namespace Fixture.Domain;

public class GetWidgetQuery : IRequest<WidgetDto> { }

public class GetWidgetHandler : IRequestHandler<GetWidgetQuery, WidgetDto>
{
    public WidgetDto Handle(GetWidgetQuery request)
    {
        var repo = new WidgetRepository();
        var entity = repo.Load();
        return new WidgetDto { Id = entity.Id, Name = entity.Name };
    }
}

public class WidgetRepository
{
    public WidgetEntity Load()
    {
        return new WidgetEntity { Id = 1, Name = "w" };
    }
}

public class WidgetEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class WidgetDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public interface IWidgetService
{
    string Describe();
}

public class WidgetService : IWidgetService
{
    public string Describe()
    {
        // The explicit Add call dies with this class; the initializer in GadgetService must then
        // keep GadgetBag.Add alive on its own.
        var bag = new GadgetBag();
        bag.Add(new GadgetEntity());
        _ = bag;
        // Same trap for Deconstruct: this explicit call dies, and the deconstruction syntax in
        // GadgetService (no identifier) must then keep it alive on its own.
        new GadgetDto().Deconstruct(out var deconA, out var deconB);
        _ = deconA + deconB;
        return SharedService.Prefix() + "widget";
    }
}
