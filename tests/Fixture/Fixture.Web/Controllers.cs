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
        SharedService.WidgetCounter++;
        SharedService.WidgetOnlyA += SharedService.WidgetOnlyB;
        var description = _svc.Describe();
        _ = description;
        return _mediator.Send(new GetWidgetQuery());
    }

    public string GetSecondary()
    {
        return SecondaryHelper.Compute();
    }

    // Uses Render through the BASE type, the partial method, the conditional file's class, and the
    // marker attribute: all must be handled correctly when this controller is removed.
    [FixtureMarker]
    public string GetWidgetBadge(ShapeBase shape)
    {
        return shape.Render() + new WidgetFormatter().FormatWidget() + new WidgetExtra().Tag();
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

    public GadgetsController(IGadgetService svc)
    {
        _svc = svc;
    }

    public string GetGadget()
    {
        return SharedService.Prefix() + _svc.Describe() + new GadgetDto().Label + SharedService.GadgetCounter
            + new Circle().Render() + new WidgetFormatter().Plain() + WidgetKind.Basic;
    }
}

public static class SecondaryHelper
{
    public static string Compute()
    {
        return "s";
    }
}
