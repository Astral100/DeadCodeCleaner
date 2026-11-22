namespace Fixture.Domain;

// Pre-existing orphan used only by CollateralHelper.Broken: the Case 9 removal target.
public class ObsoleteGizmo
{
    public void Zap() { }
}

// Production-dead class whose Broken member references ObsoleteGizmo while Useful is kept alive by
// a healthy test: removing ObsoleteGizmo must delete ONLY Broken, never the class, Useful, or the
// test (the round-9 container-escalation regression).
public class CollateralHelper
{
    public void Broken()
    {
        var g = new ObsoleteGizmo();
        g.Zap();
    }

    public static int Useful()
    {
        return 42;
    }
}
