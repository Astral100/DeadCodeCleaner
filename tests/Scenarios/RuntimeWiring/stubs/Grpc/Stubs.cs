using System;
using System.Threading.Tasks;
namespace Grpc.Core
{
    [AttributeUsage(AttributeTargets.Class)]
    public class BindServiceMethodAttribute : Attribute { public BindServiceMethodAttribute(Type bindType, string bindMethodName) { } }
    public class ServerCallContext { }
    public delegate Task<TResponse> UnaryServerMethod<TRequest, TResponse>(TRequest request, ServerCallContext context);
    public abstract class ServiceBinderBase { public virtual void AddMethod<TRequest, TResponse>(string name, UnaryServerMethod<TRequest, TResponse> handler) { } }
}
namespace Microsoft.AspNetCore.Builder
{
    public static class GrpcEndpointRouteBuilderExtensions { public static void MapGrpcService<TService>(this object app) where TService : class { } }
}
