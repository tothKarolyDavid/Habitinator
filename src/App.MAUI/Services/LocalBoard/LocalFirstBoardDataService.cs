using App.MAUI.Data;
using App.Shared.RCL.Models;
using App.Shared.RCL.Services;
using App.Shared.RCL.Services.Remote;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace App.MAUI.Services.LocalBoard;

/// <summary>SQLite-backed board with outbound outbox. Network I/O is driven by <see cref="MauiBoardSyncCoordinator" />.</summary>
#pragma warning disable CA1001 // DI singleton: owns a long-lived SemaphoreSlim and is never disposed by the container.
public sealed partial class LocalFirstBoardDataService(
    IDbContextFactory<LocalBoardDbContext> dbFactory,
    IAuthTokenStore tokens,
    RemoteBoardDataService remote,
    IServiceProvider services,
    IUserTimeZoneService timeZone,
    MauiBoardSyncStatus syncStatus,
    ILogger<LocalFirstBoardDataService> logger)
    : IBoardDataService, IMauiBoardLocalStoreLifecycle
{
    private static volatile bool _schemaReady;
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        var prefs = await services
            .GetRequiredService<IUserPreferencesService>()
            .GetAsync(cancellationToken);
        return DailySchedule.LocalToday(timeZone, prefs.DayStartLocalTime);
    }

    public Task EnsureStoreReadyAsync(CancellationToken cancellationToken = default) =>
        EnsureLocalStoreSchemaAsync(cancellationToken);

    public async Task ClearAllLocalStateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLocalStoreSchemaAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await db.BoardItems.ExecuteDeleteAsync(cancellationToken);
            await db.Outbox.ExecuteDeleteAsync(cancellationToken);
            var meta = await db.Meta.SingleOrDefaultAsync(m => m.Id == 1, cancellationToken);
            if (meta is null)
            {
                db.Meta.Add(new LocalBoardStoreMetaRow { Id = 1, BoundUserKey = null });
            }
            else
            {
                meta.BoundUserKey = null;
                meta.LastSyncCursorUtc = null;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BoardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLocalStoreSchemaAsync(cancellationToken);

        string? userKey = null;
        BoardSnapshot snap;
        var shouldFetchRemote = false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            userKey = await ResolveAuthedUserKeyAsync(cancellationToken);
            if (userKey is null)
            {
                return EmptySnapshot();
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await EnsureUserScopeAsync(db, userKey, cancellationToken);
            var today = await TodayAsync(cancellationToken);
            snap = ReadSnapshot(db, userKey, today);

            if (IsEmpty(snap) && !syncStatus.IsSyncing)
            {
                shouldFetchRemote = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (shouldFetchRemote)
        {
            snap = await TryFetchAndReplaceIfEmptyAsync(userKey, cancellationToken) ?? snap;
        }

        return snap;
    }

    private async Task<BoardSnapshot?> TryFetchAndReplaceIfEmptyAsync(string userKey, CancellationToken cancellationToken)
    {
        BoardSnapshot? fresh = null;
        try
        {
            // Network HTTP call happens OUTSIDE the _gate lock:
            fresh = await remote.GetSnapshotAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Initial board hydrate from API skipped. Offline or error.");
        }

        if (fresh is null)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await EnsureUserScopeAsync(db, userKey, cancellationToken);

            // Re-check under the gate: an item created while the fetch was in flight must not
            // be wiped by the replace, or the optimistic UI state would be lost.
            var today = await TodayAsync(cancellationToken);
            if (!IsEmpty(ReadSnapshot(db, userKey, today)))
            {
                return ReadSnapshot(db, userKey, today);
            }

            await ReplaceMirrorAsync(db, userKey, fresh, cancellationToken);
            return ReadSnapshot(db, userKey, today);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BoardItem?> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await EnsureLocalStoreSchemaAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var userKey = await ResolveAuthedUserKeyAsync(cancellationToken);
            if (userKey is null)
            {
                return null;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await EnsureUserScopeAsync(db, userKey, cancellationToken);
            var row = await db.BoardItems.AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.UserKey == userKey && x.Id == itemId && !x.IsArchived,
                    cancellationToken);
            return row?.ToModel();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Dictionary<Guid, int>> GetStreakMapAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLocalStoreSchemaAsync(cancellationToken);
        var userKey = await ResolveUserKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(userKey))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var dailies = await db.BoardItems
                .Where(x => x.UserKey == userKey && !x.IsArchived && x.Section == BoardSection.Daily)
                .Select(x => new { x.Id, x.Counter })
                .ToListAsync(cancellationToken);
            return dailies.ToDictionary(x => x.Id, x => x.Counter);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLocalStoreSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await SchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await db.Database.EnsureCreatedAsync(cancellationToken);
            try
            {
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogTrace(ex, "Failed to enable WAL mode.");
            }
            await EnsureSqliteBoardColumnsAsync(db, cancellationToken);
            MarkSchemaReady();
        }
        finally
        {
            SchemaLock.Release();
        }
    }

    private static void MarkSchemaReady()
    {
        _schemaReady = true;
    }


    private static async Task EnsureUserScopeAsync(LocalBoardDbContext db, string userKey, CancellationToken cancellationToken)
    {
        var meta = await db.Meta.SingleOrDefaultAsync(m => m.Id == 1, cancellationToken);
        if (meta is null)
        {
            db.Meta.Add(new LocalBoardStoreMetaRow { Id = 1, BoundUserKey = userKey });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (string.Equals(meta.BoundUserKey, userKey, StringComparison.Ordinal))
        {
            return;
        }

        await db.BoardItems.ExecuteDeleteAsync(cancellationToken);
        await db.Outbox.ExecuteDeleteAsync(cancellationToken);
        meta.BoundUserKey = userKey;
        meta.LastSyncCursorUtc = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string?> ResolveUserKeyAsync(CancellationToken cancellationToken)
    {
        var email = await tokens.GetEmailAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email.Trim().ToUpperInvariant();
        }

        var jwt = await tokens.GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(jwt))
        {
            return null;
        }

        var fromJwt = JwtAccessTokenDisplayClaims.TryGetEmail(jwt);
        return string.IsNullOrWhiteSpace(fromJwt) ? null : fromJwt.Trim().ToUpperInvariant();
    }

    private async Task<bool> HasAuthAsync(CancellationToken cancellationToken) =>
        !string.IsNullOrEmpty(await tokens.GetAccessTokenAsync(cancellationToken));

    private async Task<string?> ResolveAuthedUserKeyAsync(CancellationToken cancellationToken)
    {
        if (!await HasAuthAsync(cancellationToken))
        {
            return null;
        }

        var userKey = await ResolveUserKeyAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(userKey) ? null : userKey;
    }

    private static BoardSnapshot ReadSnapshot(LocalBoardDbContext db, string userKey, DateOnly today)
    {
        List<LocalBoardItemRow> items = [.. db.BoardItems.AsNoTracking().Where(x => x.UserKey == userKey && !x.IsArchived)];
        var (habits, dailies, todos) = OrderRows(items, today);
        return new(habits, dailies, todos);
    }

    private static (List<BoardItem> Habits, List<BoardItem> Dailies, List<BoardItem> Todos) OrderRows(
        IReadOnlyList<LocalBoardItemRow> items, DateOnly today)
    {
        // Ordering rules are shared with the server snapshot via BoardOrdering, so both clients render the same order.
        List<BoardItem> habits = [.. BoardOrdering.SortHabits(
            items.Where(x => x.Section == BoardSection.Habit),
            x => x.SortOrder ?? double.MaxValue,
            x => x.CreatedAtUtc ?? DateTimeOffset.MaxValue,
            x => x.Id)
            .Select(x => x.ToModel())];
        List<BoardItem> dailies = [.. BoardOrdering.SortDailies(
            items.Where(x => x.Section == BoardSection.Daily),
            x => IsDailyRowCompleteForToday(x, today),
            x => x.SortOrder ?? double.MaxValue,
            x => x.CreatedAtUtc ?? DateTimeOffset.MaxValue,
            x => x.Id)
            .Select(x => x.ToModel())];
        List<BoardItem> todos = [.. BoardOrdering.SortTodos(
            items.Where(x => x.Section == BoardSection.Todo),
            x => x.IsCompleted,
            x => x.TodoDueDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            x => x.SortOrder ?? double.MaxValue,
            x => x.CreatedAtUtc ?? DateTimeOffset.MaxValue,
            x => x.Id)
            .Select(x => x.ToModel())];
        return (habits, dailies, todos);
    }

    private static bool IsDailyRowCompleteForToday(LocalBoardItemRow row, DateOnly today) =>
        DailySchedule.IsCompletedForToday(row.DailyLastCompletedOn, row.IsCompleted, today);


    private static bool IsEmpty(BoardSnapshot s) =>
        s.Habits.Count == 0 && s.Dailies.Count == 0 && s.Todos.Count == 0;

    private static BoardSnapshot EmptySnapshot() => new([], [], []);

}
#pragma warning restore CA1001
