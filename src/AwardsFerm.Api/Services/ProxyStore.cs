using AwardsFerm.Api.Auth;
using AwardsFerm.Api.Data;
using AwardsFerm.Api.Data.Entities;
using AwardsFerm.Core.Models;
using AwardsFerm.Core.Utilities;
using Microsoft.EntityFrameworkCore;

namespace AwardsFerm.Api.Services;

public sealed class ProxyStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TokenEncryptionService _encryption;

    public ProxyStore(IServiceScopeFactory scopeFactory, TokenEncryptionService encryption)
    {
        _scopeFactory = scopeFactory;
        _encryption = encryption;
    }

    public IReadOnlyList<ProxyDefinition> GetAll(long userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.Proxies
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Name)
            .Select(x => Map(x))
            .ToList();
    }

    public ProxyDefinition Create(long userId, CreateProxyRequest request)
    {
        ValidateRequest(request.Scheme, request.Host, request.Port, request.Name);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = new ProxyEntity
        {
            UserId = userId,
            Name = request.Name.Trim(),
            Scheme = ProxyUrlFormatter.NormalizeScheme(request.Scheme),
            Host = request.Host.Trim(),
            Port = request.Port,
            Login = string.IsNullOrWhiteSpace(request.Login) ? null : request.Login.Trim(),
            PasswordEncrypted = string.IsNullOrWhiteSpace(request.Password)
                ? null
                : _encryption.Encrypt(request.Password.Trim()),
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Timezone = NormalizeOptional(request.Timezone),
            Locale = NormalizeOptional(request.Locale),
            LocationLabel = NormalizeOptional(request.LocationLabel)
        };

        db.Proxies.Add(entity);
        db.SaveChanges();
        return Map(entity);
    }

    public ProxyDefinition Update(long userId, long proxyId, UpdateProxyRequest request)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = db.Proxies.FirstOrDefault(x => x.Id == proxyId && x.UserId == userId)
                       ?? throw new InvalidOperationException("Прокси не найден.");

        if (!string.IsNullOrWhiteSpace(request.Name))
            entity.Name = request.Name.Trim();

        if (!string.IsNullOrWhiteSpace(request.Scheme))
            entity.Scheme = ProxyUrlFormatter.NormalizeScheme(request.Scheme);

        if (!string.IsNullOrWhiteSpace(request.Host))
            entity.Host = request.Host.Trim();

        if (request.Port is > 0 and <= 65535)
            entity.Port = request.Port.Value;

        if (request.Login is not null)
            entity.Login = string.IsNullOrWhiteSpace(request.Login) ? null : request.Login.Trim();

        if (request.Password is not null)
            entity.PasswordEncrypted = string.IsNullOrWhiteSpace(request.Password)
                ? null
                : _encryption.Encrypt(request.Password.Trim());

        if (request.Latitude.HasValue)
            entity.Latitude = request.Latitude;

        if (request.Longitude.HasValue)
            entity.Longitude = request.Longitude;

        if (request.Timezone is not null)
            entity.Timezone = NormalizeOptional(request.Timezone);

        if (request.Locale is not null)
            entity.Locale = NormalizeOptional(request.Locale);

        if (request.LocationLabel is not null)
            entity.LocationLabel = NormalizeOptional(request.LocationLabel);

        ValidateEntity(entity);
        db.SaveChanges();
        return Map(entity);
    }

    public void Delete(long userId, long proxyId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = db.Proxies.FirstOrDefault(x => x.Id == proxyId && x.UserId == userId)
                       ?? throw new InvalidOperationException("Прокси не найден.");

        var usedBySlots = db.SessionSlots.Any(x => x.ProxyId == proxyId);
        if (usedBySlots)
            throw new InvalidOperationException("Прокси используется в слотах сессий. Сначала снимите выбор в слотах.");

        db.Proxies.Remove(entity);
        db.SaveChanges();
    }

    public string? BuildProxyUrl(long userId, long proxyId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = db.Proxies.AsNoTracking()
            .FirstOrDefault(x => x.Id == proxyId && x.UserId == userId);
        if (entity is null)
            return null;

        var password = entity.PasswordEncrypted is null
            ? null
            : _encryption.Decrypt(entity.PasswordEncrypted);

        return ProxyUrlFormatter.Build(entity.Scheme, entity.Host, entity.Port, entity.Login, password);
    }

    public long? GetUserIdForAccount(long adAccountId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.AdAccounts
            .AsNoTracking()
            .Where(x => x.Id == adAccountId)
            .Select(x => (long?)x.UserId)
            .FirstOrDefault();
    }

    private static ProxyDefinition Map(ProxyEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Scheme = entity.Scheme,
        Host = entity.Host,
        Port = entity.Port,
        Login = entity.Login,
        HasPassword = !string.IsNullOrWhiteSpace(entity.PasswordEncrypted),
        Latitude = entity.Latitude,
        Longitude = entity.Longitude,
        Timezone = entity.Timezone,
        Locale = entity.Locale,
        LocationLabel = entity.LocationLabel
    };

    private static void ValidateRequest(string scheme, string host, int port, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Укажите название прокси.");

        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Укажите адрес прокси.");

        if (port is <= 0 or > 65535)
            throw new InvalidOperationException("Порт должен быть от 1 до 65535.");

        _ = ProxyUrlFormatter.NormalizeScheme(scheme);
    }

    private static void ValidateEntity(ProxyEntity entity)
    {
        if (string.IsNullOrWhiteSpace(entity.Name))
            throw new InvalidOperationException("Укажите название прокси.");

        if (string.IsNullOrWhiteSpace(entity.Host))
            throw new InvalidOperationException("Укажите адрес прокси.");

        if (entity.Port is <= 0 or > 65535)
            throw new InvalidOperationException("Порт должен быть от 1 до 65535.");
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
