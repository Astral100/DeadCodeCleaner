namespace T
{
    // Every test uses a derived attribute: the project is a test project through them alone.
    public class MyFactAttribute : Xunit.FactAttribute { }
    public class CustomTests
    {
        [MyFact] public void Uses_doomed_and_keep() { _ = new Api.DoomedController().Get() + new Api.Keep().Value(); }
        [MyFact] public void Healthy() { _ = new Api.Keep().Value(); }
    }
    public class ReporterTests
    {
        [MyFact] public void Creates() { _ = new Api.Reporter(); }
    }
}
