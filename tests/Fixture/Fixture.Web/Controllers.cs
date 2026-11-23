using Fixture.Domain;

namespace Fixture.Web;

public class WidgetsController
{
    private readonly Mediator _mediator;
    private readonly IWidgetService _svc;

    public WidgetsController(Mediator mediator, IWidgetService svc)
    {
        _mediator = mediator;
        _svc = svc;
    }

    public object GetWidget()
    {
        SharedService.WidgetPublicTally++;
        var description = _svc.Describe();
        _ = description;
        return _mediator.Send(new GetWidgetQuery());
    }

    public string GetSecondary()
    {
        return SecondaryHelper.Compute();
    }

    // Uses Render through the BASE type, the partial method, the conditional file's class, and the
    // marker attribute: all must be handled correctly when this controller is removed. Also the
    // ONLY code constructing WidgetTag and TagHolder, and the only [EnumNote] use outside the enum.
    [FixtureMarker]
    [EnumNote]
    public string GetWidgetBadge(ShapeBase shape)
    {
        var holder = new TagHolder("badge");
        _ = holder;
        return shape.Render() + new WidgetFormatter().FormatWidget() + new WidgetExtra().Tag() + new WidgetTag("w").Show()
            + DynamicTarget.Summarize() + new InactiveUse().Ping();
    }

    // Overload with its own private dependency: removable individually by signature.
    public string GetSecondary(int times)
    {
        return OverloadHelper.Only() + times;
    }
}

public static class OverloadHelper
{
    public static string Only()
    {
        return "o";
    }
}

public class GadgetsController
{
    private readonly IGadgetService _svc;

    public string DescribeDynamically(object source)
    {
        dynamic d = source;
        return (string)d.Summarize();
    }

    public GadgetsController(IGadgetService svc)
    {
        _svc = svc;
    }

    [Audit("gadget")]
    public string GetGadget()
    {
        return SharedService.Prefix() + _svc.Describe() + new GadgetDto().Label
            + new Circle().Render() + new WidgetFormatter().Plain() + WidgetKind.Basic + new NamedTagHolder("n").Name();
    }

    // Keeps WidgetTag in use AS A TYPE without ever constructing it: removing the other controller
    // orphans only the constructor, and that must not delete the record.
    public string DescribeTag(WidgetTag tag)
    {
        return tag.Show();
    }
}

public static class SecondaryHelper
{
    public static string Compute()
    {
        return "s";
    }
}
