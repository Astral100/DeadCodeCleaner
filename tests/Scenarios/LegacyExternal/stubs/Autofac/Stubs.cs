using System;
namespace Autofac
{
    public class Reg<T>
    {
        public Reg<T> As<TS>() { return this; }
        public Reg<T> As(params Type[] t) { return this; }
        public Reg<T> Named(string name, Type t) { return this; }
        public Reg<T> WithMetadata(string key, object value) { return this; }
        public Reg<T> Where(Func<Type, bool> predicate) { return this; }
        public Reg<T> AsImplementedInterfaces() { return this; }
        public Reg<T> AsSelf() { return this; }
    }
    public class ContainerBuilder
    {
        public Reg<T> RegisterType<T>() { return new Reg<T>(); }
        public Reg<object> RegisterAssemblyTypes(params System.Reflection.Assembly[] assemblies) { return new Reg<object>(); }
        public object Build() { return null; }
    }
    public abstract class Module { protected virtual void Load(ContainerBuilder b) { } }
}
