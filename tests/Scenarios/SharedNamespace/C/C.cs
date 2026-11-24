namespace Shared.Helpers { public static class CHelper { public static int Y() { return 2; } } }
namespace CApp { public class CController { public int Go() { return Shared.Helpers.CHelper.Y(); } } }
