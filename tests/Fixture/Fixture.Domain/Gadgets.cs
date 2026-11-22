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
    // Referenced only by a ForMember segment in MappingProfile: removing it must take the
    // segment alone, not the whole gadget mapping.
    public string AuditNote { get; set; } = "";

    // Internal pattern member: deconstruction has no identifier in source and the language only
    // requires accessibility, not "public" — must be kept while the type is in use.
    internal void Deconstruct(out int id, out string label)
    {
        id = Id;
        label = Label;
    }
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
        // Collection initializer: binds GadgetBag.Add with no identifier in the syntax tree.
        var bag = new GadgetBag { entity };
        _ = bag;
        // Deconstruction: binds the internal GadgetDto.Deconstruct with no identifier either.
        var (gadgetId, gadgetLabel) = new GadgetDto { Id = 3, Label = "d" };
        _ = gadgetId + gadgetLabel;
        // The surviving half of the shared WidgetCounter/GadgetCounter declaration.
        SharedService.GadgetCounter++;
        return SharedService.Prefix() + entity.Label;
    }
}
