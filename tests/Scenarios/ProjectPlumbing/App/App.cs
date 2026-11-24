namespace App
{
    public static class Program { public static void Main() { System.Console.WriteLine(new HomeController().Index(null!)); } }
    public class Svc
    {
        private readonly string _name;
        public Svc(string n) { _name = n; }
        public string Name() { return _name; }
    }
    public class HomeController
    {
        public int Index(Svc s) { return s.Name().Length; }
        // Losing the only constructor leaves _name unassigned: CS8618, an error under WarningsAsErrors=nullable.
        public int Old() { return new Svc("x").Name().Length; }
    }
    public class FeatureController { public int Run() { return Lib.LibUtil.Do(); } }
    public static class Calc { public static int Net(int g) { return g - 1; } }
}
