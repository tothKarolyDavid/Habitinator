using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace App.Shared.RCL.Services.Remote;

public sealed class RemoteActivityStatisticsReader : IActivityStatisticsReader
{
    private const string StatsOverviewCacheKeyPrefix = "habitinator_stats_overview_cache_v1";
    private const string StatsPersistentPrefix = "habitinator_stats_v2_";
    private const string StatsIndexKey = "habitinator_stats_index_v2";
    private static readonly JsonSerializerOptions Serializer = JsonDefaults.Api;

    private readonly IHttpClientFactory _http;
    private readonly ILocalSettingsStore? _localStore;
    private readonly ConcurrentDictionary<string, (object? Value, DateTime ExpiresAtUtc)> _cache = new();
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(60);

    public RemoteActivityStatisticsReader(IHttpClientFactory http, ILocalSettingsStore? localStore = null)
    {
        _http = http;
        _localStore = localStore;
        if (_localStore != null)
        {
            var raw = _localStore.Read(StatsOverviewCacheKeyPrefix);
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<ActivityOverviewDto>(raw, Serializer);
                    if (cached != null)
                    {
                        var defaultPath = "api/activity/overview";
                        _cache[defaultPath] = (cached, DateTime.UtcNow.AddMinutes(15));
                    }
                }
                catch
                {
                    // Ignore corrupted local cache
                }
            }

            // Hydrate any v2 persisted entries into memory as stale fallback
            TryHydratePersistentCache();
        }
    }

    private void TryHydratePersistentCache()
    {
        if (_localStore == null)
        {
            return;
        }

        try
        {
            var indexRaw = _localStore.Read(StatsIndexKey);
            if (string.IsNullOrEmpty(indexRaw))
            {
                return;
            }

            var keys = JsonSerializer.Deserialize<HashSet<string>>(indexRaw, Serializer);
            if (keys == null)
            {
                return;
            }

            foreach (var key in keys)
            {
                var raw = _localStore.Read(key);
                if (!string.IsNullOrEmpty(raw))
                {
                    // We do not know the type, so keep raw for on-demand deserialize. Keep placeholder?
                    // For now hydrate only overview default already handled. Other types will be loaded on demand via TryReadPersistent.
                }
            }
        }
        catch (Exception ex)
        {
            // Ignore - best effort hydration of persistent cache
            _ = ex;
        }
    }

    private HttpClient Client => _http.CreateClient("api");

    public async Task<ActivityOverviewDto> GetOverviewAsync(string? periodKey, string? tag = null,
        CancellationToken cancellationToken = default)
    {
        var path = "api/activity/overview" + BuildActivityQuery(periodKey, tag);
        var result = await GetJsonCachedOrThrowAsync<ActivityOverviewDto>(path, DefaultCacheTtl, cancellationToken);
        WritePersistent(path, result);
        if (_localStore != null && string.IsNullOrEmpty(periodKey) && string.IsNullOrEmpty(tag))
        {
            try
            {
                _localStore.Write(StatsOverviewCacheKeyPrefix, JsonSerializer.Serialize(result, Serializer));
            }
            catch (Exception ex)
            {
                // Ignore storage write errors
                _ = ex;
            }
        }

        return result;
    }

    public async Task<ActivityDashboardDto> GetDashboardAsync(string? periodKey, string? tag = null,
        CancellationToken cancellationToken = default)
    {
        var path = "api/activity/dashboard" + BuildActivityQuery(periodKey, tag);
        var result = await GetJsonCachedOrThrowAsync<ActivityDashboardDto>(path, DefaultCacheTtl, cancellationToken);
        WritePersistent(path, result);
        return result;
    }

    public async Task<DailyContributionsViewDto> GetDailyContributionsAsync(string? periodKey, string? tag = null,
        CancellationToken cancellationToken = default)
    {
        var path = "api/activity/daily-contributions" + BuildActivityQuery(periodKey, tag);
        var result = await GetJsonCachedOrThrowAsync<DailyContributionsViewDto>(path, DefaultCacheTtl, cancellationToken);
        WritePersistent(path, result);
        return result;
    }

    public async Task<HabitContributionsViewDto> GetHabitContributionsAsync(string? periodKey, string? tag = null,
        CancellationToken cancellationToken = default)
    {
        var path = "api/activity/habit-contributions" + BuildActivityQuery(periodKey, tag);
        var result = await GetJsonCachedOrThrowAsync<HabitContributionsViewDto>(path, DefaultCacheTtl, cancellationToken);
        WritePersistent(path, result);
        return result;
    }

    public async Task<ActivityDayDetailDto> GetActivityDayDetailAsync(DateOnly day, string? tag = null,
        CancellationToken cancellationToken = default)
    {
        var s = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = "api/activity/day?date=" + Uri.EscapeDataString(s) +
                   (string.IsNullOrEmpty(tag) ? string.Empty : "&tag=" + Uri.EscapeDataString(tag));
        var result = await GetJsonCachedOrThrowAsync<ActivityDayDetailDto>(path, DefaultCacheTtl, cancellationToken);
        WritePersistent(path, result);
        return result;
    }

    public void InvalidateCache()
    {
        _cache.Clear();
        ClearPersistentStore();
    }

    private void ClearPersistentStore()
    {
        if (_localStore == null)
        {
            return;
        }

        try
        {
            _localStore.Write(StatsOverviewCacheKeyPrefix, "");
            var indexRaw = _localStore.Read(StatsIndexKey);
            if (string.IsNullOrEmpty(indexRaw))
            {
                return;
            }

            var keys = JsonSerializer.Deserialize<HashSet<string>>(indexRaw, Serializer);
            if (keys == null)
            {
                return;
            }

            foreach (var k in keys)
            {
                SafeWriteStore(k, "");
            }

            _localStore.Write(StatsIndexKey, "");
        }
        catch (Exception ex)
        {
            // Ignore storage write errors
            _ = ex;
        }
    }

    private void SafeWriteStore(string key, string value)
    {
        try
        {
            _localStore?.Write(key, value);
        }
        catch (Exception ex)
        {
            // Ignore - best effort
            _ = ex;
        }
    }

    public void InvalidateForTags(IEnumerable<string>? tags)
    {
        var tagSet = BuildTagSet(tags);
        var hasTags = tagSet.Count > 0;

        // Remove only entries whose tag filter intersects with the changed item tags, or tag == null (all)
        foreach (var key in _cache.Keys.ToList())
        {
            var cachedTag = ExtractTagFromRequestUri(key);
            if (ShouldInvalidateForTag(cachedTag, tagSet, hasTags))
            {
                _cache.TryRemove(key, out _);
                RemovePersistentForRequestUri(key);
            }
        }

        // Also handle legacy overview key
        if (_localStore != null && !hasTags)
        {
            SafeWriteStore(StatsOverviewCacheKeyPrefix, "");
        }
    }

    private static HashSet<string> BuildTagSet(IEnumerable<string>? tags)
    {
#pragma warning disable IDE0028 // Collection initialization can be simplified - comparer required, collection expression would lose OrdinalIgnoreCase
        var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0028
        if (tags == null)
        {
            return tagSet;
        }

        foreach (var t in tags)
        {
            foreach (var parsed in App.Shared.RCL.Models.BoardTagUtil.ParseTags(t))
            {
                tagSet.Add(parsed);
            }
        }

        return tagSet;
    }

    public void InvalidateForItem(App.Shared.RCL.Models.BoardItem item)
    {
        InvalidateForTags(App.Shared.RCL.Models.BoardTagUtil.ParseTags(item.Tags));
    }

    private static string? ExtractTagFromRequestUri(string requestUri)
    {
        var qIdx = requestUri.IndexOf('?');
        if (qIdx < 0)
        {
            return null;
        }

        var query = requestUri[qIdx..];
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "tag")
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }

        return null;
    }

    private static bool ShouldInvalidateForTag(string? cachedTag, HashSet<string> itemTags, bool hasTags)
    {
        if (string.IsNullOrEmpty(cachedTag))
        {
            // Cached entry is for all tags, any item change affects it
            return true;
        }

        if (!hasTags)
        {
            // Item has no tags, it never appears in tag-filtered views
            return false;
        }

        var cachedTags = new HashSet<string>(App.Shared.RCL.Models.BoardTagUtil.ParseTags(cachedTag), StringComparer.OrdinalIgnoreCase);
        return cachedTags.Overlaps(itemTags);
    }

    private void RemovePersistentForRequestUri(string requestUri)
    {
        if (_localStore == null)
        {
            return;
        }

        try
        {
            var key = PersistentKey(requestUri);
            _localStore.Write(key, "");
            var indexRaw = _localStore.Read(StatsIndexKey);
            if (!string.IsNullOrEmpty(indexRaw))
            {
                var index = JsonSerializer.Deserialize<HashSet<string>>(indexRaw, Serializer);
                if (index != null && index.Remove(key))
                {
                    _localStore.Write(StatsIndexKey, JsonSerializer.Serialize(index, Serializer));
                }
            }
        }
        catch (Exception ex)
        {
            // Ignore - best effort to remove persistent entry
            _ = ex;
        }
    }

    public bool TryGetCachedOverview(string? periodKey, string? tag, out ActivityOverviewDto? overview)
    {
        var path = "api/activity/overview" + BuildActivityQuery(periodKey, tag);
        if (_cache.TryGetValue(path, out var entry) && entry.Value is ActivityOverviewDto cached)
        {
            // Return even if expired as stale fallback for offline
            overview = cached;
            return true;
        }

        if (TryReadPersistent(path, out ActivityOverviewDto? persisted) && persisted != null)
        {
            overview = persisted;
            _cache[path] = (persisted, DateTime.UtcNow.AddMinutes(15));
            return true;
        }

        if (TryReadLegacyOverview(periodKey, tag, out overview) && overview != null)
        {
            _cache[path] = (overview, DateTime.UtcNow.AddMinutes(15));
            return true;
        }

        overview = null;
        return false;
    }

    private bool TryReadLegacyOverview(string? periodKey, string? tag, out ActivityOverviewDto? overview)
    {
        overview = null;
        if (_localStore == null || !string.IsNullOrEmpty(periodKey) || !string.IsNullOrEmpty(tag))
        {
            return false;
        }

        var raw = _localStore.Read(StatsOverviewCacheKeyPrefix);
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        try
        {
            overview = JsonSerializer.Deserialize<ActivityOverviewDto>(raw, Serializer);
            return overview != null;
        }
        catch (Exception ex)
        {
            // Ignore corrupted local cache
            _ = ex;
            return false;
        }
    }

    private static string PersistentKey(string requestUri)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(requestUri));
        return $"{StatsPersistentPrefix}{Convert.ToHexString(hash)[..16]}";
    }

    private void WritePersistent<T>(string requestUri, T value) where T : class
    {
        if (_localStore == null)
        {
            return;
        }

        try
        {
            var key = PersistentKey(requestUri);
            var json = JsonSerializer.Serialize(value, Serializer);
            _localStore.Write(key, json);
            var indexRaw = _localStore.Read(StatsIndexKey);
            var index = string.IsNullOrEmpty(indexRaw)
                ? []
                : JsonSerializer.Deserialize<HashSet<string>>(indexRaw, Serializer) ?? [];
            if (index.Add(key))
            {
                _localStore.Write(StatsIndexKey, JsonSerializer.Serialize(index, Serializer));
            }
        }
        catch (Exception ex)
        {
            // Ignore - best effort to write persistent cache
            _ = ex;
        }
    }

    private bool TryReadPersistent<T>(string requestUri, out T? value) where T : class
    {
        value = default;
        if (_localStore == null)
        {
            return false;
        }

        try
        {
            var key = PersistentKey(requestUri);
            var raw = _localStore.Read(key);
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            var des = JsonSerializer.Deserialize<T>(raw, Serializer);
            if (des is not null)
            {
                value = des;
                return true;
            }
        }
        catch (Exception ex)
        {
            // Ignore - best effort to read persistent cache
            _ = ex;
        }

        return false;
    }

    private static bool IsOfflineException(Exception ex)
    {
        if (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return true;
        }

        if (ex is InvalidOperationException ioe && ioe.InnerException is HttpRequestException)
        {
            return true;
        }

        // Treat any InvalidOperationException that is not auth-related as offline
        if (ex is InvalidOperationException ioe2 && !ioe2.Message.Contains("Sign in required", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string BuildActivityQuery(string? periodKey, string? tag)
    {
        var q = new List<string>();
        if (!string.IsNullOrEmpty(periodKey))
        {
            q.Add("period=" + Uri.EscapeDataString(periodKey));
        }

        if (!string.IsNullOrEmpty(tag))
        {
            q.Add("tag=" + Uri.EscapeDataString(tag));
        }

        return q.Count == 0 ? string.Empty : "?" + string.Join("&", q);
    }

    private async Task<T> GetJsonCachedOrThrowAsync<T>(string requestUri, TimeSpan ttl, CancellationToken cancellationToken) where T : class
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(requestUri, out var entry) && entry.ExpiresAtUtc > now && entry.Value is T cachedValue)
        {
            return cachedValue;
        }

        try
        {
            var result = await GetJsonOrThrowAsync<T>(requestUri, cancellationToken);
            _cache[requestUri] = (result, now.Add(ttl));
            return result;
        }
        catch (Exception ex) when (IsOfflineException(ex))
        {
            // Do not fallback for auth failures
            if (ex is InvalidOperationException ioe && ioe.Message.Contains("Sign in required", StringComparison.Ordinal))
            {
                throw;
            }

            if (_cache.TryGetValue(requestUri, out var stale) && stale.Value is T staleVal)
            {
                return staleVal;
            }

            if (TryReadPersistent(requestUri, out T? persisted) && persisted != null)
            {
                _cache[requestUri] = (persisted, now.Add(ttl));
                return persisted;
            }

            throw;
        }
    }

    private async Task<T> GetJsonOrThrowAsync<T>(string requestUri, CancellationToken cancellationToken) where T : class
    {
        using var res = await Client.GetAsync(requestUri, cancellationToken);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Sign in required. Open Log in and try again.");
        }

        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<T>(Serializer, cancellationToken);
        return body is null
            ? throw new InvalidOperationException("Empty response from the statistics API.")
            : body;
    }
}
