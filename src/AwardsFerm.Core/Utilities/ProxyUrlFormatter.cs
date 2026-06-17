namespace AwardsFerm.Core.Utilities;

public static class ProxyUrlFormatter
{
    public static string Build(string scheme, string host, int port, string? login, string? password)
    {
        var normalizedScheme = NormalizeScheme(scheme);
        if (!string.IsNullOrWhiteSpace(login))
        {
            var user = Uri.EscapeDataString(login.Trim());
            var pass = Uri.EscapeDataString(password?.Trim() ?? string.Empty);
            return $"{normalizedScheme}://{user}:{pass}@{host.Trim()}:{port}";
        }

        return $"{normalizedScheme}://{host.Trim()}:{port}";
    }

    public static string NormalizeScheme(string scheme) =>
        scheme.Trim().ToLowerInvariant() switch
        {
            "socks5" or "socks5h" => "socks5",
            _ => "http"
        };
}
