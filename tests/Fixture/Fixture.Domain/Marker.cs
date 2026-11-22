[assembly: Fixture.Domain.FixtureMarker]

namespace Fixture.Domain;

// Assembly-attribute trap: when the only other user (the removed controller) dies, the assembly
// attribute above still references this class — it must never be orphaned.
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method)]
public class FixtureMarkerAttribute : Attribute
{
}
