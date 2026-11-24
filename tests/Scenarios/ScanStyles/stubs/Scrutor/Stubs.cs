using System;
namespace Scrutor
{
    public class Selector
    {
        public Selector FromAssembliesOf(params Type[] types) { return this; }
        public Selector AddClasses() { return this; }
        public Selector AsMatchingInterface() { return this; }
    }
    public class Services { }
    public static class ScanExtensions { public static Services Scan(this Services s, Action<Selector> a) { return s; } }
}
