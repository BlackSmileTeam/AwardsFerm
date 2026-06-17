namespace AwardsFerm.Core.Models;

public sealed class ProxyDefinition
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Scheme { get; set; } = "http";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Login { get; set; }
    public bool HasPassword { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Timezone { get; set; }
    public string? Locale { get; set; }
    public string? LocationLabel { get; set; }
    public string DisplayAddress => $"{Scheme}://{Host}:{Port}";
}

public sealed class CreateProxyRequest
{
    public string Name { get; set; } = string.Empty;
    public string Scheme { get; set; } = "http";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Login { get; set; }
    public string? Password { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Timezone { get; set; }
    public string? Locale { get; set; }
    public string? LocationLabel { get; set; }
}

public sealed class UpdateProxyRequest
{
    public string? Name { get; set; }
    public string? Scheme { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Login { get; set; }
    public string? Password { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Timezone { get; set; }
    public string? Locale { get; set; }
    public string? LocationLabel { get; set; }
}
