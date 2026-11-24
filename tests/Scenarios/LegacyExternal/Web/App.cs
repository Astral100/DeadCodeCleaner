[assembly: System.Web.PreApplicationStartMethod(typeof(Web.Bootstrap), "Start")]
namespace Web
{
    // Every helper is used by Admin AND by exactly one framework-reached place: removing Admin must
    // delete none of them — each would die if its place were not recognised as live.
    public class Global : System.Web.HttpApplication
    {
        public static Heartbeat Beat;
        protected void Application_Start() { HelperG.Go(); Beat = new Heartbeat(); AutofacConfig.Register(); ScanConfig.Register(); }
    }
    public static class Bootstrap { public static void Start() { HelperS.Go(); } }
    public class Page1 : System.Web.UI.Page { protected void Page_Load(object s, System.EventArgs e) { HelperP.Go(); } }
    public class Order { public string LegacyCode = ""; }
    public class OrderConfig : System.Data.Entity.ModelConfiguration.EntityTypeConfiguration<Order> { public OrderConfig() { HelperE.Go(); } }
    public interface IFoo { } public class Foo : IFoo { public Foo() { HelperF.Go(); } }
    public interface IBar { } public class Bar : IBar { public Bar() { HelperB.Go(); } }
    public interface IBaz { } public class Baz : IBaz { public Baz() { HelperZ.Go(); } }
    public static class AutofacConfig
    {
        public static void Register()
        {
            var b = new Autofac.ContainerBuilder();
            b.RegisterType<Foo>().Named("foo", typeof(IFoo));
            b.RegisterType<Bar>().As(typeof(IBar));
            b.RegisterType<Baz>().WithMetadata("k", 1).As<IBaz>();
            b.Build();
        }
    }
    // The timer's initializer starts a background job: losing Stop() must not delete it.
    public class Heartbeat
    {
        private readonly System.Threading.Timer _timer = new System.Threading.Timer(Tick, null, 1000, 1000);
        static void Tick(object s) { HelperT.Go(); }
        public void Stop() { _timer.Dispose(); }
    }
    // Losing Set leaves _n never assigned: CS0649, an ERROR under TreatWarningsAsErrors.
    public static class Counter
    {
        private static int _n;
        public static void Set(int v) { _n = v; }
        public static int Read() { return _n; }
    }
    public static class Mid
    {
        public static void Keep() { } public static void Drop() { }
#if NEVER_DEFINED
        public static void Hidden() { }
#endif
    }
    public class HomeController : System.Web.Mvc.Controller
    {
        public HomeController(IFoo f, IBar b, IBaz z) { }
        public void Admin() { HelperG.Go(); HelperS.Go(); HelperP.Go(); HelperE.Go(); HelperF.Go(); HelperB.Go(); HelperZ.Go(); HelperT.Go(); Global.Beat.Stop(); Counter.Set(5); Mid.Drop(); }
        public int Other() { Mid.Keep(); return Counter.Read(); }
    }
    public static class HelperG { public static void Go() { } }
    public static class HelperS { public static void Go() { } }
    public static class HelperP { public static void Go() { } }
    public static class HelperE { public static void Go() { } }
    public static class HelperF { public static void Go() { } }
    public static class HelperB { public static void Go() { } }
    public static class HelperZ { public static void Go() { } }
    public static class HelperT { public static void Go() { } }
    // A user base module whose helper registers: the derived module must not die as "empty".
    public interface IRepo { } public class Repo : IRepo { }
    public interface IOrderSvc { } public class OrderSvc : IOrderSvc { }
    public abstract class BaseModule : Autofac.Module
    {
        protected void RegisterRepos(Autofac.ContainerBuilder b) { b.RegisterType<Repo>().As<IRepo>(); }
    }
    public class OrdersModule : BaseModule
    {
        protected override void Load(Autofac.ContainerBuilder b) { b.RegisterType<OrderSvc>().As<IOrderSvc>(); RegisterRepos(b); }
    }
    public class OrdersController : System.Web.Mvc.Controller { public OrdersController(IOrderSvc s) { } }
    public class ReposController : System.Web.Mvc.Controller { public ReposController(IRepo r) { } }
    // Assembly scanning: classes whose name ends with "Service" register under all their interfaces.
    public static class ScanConfig
    {
        public static void Register()
        {
            var b = new Autofac.ContainerBuilder();
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.Name.EndsWith("Service")).AsImplementedInterfaces();
        }
    }
    public interface IAuditService { }
    public class AuditService : IAuditService { }        // matches the filter: kept while IAuditService is used
    public class OldAuditService : IAuditService { }     // matches too: Autofac registers it as well
    public class AuditWriter : IAuditService { }         // filtered out: deletable
    public interface IReportService { }
    public class ReportService : IReportService { }      // matches, but its interface dies: deletable
    public class ScanController : System.Web.Mvc.Controller
    {
        public ScanController(IAuditService a) { }
        public void Build() { _ = new AuditService(); _ = new OldAuditService(); _ = new AuditWriter(); IReportService r = new ReportService(); _ = r; }
    }
    // A pre-existing orphan built on an OUTSIDE framework interface, referenced nowhere: probably
    // created by that framework at runtime — flagged for review, never deleted unconfirmed.
    public class OrderPlacedHandler : Some.Bus.IHandleMessages<string> { public void Handle(string message) { HelperJ.Go(); } }
    // A plain pre-existing orphan: still deleted with its broken reference.
    public class PlainOld { public void Touch() { HelperJ.Go(); } }
    public class JobsController : System.Web.Mvc.Controller { public void Run() { HelperJ.Go(); } }
    public static class HelperJ { public static void Go() { } }
}
