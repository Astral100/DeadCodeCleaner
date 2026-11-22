namespace Fixture.Domain;

// Override-dispatch trap: the removed controller calls Render through the BASE type, surviving code
// only through the derived one. ShapeBase.Render must survive, or Circle's override cannot compile.
public abstract class ShapeBase
{
    public abstract string Render();
}

public class Circle : ShapeBase
{
    public override string Render()
    {
        return "circle";
    }
}

// Enum-member removal trap: deleting Fancy must take its separator comma along.
// EnumNote on Basic: the attribute class is also used by the removed controller, and must survive
// through the enum-member attribute edge.
public enum WidgetKind
{
    Unknown,
    [EnumNote]
    Basic,
    Fancy,
    Legacy,
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Method)]
public class EnumNoteAttribute : Attribute
{
}

// Primary-constructor trap: the removed controller is the only code CONSTRUCTING WidgetTag, while
// surviving code still uses it as a type. An orphaned primary constructor must never delete the
// record (its declaring syntax IS the type declaration).
public record WidgetTag(string Label)
{
    public string Show()
    {
        return Label;
    }
}

// Primary-base-call trap: NamedTagHolder's ": TagHolder(name)" is the only SURVIVING reference to
// TagHolder's constructor once the removed controller (the other caller) dies.
public class TagHolder
{
    public TagHolder(string tag)
    {
        _ = tag;
    }
}

public class NamedTagHolder(string name) : TagHolder(name)
{
    public string Name()
    {
        return "n";
    }
}
