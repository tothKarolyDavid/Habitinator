using App.Shared.RCL.Models;

using Microsoft.Extensions.Logging;

namespace App.Shared.RCL.Services;

/// <summary>
///     Local-first user preferences shared by the WASM web client and MAUI: reads return instantly
///     from the platform store, saves persist locally and best-effort to the server, and a background
///     refresh keeps the local copy aligned with the server.
/// </summary>
public sealed class LocalFirstUserPreferencesService : LocalFirstSettingsServiceBase<UserPreferences>, IUserPreferencesService
{
    private const string PreferencesKey = "user_preferences_v1";

    public LocalFirstUserPreferencesService(
        IHttpClientFactory http,
        IClientSessionProvider sessionProvider,
        ILocalSettingsStore localStore,
        ILogger<LocalFirstUserPreferencesService> logger)
        : base(
            http,
            sessionProvider,
            localStore,
            logger,
            PreferencesKey,
            "api/settings/preferences",
            UserPreferencesJson.DeserializeOrDefault,
            UserPreferencesJson.Serialize)
    {
    }
}

