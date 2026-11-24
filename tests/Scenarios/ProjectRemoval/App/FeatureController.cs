using Feature;

namespace App
{
    // The only user of the Feature project.
    public class FeatureController { public int Get() { return new FeatureService().Count(); } }
}
