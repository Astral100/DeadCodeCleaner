using System;
namespace System.Web { public class HttpApplication { } }
namespace System.Web { [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)] public sealed class PreApplicationStartMethodAttribute : Attribute { public PreApplicationStartMethodAttribute(Type t, string m) { } } }
namespace System.Web.UI { public class Page { } }
namespace System.Web.Mvc { public abstract class Controller { } }
namespace System.Data.Entity.ModelConfiguration { public class EntityTypeConfiguration<T> { } }
namespace Some.Bus { public interface IHandleMessages<T> { void Handle(T message); } }
