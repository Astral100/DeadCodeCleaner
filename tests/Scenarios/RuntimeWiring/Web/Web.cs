namespace Web
{
    // The only C# user of Lib; the view still names Lib's MenuVm.
    public class OldController { public int Get() { return Lib.Helper.Do(); } }
    public static class Program { public static void Main() { } }
}
