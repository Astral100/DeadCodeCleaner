namespace Fixture.Domain;

// Inactive-region trap: when WidgetExtra orphans, this file looks fully dead to the compiler —
// but the #if block below holds code it cannot see, so the file must never be deleted whole.
public class WidgetExtra
{
    public string Tag()
    {
        return "extra";
    }
}

#if FIXTURE_LEGACY
public class HiddenLegacy
{
    public string Hidden()
    {
        return "hidden";
    }
}
#endif
