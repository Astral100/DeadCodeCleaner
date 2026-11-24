using System.Threading.Tasks;
using Grpc.Core;
namespace Api
{
    // Shaped like Grpc.Tools output: the framework finds BindService only through the attribute's string.
    public class HelloRequest { public string Name { get; set; } }
    public class HelloReply { public string Message { get; set; } }
    public static partial class Greeter
    {
        [BindServiceMethod(typeof(Greeter), "BindService")]
        public abstract partial class GreeterBase
        {
            public virtual Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context) { throw new System.NotImplementedException(); }
        }
        public static void BindService(ServiceBinderBase serviceBinder, GreeterBase serviceImpl)
        {
            serviceBinder.AddMethod<HelloRequest, HelloReply>("SayHello", serviceImpl.SayHello);
        }
    }
}
