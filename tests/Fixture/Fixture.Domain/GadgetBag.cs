using System.Collections;

namespace Fixture.Domain;

// Collection-initializer trap: surviving code reaches Add only through "new GadgetBag { entity }",
// which has no identifier in syntax — the bound tree must supply the edge or Add gets over-deleted.
public class GadgetBag : IEnumerable
{
    private readonly List<GadgetEntity> _items = new();

    internal void Add(GadgetEntity item)
    {
        _items.Add(item);
    }

    public IEnumerator GetEnumerator()
    {
        return _items.GetEnumerator();
    }
}
