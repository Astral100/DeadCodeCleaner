namespace Tst
{
    public class FeatureTests { [Xunit.Fact] public void Both() { new App.FeatureController().Run(); _ = new App.HomeController(); } }
}
