using System;
namespace Scrutor
{
    public class Selector
    {
        public Selector FromAssembliesOf(params Type[] types) { return this; }
        public Selector AddClasses() { return this; }
        public Selector AsMatchingInterface() { return this; }
    }
    public class Services { public Services Scan(Action<Selector> a) { return this; } }
}
