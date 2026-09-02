using App.MAUI.Data;
using App.Shared.RCL.Models;
using App.Shared.RCL.Services;
using App.Shared.RCL.Services.Remote;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace App.MAUI.Services.LocalBoard;

public sealed partial class LocalFirstBoardDataService
{
    public async Task<bool> TryDrainOneOutboxOperationAsync(CancellationToken cancellationToken = default)
    {
        Guid operationId;

        await EnsureLocalStoreSchemaAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var userKey = await ResolveAuthedUserKeyAsync(cancellationToken);
            if (userKey is null)
            {
                return false;
            }

            await EnsureUserScopeAsync(db, userKey, cancellationToken);

            var head = await db.Outbox
                .Where(o => o.UserKey == userKey)
                .OrderBy(o => o.CreatedAtUtc)
                .ThenBy(o => o.OperationId)
                .FirstOrDefaultAsync(cancellationToken);

            if (head is null)
            {
                return false;
            }

            if (head.AttemptCount > 0 && head.LastAttemptUtc is { } last)
            {
                var wait = Backoff(head.AttemptCount);
                if (DateTime.UtcNow < last + wait)
                {
                    return false;
                }
            }

            operationId = head.OperationId;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await ExecuteOutboxRemoteByIdAsync(operationId, remote, cancellationToken);

            await DropOutboxOperationAsync(operationId, cancellationToken);

            return true;
        }
        catch (BoardRemoteConflictException ex)
        {
            return await HandleRemoteConflictAsync(operationId, ex, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outbox operation {OperationId} failed.", operationId);
            await RecordOutboxFailureAsync(operationId, ex.Message, cancellationToken);

            return false;
        }
    }

    private static bool AreItemsContentEqual(BoardItem a, BoardItem b)
    {
        return string.Equals(a.Title, b.Title, StringComparison.Ordinal) &&
               a.IsCompleted == b.IsCompleted &&
               a.Counter == b.Counter &&
               string.Equals(a.Notes ?? string.Empty, b.Notes ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(a.Tags ?? string.Empty, b.Tags ?? string.Empty, StringComparison.Ordinal) &&
               a.TrackPlus == b.TrackPlus &&
               a.TrackMinus == b.TrackMinus &&
               a.NegativeCounter == b.NegativeCounter &&
               a.ResetPeriod == b.ResetPeriod &&
               a.DailyStartDate == b.DailyStartDate &&
               a.DailyRepeat == b.DailyRepeat &&
               a.DailyRepeatInterval == b.DailyRepeatInterval &&
               string.Equals(a.ChecklistJson ?? string.Empty, b.ChecklistJson ?? string.Empty, StringComparison.Ordinal) &&
               a.DailyLastCompletedOn == b.DailyLastCompletedOn &&
               a.TodoDueDate == b.TodoDueDate &&
               NullableDoubleEquals(a.SortOrder, b.SortOrder) &&
               a.IsArchived == b.IsArchived;
    }

    private static bool NullableDoubleEquals(double? a, double? b)
    {
        if (a is null && b is null)
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        return Math.Abs(a.Value - b.Value) < 0.0001;
    }

    private async Task<bool> HandleRemoteConflictAsync(
        Guid operationId,
        BoardRemoteConflictException ex,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(ex, "Outbox operation {OperationId} returned 409 Conflict.", operationId);

        var serverItem = ex.ServerItem;
        if (serverItem is null)
        {
            logger.LogWarning("Conflict exception has no server item; dropping op.");
            await DropOutboxOperationAsync(operationId, cancellationToken);
            RequestSyncSoon();
            return false;
        }

        BoardItem? localItem = null;
        var section = BoardSection.Todo;
        var localTime = DateTimeOffset.MinValue;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.BoardItems.FindAsync([serverItem.Id], cancellationToken);
            if (row is not null)
            {
                localItem = row.ToModel();
                section = row.Section;
            }

            var opRow = await db.Outbox.FindAsync([operationId], cancellationToken);
            if (opRow is not null)
            {
                localTime = new DateTimeOffset(opRow.CreatedAtUtc, TimeSpan.Zero);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (localItem is null)
        {
            logger.LogWarning("Local item not found for conflict resolution; dropping op.");
            await DropOutboxOperationAsync(operationId, cancellationToken);
            RequestSyncSoon();
            return false;
        }

        // 1. Content-Aware check: if user-facing fields match exactly, resolve keeping Server version silently.
        if (AreItemsContentEqual(localItem, serverItem))
        {
            logger.LogInformation("Conflict detected but items are content-identical. Auto-resolving by keeping Server version silently.");
            await ResolveConflictKeepServerAsync(operationId, serverItem, section, cancellationToken);
            return false;
        }

        // 2. Last-Write-Wins, LWW, check: compare local update enqueued time against server updated timestamp.
        var serverTime = serverItem.ServerUpdatedAtUtc ?? DateTimeOffset.MinValue;
        if (localTime >= serverTime)
        {
            logger.LogInformation("Conflict auto-resolved via Last-Write-Wins: Keeping Device version (Local: {LocalTime} >= Server: {ServerTime}).", localTime, serverTime);
            await ResolveConflictKeepMineAsync(operationId, serverItem, cancellationToken);
        }
        else
        {
            logger.LogInformation("Conflict auto-resolved via Last-Write-Wins: Keeping Server version (Local: {LocalTime} < Server: {ServerTime}).", localTime, serverTime);
            await ResolveConflictKeepServerAsync(operationId, serverItem, section, cancellationToken);
        }

        return false;
    }

    private async Task ResolveConflictKeepMineAsync(Guid operationId, BoardItem serverItem, CancellationToken cancellationToken)
    {
        logger.LogInformation("Conflict resolved by user: Keeping Device version.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var opRow = await db.Outbox.FindAsync([operationId], cancellationToken);
            if (opRow is not null)
            {
                var updatedPayload = BoardOutboxPayloadMapper.RemapExpectedVersion(
                    opRow.Kind,
                    opRow.PayloadJson,
                    serverItem.ServerUpdatedAtUtc ?? DateTimeOffset.UtcNow);
                opRow.PayloadJson = updatedPayload;
                opRow.AttemptCount = 0;
                opRow.LastError = null;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResolveConflictKeepServerAsync(
        Guid operationId,
        BoardItem serverItem,
        BoardSection section,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Conflict resolved by user: Keeping Server version.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var opRow = await db.Outbox.FindAsync([operationId], cancellationToken);
            if (opRow is not null)
            {
                db.Outbox.Remove(opRow);
            }

            var localRow = await db.BoardItems.FindAsync([serverItem.Id], cancellationToken);
            if (localRow is not null)
            {
                var userKey = localRow.UserKey;
                localRow.CopyFrom(LocalBoardItemRow.FromModel(section, userKey, serverItem, false));
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        RequestSyncSoon();
    }

    public async Task<string?> TryGetStuckOutboxHintAsync(int minAttempts, CancellationToken cancellationToken = default)
    {
        await EnsureLocalStoreSchemaAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var userKey = await ResolveAuthedUserKeyAsync(cancellationToken);
            if (userKey is null)
            {
                return null;
            }

            var row = await db.Outbox
                .Where(o => o.UserKey == userKey && o.AttemptCount >= minAttempts)
                .OrderByDescending(o => o.AttemptCount)
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(row.LastError)
                ? "Some changes could not sync. Try again when online."
                : $"Sync issue: {row.LastError}";
        }
        finally
        {
            _gate.Release();
        }
    }


    private async Task ExecuteOutboxRemoteByIdAsync(Guid operationId, RemoteBoardDataService api,
        CancellationToken cancellationToken)
    {
        BoardOutboxRow head;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            head = await db.Outbox.FindAsync([operationId], cancellationToken)
                   ?? throw new InvalidOperationException("Outbox entry disappeared.");
        }
        finally
        {
            _gate.Release();
        }

        await ExecuteOutboxRemoteAsync(_gate, dbFactory, head, api, cancellationToken);
    }

    private static T DeserializePayload<T>(BoardOutboxRow head, string failureMessage)
    {
        return System.Text.Json.JsonSerializer.Deserialize<T>(head.PayloadJson, BoardOutboxJson.Options)
               ?? throw new InvalidOperationException(failureMessage);
    }

    private static async Task ExecuteOutboxRemoteAsync(SemaphoreSlim gate, IDbContextFactory<LocalBoardDbContext> dbFactory,
        BoardOutboxRow head, RemoteBoardDataService api, CancellationToken cancellationToken)
    {
        async Task Patch(Guid itemId, BoardItem? updated) =>
            await PatchLocalAsync(gate, dbFactory, itemId, head.UserKey, updated, cancellationToken);

        switch (head.Kind)
        {
            case BoardOutboxOperationKind.Create:
                {
                    var p = DeserializePayload<CreateOutboxPayload>(head, "Invalid create payload.");
                    var serverItem = await api.CreateItemAsync(p.Section, p.Title, p.ClientItemId, head.OperationId, cancellationToken);
                    await CommitCreateSuccessAsync(gate, dbFactory, p.ClientItemId, p.Section, serverItem, head.UserKey, cancellationToken);
                    return;
                }
            case BoardOutboxOperationKind.Rename:
                {
                    var p = DeserializePayload<RenameOutboxPayload>(head, "Invalid rename payload.");
                    var updated = await api.RenameItemAsync(
                        p.Section,
                        p.ItemId,
                        p.Title,
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    await Patch(p.ItemId, updated);
                    return;
                }
            case BoardOutboxOperationKind.Delete:
                {
                    var p = DeserializePayload<SectionItemOutboxPayload>(head, "Invalid delete payload.");
                    _ = await api.DeleteItemAsync(
                        p.Section,
                        p.ItemId,
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    return;
                }
            case BoardOutboxOperationKind.Toggle:
                {
                    var p = DeserializePayload<SectionItemOutboxPayload>(head, "Invalid toggle payload.");
                    var updated = await api.ToggleItemAsync(
                        p.Section,
                        p.ItemId,
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    // The server rejected the toggle, for example because the item no longer exists. Pull server
                    // truth so the optimistic local state converges instead of staying divergent.
                    updated ??= await api.GetItemAsync(p.ItemId, cancellationToken);

                    await Patch(p.ItemId, updated);
                    return;
                }
            case BoardOutboxOperationKind.CompleteDailyForDate:
                {
                    var p = DeserializePayload<CompleteDailyOutboxPayload>(head, "Invalid complete-daily payload.");
                    var updated = await api.CompleteDailyForDateAsync(
                        p.ItemId,
                        p.CompletedOn,
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    // The server rejected the retro check, for example because the daily is checked
                    // for today elsewhere or the schedule changed. Replace the optimistic local
                    // state with server truth so the board and streaks match the server.
                    updated ??= await api.GetItemAsync(p.ItemId, cancellationToken);

                    await Patch(p.ItemId, updated);
                    return;
                }
            case BoardOutboxOperationKind.HabitIncrement:
                {
                    var p = DeserializePayload<ItemIdOutboxPayload>(head, "Invalid habit+ payload.");
                    var updated = await api.IncrementHabitPlusAsync(
                        p.ItemId,
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    await Patch(p.ItemId, updated);
                    return;
                }
            case BoardOutboxOperationKind.HabitDecrement:
                {
                    var p = DeserializePayload<ItemIdOutboxPayload>(head, "Invalid habit− payload.");
                    var updated = await api.IncrementHabitMinusAsync(
                        p.ItemId,
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    await Patch(p.ItemId, updated);
                    return;
                }
            case BoardOutboxOperationKind.UpdateHabit:
                {
                    var p = DeserializePayload<UpdateHabitOutboxPayload>(head, "Invalid habit update payload.");
                    var updated = await api.UpdateHabitAsync(
                        p.ItemId,
                        new UpdateHabitArgs(
                            p.Title,
                            p.Notes,
                            p.Tags,
                            p.TrackPlus,
                            p.TrackMinus,
                            p.ResetPeriod,
                            p.Counter,
                            p.NegativeCounter,
                            p.ChecklistJson,
                            p.SortOrder),
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    await Patch(p.ItemId, updated);
                    return;
                }
            case BoardOutboxOperationKind.UpdateTodo:
                {
                    var p = DeserializePayload<UpdateTodoOutboxPayload>(head, "Invalid todo update payload.");
                    var updated = await api.UpdateTodoAsync(
                        p.ItemId,
                        new UpdateTodoArgs(
                            p.Title,
                            p.Notes,
                            p.Tags,
                            p.ChecklistJson,
                            p.DueDate,
                            p.SortOrder,
                            p.TodoRepeatIntervalDays),
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    await Patch(p.ItemId, updated);
                    return;
                }
            case BoardOutboxOperationKind.UpdateDaily:
                {
                    var p = DeserializePayload<UpdateDailyOutboxPayload>(head, "Invalid daily update payload.");
                    var updated = await api.UpdateDailyAsync(
                        p.ItemId,
                        new UpdateDailyArgs(
                            p.Title,
                            p.Notes,
                            p.Tags,
                            p.StartDate,
                            p.Repeat,
                            p.RepeatInterval,
                            p.ChecklistJson,
                            p.Counter,
                            p.SortOrder),
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    await Patch(p.ItemId, updated);
                    return;
                }
            case BoardOutboxOperationKind.Archive:
                {
                    var p = DeserializePayload<SectionItemOutboxPayload>(head, "Invalid archive payload.");
                    var updated = await api.ArchiveItemAsync(
                        p.Section,
                        p.ItemId,
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    await Patch(p.ItemId, updated);
                    return;
                }
            case BoardOutboxOperationKind.Unarchive:
                {
                    var p = DeserializePayload<SectionItemOutboxPayload>(head, "Invalid unarchive payload.");
                    var updated = await api.UnarchiveItemAsync(
                        p.Section,
                        p.ItemId,
                        head.OperationId,
                        p.ExpectedServerUpdatedAtUtc,
                        cancellationToken);
                    await Patch(p.ItemId, updated);
                    return;
                }
            default:
                throw new InvalidOperationException($"Unknown outbox kind {head.Kind}.");
        }
    }

    private async Task DropOutboxOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var still = await db.Outbox.FindAsync([operationId], cancellationToken);
            if (still is not null)
            {
                db.Outbox.Remove(still);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RecordOutboxFailureAsync(Guid operationId, string message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var still = await db.Outbox.FindAsync([operationId], cancellationToken);
            if (still is not null)
            {
                still.AttemptCount++;
                still.LastAttemptUtc = DateTime.UtcNow;
                still.LastError = message;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task CommitCreateSuccessAsync(SemaphoreSlim gate, IDbContextFactory<LocalBoardDbContext> dbFactory,
        Guid clientId, BoardSection section, BoardItem serverItem, string userKey, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var old = await db.BoardItems.FindAsync([clientId], cancellationToken);
            if (old is not null)
            {
                db.BoardItems.Remove(old);
            }

            db.BoardItems.Add(LocalBoardItemRow.FromModel(section, userKey, serverItem, false));

            foreach (var row in await db.Outbox.Where(o => o.UserKey == userKey).ToListAsync(cancellationToken))
            {
                row.PayloadJson = BoardOutboxPayloadMapper.RemapClientToServerId(row.Kind, row.PayloadJson, clientId, serverItem.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task PatchLocalAsync(SemaphoreSlim gate, IDbContextFactory<LocalBoardDbContext> dbFactory,
        Guid itemId, string userKey, BoardItem? serverItem, CancellationToken cancellationToken)
    {
        if (serverItem is null)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.BoardItems.FirstOrDefaultAsync(
                x => x.UserKey == userKey && x.Id == itemId,
                cancellationToken);
            if (row is null)
            {
                return;
            }

            var section = row.Section;
            var awaiting = row.AwaitingServerCreate;
            row.CopyFrom(LocalBoardItemRow.FromModel(section, userKey, serverItem, awaiting));
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ApplyLocalToggle(BoardSection section, LocalBoardItemRow row, DateOnly today)
    {
        switch (section)
        {
            case BoardSection.Habit:
                row.IsCompleted = !row.IsCompleted;
                break;
            case BoardSection.Daily:
                {
                    (row.DailyLastCompletedOn, row.IsCompleted) = DailySchedule.ToggleForToday(
                        row.DailyLastCompletedOn, row.IsCompleted, today);
                    break;
                }
            case BoardSection.Todo:
                row.IsCompleted = !row.IsCompleted;
                break;
        }
    }

    private async Task<T> MutateAsync<T>(Func<LocalBoardDbContext, string, Task<T>> action, CancellationToken cancellationToken)
    {
        await EnsureLocalStoreSchemaAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var userKey = await ResolveAuthedUserKeyAsync(cancellationToken)
                ?? throw new InvalidOperationException("Sign in to change your board.");

            await EnsureUserScopeAsync(db, userKey, cancellationToken);
            return await action(db, userKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> MutateWithSyncAsync<T>(Func<LocalBoardDbContext, string, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            var result = await MutateAsync(action, cancellationToken);
            // Granular stats invalidation: only affected tag caches, not all
            if (result is BoardItem bi)
            {
                InvalidateStatsCache(bi);
            }
            else if (result is not null)
            {
                // For operations without a direct BoardItem result like Delete which returns bool, fall back to full invalidation
                InvalidateStatsCache();
            }
            return result;
        }
        finally
        {
            RequestSyncSoon();
        }
    }

    private void InvalidateStatsCache(BoardItem? item = null)
    {
        try
        {
            var stats = services.GetService<IActivityStatisticsReader>();
            if (stats != null)
            {
                if (item != null)
                {
                    stats.InvalidateForItem(item);
                }
                else
                {
                    stats.InvalidateCache();
                }
            }

            var offline = services.GetService<OfflineActivityStatisticsProvider>();
            if (offline != null)
            {
                if (item != null)
                {
                    offline.InvalidateForTags(BoardTagUtil.ParseTags(item.Tags));
                }
                else
                {
                    offline.InvalidateForTags(null);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Could not invalidate stats cache.");
        }
    }

    private void RequestSyncSoon()
    {
        try
        {
            services.GetRequiredService<MauiBoardSyncCoordinator>().RequestSync();
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Could not request board sync.");
        }
    }

    private static void Enqueue<T>(LocalBoardDbContext db, string userKey, BoardOutboxOperationKind kind, T payload)
    {
        db.Outbox.Add(
            new BoardOutboxRow
            {
                OperationId = Guid.NewGuid(),
                UserKey = userKey,
                Kind = kind,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload, BoardOutboxJson.Options),
                CreatedAtUtc = DateTime.UtcNow
            });
    }

    private static async Task<bool> TryCoalesceDeletePendingCreateAsync(LocalBoardDbContext db, string userKey, Guid itemId,
        CancellationToken cancellationToken)
    {
        var pending = await db.Outbox
            .Where(o => o.UserKey == userKey && o.Kind == BoardOutboxOperationKind.Create)
            .ToListAsync(cancellationToken);

        foreach (var row in pending)
        {
            var p = System.Text.Json.JsonSerializer.Deserialize<CreateOutboxPayload>(row.PayloadJson, BoardOutboxJson.Options);
            if (p?.ClientItemId != itemId)
            {
                continue;
            }

            db.Outbox.Remove(row);
            var entity = await db.BoardItems.FindAsync([itemId], cancellationToken);
            if (entity is not null)
            {
                db.BoardItems.Remove(entity);
            }

            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempt, 8))));

}
