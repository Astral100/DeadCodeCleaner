namespace Fixture.Domain;

// Attribute with a parameterized constructor: the ctor is referenced ONLY through attribute
// usages, which carry no identifier naming it. One usage sits on a dying class, the other on a
// surviving METHOD — method subtrees take the body-operation fast path, so the surviving usage
// must still contribute a constructor edge.
public sealed class AuditAttribute : Attribute
{
    public AuditAttribute(string note)
    {
        Note = note;
    }

    public string Note { get; }
}
