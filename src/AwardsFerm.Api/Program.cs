using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using AwardsFerm.Api.Auth;
using AwardsFerm.Api.Data;
using AwardsFerm.Api.Hubs;
using AwardsFerm.Api.Options;
using AwardsFerm.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<YandexRsyaOptions>(builder.Configuration.GetSection(YandexRsyaOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

var sqlitePath = Environment.GetEnvironmentVariable("SQLITE_DB_PATH")
                 ?? builder.Configuration["Database:Path"]
                 ?? "/var/lib/awardsferm/awardsferm.db";
var sqliteDir = Path.GetDirectoryName(sqlitePath);
if (!string.IsNullOrWhiteSpace(sqliteDir))
    Directory.CreateDirectory(sqliteDir);
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={sqlitePath}"));

var auth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.JwtSecret));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = auth.JwtIssuer,
            ValidAudience = auth.JwtAudience,
            IssuerSigningKey = jwtKey
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddHttpClient<YandexRsyaStatisticsService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddSingleton<SessionSlotStore>();
builder.Services.AddSingleton<ProxyStore>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<SessionRunnerService>();
builder.Services.AddHostedService<ScheduledSessionService>();
builder.Services.AddSingleton<SessionEventBroadcaster>();
builder.Services.AddSingleton<AwardsFerm.Core.Interfaces.ISessionEventReporter>(sp =>
    sp.GetRequiredService<SessionEventBroadcaster>());
builder.Services.AddScoped<UserAccountResolver>();
builder.Services.AddScoped<UserProfitService>();
builder.Services.AddSingleton<TokenEncryptionService>();
builder.Services.AddSingleton<JwtTokenService>();

builder.Services.AddHttpClient("worker", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddHttpClient("worker-quick", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("web", policy =>
    {
        policy.WithOrigins(
                "http://localhost:5173",
                "http://localhost:3000",
                "http://localhost:8080")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<SessionHub>("/hubs/session");

app.Run();
