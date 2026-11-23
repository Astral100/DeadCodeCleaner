namespace Fixture.Domain;

// Used only by WidgetsController in code, but its member name is ALSO accessed through 'dynamic'
// (GadgetsController.DescribeDynamically): the exclusion list keeps it when the controller goes.
public static class DynamicTarget
{
    public static string Summarize()
    {
        return "summary";
    }
}

// Used only by WidgetsController in code, but ALSO named inside an inactive #if region
// (Conditional.cs): the exclusion list keeps it when the controller goes.
public class InactiveUse
{
    public string Ping()
    {
        return "ping";
    }
}
