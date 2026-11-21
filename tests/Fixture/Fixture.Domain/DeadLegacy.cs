namespace Fixture.Domain;

// Pre-existing orphans: nothing references these today. The analyzer must leave them untouched.
public class DeadLegacyHelper
{
    public string Legacy() => new DeadLegacyChild().Value();
}

public class DeadLegacyChild
{
    public string Value() => "old";
}

// Pre-existing orphan that references code which WILL be deleted when WidgetsController goes:
// must then be deleted too (it would no longer compile).
public class DeadWidgetAdmirer
{
    public string Admire() => new WidgetService().Describe();
}
