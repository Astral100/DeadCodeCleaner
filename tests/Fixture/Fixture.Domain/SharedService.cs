namespace Fixture.Domain;

public static class SharedService
{
    // One declaration, two fields with different fates: the orphaned one must be KEPT
    // (it cannot be deleted alone) with a warning.
    public static int WidgetCounter, GadgetCounter;

    // One declaration where BOTH fields orphan together: the whole line goes.
    public static int WidgetOnlyA, WidgetOnlyB;

    public static string Prefix() => "shared:";
}
