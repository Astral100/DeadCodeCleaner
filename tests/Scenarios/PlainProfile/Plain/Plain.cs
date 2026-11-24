namespace Plain;

public static class Program
{
    public static void Main()
    {
        System.Console.WriteLine("plain");
    }
}

public class Profile
{
    public string Name = "";
}

public class AdminProfile : Profile
{
    public void Touch()
    {
        Doomed.Go();
    }
}

public static class Doomed
{
    public static void Go() { }
}
