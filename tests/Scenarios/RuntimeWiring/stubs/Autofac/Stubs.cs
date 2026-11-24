using System;
namespace Autofac
{
    public class Reg<T>
    {
        public Reg<T> As<TS>() { return this; }
        public Reg<T> Named<TS>(Func<Type, string> name) { return this; }
        public Reg<T> AsClosedTypesOf(Type open) { return this; }
        public Reg<T> Where(Func<Type, bool> predicate) { return this; }
        public Reg<T> AsImplementedInterfaces() { return this; }
        public Reg<T> AsSelf() { return this; }
        public Reg<T> AutoActivate() { return this; }
    }
    public class ContainerBuilder
    {
        public Reg<T> RegisterType<T>() { return new Reg<T>(); }
        public Reg<object> RegisterAssemblyTypes(params System.Reflection.Assembly[] assemblies) { return new Reg<object>(); }
    }
}
