namespace Fixture.Domain;

public static class SharedService
{
    // One declaration, two fields with different fates: the orphaned one must be KEPT
    // (it cannot be deleted alone) with a warning. Internal on purpose: public fields are
    // auto-kept with their in-use type (serializer/marshalling shape) and would never orphan.
    internal static int WidgetCounter, GadgetCounter;

    // One declaration where BOTH fields orphan together: the whole line goes.
    internal static int WidgetOnlyA, WidgetOnlyB;

    // Referenced only by the removed controller, but PUBLIC: serializers and data binding reach
    // public fields by reflection, so it must stay with its in-use type.
    public static int WidgetPublicTally;

    public static string Prefix()
    {
        return "shared:";
    }
}
