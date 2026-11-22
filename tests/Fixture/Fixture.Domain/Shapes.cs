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
public enum WidgetKind
{
    Unknown,
    Basic,
    Fancy,
    Legacy,
}
