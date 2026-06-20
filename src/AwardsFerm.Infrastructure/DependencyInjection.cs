using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using AwardsFerm.Infrastructure.Playwright;
using AwardsFerm.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AwardsFerm.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAwardsFermInfrastructure(this IServiceCollection services, string profilesRoot)
    {
        var profileRepository = new ProfileRepository(profilesRoot);
        services.AddSingleton(profileRepository);
        services.AddSingleton<IProfileRepository>(sp => sp.GetRequiredService<ProfileRepository>());
        services.AddSingleton<ICookieStore, CookieStore>();
        services.AddSingleton<PlaywrightBrowserFactory>();
        services.AddSingleton<ISessionPauseCoordinator, SessionPauseCoordinator>();
        services.AddSingleton<ISessionPreviewCoordinator, SessionPreviewCoordinator>();
        services.AddSingleton<SessionRemoteInputCoordinator>();
        services.AddSingleton<IBrowserSessionRunner, BrowserSessionRunner>();
        return services;
    }
}
