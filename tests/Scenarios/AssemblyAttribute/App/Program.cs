using Lib;

[assembly: Marker]

namespace App;

public static class Program
{
    public static void Main()
    {
        System.Console.WriteLine("hello");
    }
}

public class Legacy
{
    public void Use()
    {
        var d = new Doomed();
        d.Go();
    }
}
