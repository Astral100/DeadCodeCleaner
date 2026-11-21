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
    public WidgetEntity Load() => new WidgetEntity { Id = 1, Name = "w" };
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
    public string Describe() => SharedService.Prefix() + "widget";
}
