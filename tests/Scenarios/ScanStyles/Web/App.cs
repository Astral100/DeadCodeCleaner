[assembly: Microsoft.Owin.OwinStartup(typeof(Web.AuthConfig))]
namespace Web
{
    // Every helper is used by Admin AND by exactly one place only a framework reaches: removing
    // Admin must delete none of them.
    public class Global : System.Web.HttpApplication
    {
        public static Heartbeat Beat = new Heartbeat();
        public static UserRepo Users = new UserRepo();
        public static AuditFilter Filter = new AuditFilter();
        protected void Application_Start() { ScanConfig.Register(); new Hosting.HostBuilder().UseStartup<ApiStartup>(); }
        protected void FormsAuthentication_OnAuthenticate(object s, System.EventArgs e) { HelperA.Go(); }
    }
    public class AuthConfig { public void Configuration(object app) { HelperO.Go(); } }
    public class ApiStartup { public void ConfigureServices(object services) { HelperU.Go(); } }
    public interface ICommandHandler<T> { void Handle(T c); }
    public class CreateOrder { }
    public class CreateOrderHandler : ICommandHandler<CreateOrder> { public void Handle(CreateOrder c) { HelperC.Go(); } }
    public interface IPlugin { }
    public class ZipPlugin : IPlugin { public ZipPlugin() { HelperN.Go(); } }
    public abstract class HandlerBase { }
    public class EmailHandler : HandlerBase { public EmailHandler() { HelperB.Go(); } }
    public interface IFooService { }
    public class FooService : IFooService { public FooService() { HelperI.Go(); } }
    public interface IOrderRepository { }
    public class SqlOrderRepository : IOrderRepository { public SqlOrderRepository() { HelperM.Go(); } }
    public class CacheWarmer { public CacheWarmer() { HelperW.Go(); } }
    public class ReportBuilder { public ReportBuilder(IFooService f) { HelperS.Go(); } public ReportBuilder() { } }
    public static class ScanConfig
    {
        public static void Register()
        {
            var b = new Autofac.ContainerBuilder();
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).AsClosedTypesOf(typeof(ICommandHandler<>));
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.Name.EndsWith("Plugin")).Named<IPlugin>(t => t.Name);
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.IsSubclassOf(typeof(HandlerBase))).As<HandlerBase>();
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.Name.EndsWith("service", true, System.Globalization.CultureInfo.InvariantCulture)).AsImplementedInterfaces();
            b.RegisterAssemblyTypes(typeof(ScanConfig).Assembly).Where(t => t.Namespace != null && t.Namespace.StartsWith("Web") && t.Name.StartsWith("Report")).AsSelf();
            b.RegisterType<CacheWarmer>().AsSelf().AutoActivate();
            b.RegisterType<NightlyTask>().AsSelf().AutoActivate();
            new Scrutor.Services().Scan(s => s.FromAssembliesOf(typeof(ScanConfig)).AddClasses().AsMatchingInterface());
        }
    }
    // A timer started by a SOLUTION class's constructor, created in a field initializer.
    public class Poller { private readonly System.Threading.Timer _t; public Poller(int ms) { _t = new System.Threading.Timer(_ => { }, null, ms, ms); } public void Stop() { _t.Dispose(); } }
    public class Heartbeat { private readonly Poller _poller = new Poller(1000); public void Stop() { _poller.Stop(); } }
    // UserRepo has no constructor: it implicitly calls the base constructor with its default argument.
    public class RepoBase { public string Conn; public RepoBase(string conn = "db") { Conn = conn; HelperR.Go(); } }
    public class UserRepo : RepoBase { }
    // Built by the container, never by the solution: a method nothing calls may be a convention the
    // tool does not know — held for review, not deleted.
    public class NightlyTask { public void Execute() { HelperX.Go(); } }
    // Created by the solution's own code: its dead method is deleted as usual.
    public class AuditFilter : System.Web.Mvc.ActionFilterAttribute { public void OldLog() { HelperY.Go(); } }
    // Kept by exact name in the config: reached by reflection, so its members stay too.
    public class ExportPlugin { public void Run() { HelperK.Go(); } }
    public class HomeController : System.Web.Mvc.Controller
    {
        public HomeController(ICommandHandler<CreateOrder> h, IPlugin p, HandlerBase hb, IFooService f, IOrderRepository r, ReportBuilder rb) { h.Handle(new CreateOrder()); }
        public void Admin()
        {
            HelperA.Go(); HelperO.Go(); HelperU.Go(); HelperC.Go(); HelperN.Go(); HelperB.Go(); HelperI.Go(); HelperM.Go(); HelperW.Go();
            HelperS.Go(); HelperR.Go(); HelperK.Go(); HelperX.Go(); HelperY.Go(); Global.Beat.Stop(); _ = new RepoBase("x"); new ExportPlugin().Run(); _ = new ReportBuilder();
        }
    }
    public static class HelperA { public static void Go() { } }
    public static class HelperO { public static void Go() { } }
    public static class HelperU { public static void Go() { } }
    public static class HelperC { public static void Go() { } }
    public static class HelperN { public static void Go() { } }
    public static class HelperB { public static void Go() { } }
    public static class HelperI { public static void Go() { } }
    public static class HelperM { public static void Go() { } }
    public static class HelperW { public static void Go() { } }
    public static class HelperS { public static void Go() { } }
    public static class HelperR { public static void Go() { } }
    public static class HelperK { public static void Go() { } }
    public static class HelperX { public static void Go() { } }
    public static class HelperY { public static void Go() { } }
}
// A class outside any namespace: the scan filter reading t.Namespace must not crash on it.
public class GlobalNamespaceThing { }
