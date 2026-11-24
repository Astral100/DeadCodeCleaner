using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Api
{
    // gRPC: the service override is reached only through the generated binder.
    public class GreetingFormatter { public string Format(string name) { return "Hello " + name; } }
    public class GreeterService : Greeter.GreeterBase
    {
        public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        {
            return Task.FromResult(new HelloReply { Message = new GreetingFormatter().Format(request.Name) });
        }
    }
    public class LegacyController { public string Greet(string n) { return new GreetingFormatter().Format(n); } }

    // Registrations: AB is IA's only registration but one of two for IB; C1 has a twin; the
    // repository's registration shares only a type argument with the validator's.
    public interface IA { void A(); }
    public interface IB { void B(); }
    public class AB : IA, IB { public void A() { } public void B() { } }
    public class BOnly : IB { public void B() { } }
    public interface IC { void C(); }
    public class C1 : IC { public void C() { } }
    public class C2 : IC { public void C() { } }
    public class Customer { }
    public interface IRepository<T> { T Get(); }
    public interface IValidator<T> { bool Ok(T t); }
    public class CustomerRepository : IRepository<Customer> { public Customer Get() { return new Customer(); } }
    public class CustomerValidator : IValidator<Customer> { public bool Ok(Customer c) { return true; } }
    public class OrdersController
    {
        public OrdersController(IA a, IB b, IC c, IRepository<Customer> repo, IValidator<Customer> val) { a.A(); b.B(); c.C(); val.Ok(repo.Get()); }
    }

    // Built by the container; created with new only by a test and by dead code: its dead method is
    // held for review.
    public class Reporter { public void Old() { HelperQ.Go(); } }
    public class ReportsController { public ReportsController(Reporter r) { } public void Run() { HelperQ.Go(); } }
    public class DeadMaker { public Reporter Make() { return new Reporter(); } }
    public static class HelperQ { public static void Go() { } }

    // _a is unused but shares its declaration with the used _b: its initializer stays in the text.
    public static class Old { public static int Seed = 1; }
    public class Svc { private int _a = Old.Seed, _b; public int B() { return _b; } }
    public class SvcController { public int Get() { return Old.Seed; } }

    // Tested only through tests marked with a derived attribute.
    public class DoomedController { public int Get() { return 1; } }
    public class Keep { public int Value() { return 2; } }
    public class HomeController
    {
        public int Index() { return new Svc().B() + new Keep().Value(); }
        public OrderDto Show(Order o) { return new OrderDto(); }
    }

    // A live mapping that USES ComputeTotal (a MapFrom source) and CONFIGURES Total.
    public class Order { public decimal ComputeTotal() { return 1m; } }
    public class OrderDto { public decimal Total { get; set; } }
    public class OrderProfile : AutoMapper.Profile
    {
        public OrderProfile() { CreateMap<Order, OrderDto>().ForMember(d => d.Total, o => o.MapFrom(s => s.ComputeTotal())); }
    }

    public static class Registrations
    {
        public static void Configure()
        {
            var b = new Autofac.ContainerBuilder();
            b.RegisterType<AB>().As<IA>().As<IB>();
            b.RegisterType<BOnly>().As<IB>();
            b.RegisterType<Reporter>().AsSelf();
            var services = new ServiceCollection();
            services.AddScoped<IC, C1>();
            services.AddScoped<IC, C2>();
            services.AddScoped<IRepository<Customer>, CustomerRepository>();
            services.AddScoped<IValidator<Customer>, CustomerValidator>();
        }
    }

    public static class Program
    {
        public static void Main()
        {
            object app = new object();
            app.MapGrpcService<GreeterService>();
            Registrations.Configure();
        }
    }
}
