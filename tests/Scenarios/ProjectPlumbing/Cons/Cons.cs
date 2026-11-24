using LinkedNs;
namespace Cons
{
    public class UseController { public int Go() { return SharedThing.V(); } }
    public class OldController { public int Go() { return 2; } }
}
