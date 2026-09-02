using App.Shared.RCL.Models;

using Microsoft.Extensions.Logging;

namespace App.Shared.RCL.Services.Remote;

public sealed class RemoteNotificationSettingsService : LocalFirstSettingsServiceBase<NotificationSettings>, INotificationSettingsService
{
    private const string PreferencesKey = "notification_settings_v1";

    public RemoteNotificationSettingsService(
        IHttpClientFactory http,
        IClientSessionProvider sessionProvider,
        ILocalSettingsStore localStore,
        ILogger<RemoteNotificationSettingsService> logger)
        : base(
            http,
            sessionProvider,
            localStore,
            logger,
            PreferencesKey,
            "api/settings/notifications",
            NotificationSettingsJson.DeserializeOrDefault,
            NotificationSettingsJson.Serialize)
    {
    }
}

