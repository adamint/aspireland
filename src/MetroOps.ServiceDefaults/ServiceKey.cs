using System.Security.Cryptography;
using System.Text;

namespace MetroOps.ServiceDefaults;

public static class ServiceKey
{
    public static bool Matches(HttpContext context, IConfiguration config, string name, string header = "X-Service-Key")
    {
        var expected = config[name];
        var actual = context.Request.Headers[header].FirstOrDefault();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        var a = Encoding.UTF8.GetBytes(actual);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
