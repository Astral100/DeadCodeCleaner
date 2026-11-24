namespace Lib
{
    // A library with no entry point: nothing counts as in use.
    public class Alpha { public int Get() { return new Beta().Get(); } }
    public class Beta { public int Get() { return 1; } }
}
