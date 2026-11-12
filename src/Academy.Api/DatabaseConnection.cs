using Npgsql;

namespace Academy.Api;

public static class DatabaseConnection
{
    /// <summary>
    /// Resolves the Postgres connection string: Neon's DATABASE_URL (URI or key=value),
    /// then ConnectionStrings:Academy, then the local Docker default.
    /// </summary>
    public static string Resolve(IConfiguration configuration)
    {
        var neonUrl = configuration["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(neonUrl)) return Normalize(neonUrl);

        var configured = configuration.GetConnectionString("Academy");
        if (!string.IsNullOrWhiteSpace(configured)) return Normalize(configured);

        return "Host=localhost;Port=5432;Database=academy;Username=postgres;Password=postgres";
    }

    /// <summary>Npgsql only accepts key=value strings, while Neon hands out postgres:// URIs.</summary>
    public static string Normalize(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value;

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
            SslMode = SslMode.Require,
            ApplicationName = "AnaQuranAcademy",
            MaxPoolSize = 20,
            // Npgsql leaves TCP keepalive off, so a connection idle past the pooler's
            // timeout is closed silently and the next request pays a full DNS + TLS +
            // startup handshake. 15s is comfortably inside any pooler or NAT cutoff.
            KeepAlive = 15
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var val = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            switch (key)
            {
                case "sslmode" when Enum.TryParse<SslMode>(val.Replace("-", ""), true, out var mode):
                    builder.SslMode = mode;
                    break;
                case "application_name":
                    builder.ApplicationName = val;
                    break;
                case "connect_timeout" when int.TryParse(val, out var seconds):
                    builder.Timeout = seconds;
                    break;
            }
        }

        return builder.ConnectionString;
    }
}
