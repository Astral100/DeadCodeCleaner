using System;
namespace Microsoft.Owin { [AttributeUsage(AttributeTargets.Assembly)] public sealed class OwinStartupAttribute : Attribute { public OwinStartupAttribute(Type t) { } } }
namespace Hosting { public class HostBuilder { public HostBuilder UseStartup<T>() { return this; } } }
