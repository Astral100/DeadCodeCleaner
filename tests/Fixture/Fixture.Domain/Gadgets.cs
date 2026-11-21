namespace Fixture.Domain;

public class GadgetEntity
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public class GadgetDto
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public interface IGadgetService
{
    string Describe();
}

public class GadgetService : IGadgetService
{
    public string Describe()
    {
        var entity = new GadgetEntity { Id = 2, Label = "g" };
        return SharedService.Prefix() + entity.Label;
    }
}
