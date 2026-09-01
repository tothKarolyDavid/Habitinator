using System.Collections.Frozen;

using App.Shared.RCL;
using App.Shared.RCL.Models;
using App.Shared.RCL.Services;
using App.Web.Data;

using Microsoft.EntityFrameworkCore;

namespace App.Web.Services;

public sealed record DailyBackfillArgs(
    DateOnly? DailyStart,
    DailyRepeatType Repeat,
    int Interval,
    int Streak);

public sealed class BoardPersistenceService(
    ApplicationDbContext dbContext,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    MemoryCacheStore<BoardSnapshot> snapshotCache,
    MemoryCacheStore<Dictionary<Guid, int>> streakCache,
    IBoardChangeNotifier boardChangeNotifier,
    IUserTimeZoneService timeZone,
    DailyStreakCalculationService streakCalculator)
{
    private async Task<DateOnly> TodayAsync(Guid userId, CancellationToken cancellationToken)
    {
        var (today, _) = await TodayAndDayStartAsync(userId, cancellationToken);
        return today;
    }

    private Task<(DateOnly Today, TimeSpan? DayStartLocalTime)> TodayAndDayStartAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        UserDayContext.LoadAsync(dbContext, userId, timeZone, cancellationToken);

    private static IQueryable<BoardItemEntity> LiveBoardItems(ApplicationDbContext db, Guid userId) =>
        db.BoardItems.Where(x => x.UserId == userId && x.DeletedAtUtc == null && !x.IsArchived);

    private static BoardMutationStatus MatchExpected(BoardItemEntity? entity, DateTimeOffset? expectedUpdatedAtUtc)
    {
        if (entity is null)
        {
            return BoardMutationStatus.NotFound;
        }

        if (expectedUpdatedAtUtc is null)
        {
            return BoardMutationStatus.Ok;
        }

        return entity.UpdatedAtUtc.Equals(expectedUpdatedAtUtc.Value)
            ? BoardMutationStatus.Ok
            : BoardMutationStatus.Conflict;
    }

    private Task<BoardItemEntity?> FindLiveItemAsync(
        Guid userId,
        BoardSection section,
        Guid itemId,
        CancellationToken cancellationToken) =>
        dbContext.BoardItems.FirstOrDefaultAsync(
            x => x.UserId == userId && x.Section == section && x.Id == itemId && x.DeletedAtUtc == null,
            cancellationToken);

    /// <summary>
    ///     Shared mutation pipeline: load, expected-version check, mutate, save, map to model,
    ///     and notify. The mutate delegate returns <c>false</c> to abort the mutation with
    ///     <see cref="BoardMutationStatus.NotFound" /> without saving. <paramref name="toModel" />
    ///     returning <c>null</c> produces an <see cref="BoardMutationStatus.Ok" /> result without a model.
    /// </summary>
    private async Task<BoardMutationResult> MutateItemAsync(
        Guid userId,
        BoardSection section,
        Guid itemId,
        DateTimeOffset? expectedUpdatedAtUtc,
        Func<BoardItemEntity, Task<bool>> mutate,
        Func<BoardItemEntity, Task<BoardItem>>? toModel,
        CancellationToken cancellationToken)
    {
        var (entity, conflict) = await LoadAndCheckAsync(userId, section, itemId, expectedUpdatedAtUtc, cancellationToken);
        if (entity is null || conflict is not null)
        {
            return conflict ?? new BoardMutationResult(BoardMutationStatus.NotFound, null);
        }

        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (!await mutate(entity))
        {
            return new BoardMutationResult(BoardMutationStatus.NotFound, null);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var model = toModel is null ? null : await toModel(entity);
        await boardChangeNotifier.NotifyBoardChangedAsync(userId, cancellationToken);
        return new BoardMutationResult(BoardMutationStatus.Ok, model);
    }

    private async Task<(BoardItemEntity? Entity, BoardMutationResult? Conflict)> LoadAndCheckAsync(
        Guid userId,
        BoardSection section,
        Guid itemId,
        DateTimeOffset? expected,
        CancellationToken cancellationToken)
    {
        var entity = await FindLiveItemAsync(userId, section, itemId, cancellationToken);
        if (entity is null)
        {
            return (null, new BoardMutationResult(BoardMutationStatus.NotFound, null));
        }

        var st = MatchExpected(entity, expected);
        if (st == BoardMutationStatus.Conflict)
        {
            return (null, new BoardMutationResult(
                BoardMutationStatus.Conflict,
                await ToModelWithDailyStreaksAsync(userId, entity, cancellationToken)));
        }

        return (entity, null);
    }

    public async Task<BoardSyncDelta> GetSyncDeltaAsync(
        Guid userId,
        DateTimeOffset cursorExclusive,
        CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var changed = await readDb.BoardItems
            .AsNoTracking()
            .Where(x => x.UserId == userId
                        && ((x.DeletedAtUtc == null && x.UpdatedAtUtc > cursorExclusive)
                            || (x.DeletedAtUtc != null && x.DeletedAtUtc > cursorExclusive)))
            .OrderBy(x => x.UpdatedAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var upserts = new List<BoardSyncItem>();
        var deletedIds = new List<Guid>();
        DateTimeOffset? next = null;

        var (today, dayStart) = await TodayAndDayStartAsync(userId, cancellationToken);
        var dailyRows = changed.Where(x => x.DeletedAtUtc is null && x.Section == BoardSection.Daily).ToList();
        var dailyStreaks = await streakCalculator.BuildDailyStreakMapAsync(userId, dailyRows, today, dayStart, readDb, cancellationToken);

        foreach (var row in changed)
        {
            if (row.DeletedAtUtc is not null)
            {
                deletedIds.Add(row.Id);
                next = MaxCursor(next, row.DeletedAtUtc.Value);
                continue;
            }

            upserts.Add(new BoardSyncItem(row.Section, ToModelWithToday(row, today, dailyStreaks)));
            next = MaxCursor(next, row.UpdatedAtUtc);
        }

        var nextCursor = (next ?? cursorExclusive).ToString("O");
        return new BoardSyncDelta(upserts, deletedIds, nextCursor);
    }

    private static DateTimeOffset? MaxCursor(DateTimeOffset? a, DateTimeOffset b) => a is null || b > a ? b : a;

    public async Task<BoardItem?> GetItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var cached = await GetSnapshotAsync(userId, cancellationToken);
        var item = cached.Habits.FirstOrDefault(x => x.Id == itemId)
            ?? cached.Dailies.FirstOrDefault(x => x.Id == itemId)
            ?? cached.Todos.FirstOrDefault(x => x.Id == itemId);
        if (item is not null)
        {
            return item;
        }

        await using var readDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await readDb.BoardItems.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.Id == itemId && x.DeletedAtUtc == null && !x.IsArchived,
                cancellationToken);
        return entity is null ? null : await ToModelWithDailyStreaksAsync(userId, entity, cancellationToken);
    }

    public async Task<BoardSnapshot> GetSnapshotAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (snapshotCache.TryGet(userId, out var cached))
        {
            return cached;
        }

        await using var readDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await LiveBoardItems(readDb, userId)
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var (today, dayStart) = await TodayAndDayStartAsync(userId, cancellationToken);
        var dailies = items.Where(x => x.Section == BoardSection.Daily).ToList();
        var dailyStreaks = await streakCalculator.BuildDailyStreakMapAsync(userId, dailies, today, dayStart, readDb, cancellationToken);
        var snapshot = BuildSnapshot(items, today, dailyStreaks);
        snapshotCache.Set(userId, snapshot);
        var streakMap = new Dictionary<Guid, int>(dailyStreaks);
        streakCache.Set(userId, streakMap);
        return snapshot;
    }

    public async Task<Dictionary<Guid, int>> GetDailyStreakMapAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (streakCache.TryGet(userId, out var cached))
        {
            return cached;
        }

        await using var readDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var dailies = await LiveBoardItems(readDb, userId).AsNoTracking()
            .Where(x => x.Section == BoardSection.Daily)
            .ToListAsync(cancellationToken);
        var (today, dayStart) = await TodayAndDayStartAsync(userId, cancellationToken);
        var map = await streakCalculator.BuildDailyStreakMapAsync(userId, dailies, today, dayStart, readDb, cancellationToken);
        var result = new Dictionary<Guid, int>(map);
        streakCache.Set(userId, result);
        return result;
    }

    public async Task<BoardItem> CreateItemAsync(Guid userId, BoardSection section, string title,
        Guid? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var utcNow = DateTimeOffset.UtcNow;
        if (itemId is { } id)
        {
            var existing = await dbContext.BoardItems
                .FirstOrDefaultAsync(x => x.UserId == userId && x.Id == id, cancellationToken);
            if (existing is not null)
            {
                existing.DeletedAtUtc = null;
                existing.Title = ZalgoSanitizer.SanitizeAndTrim(title);
                existing.UpdatedAtUtc = utcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                var restored = await ToModelWithDailyStreaksAsync(userId, existing, cancellationToken);
                await boardChangeNotifier.NotifyBoardChangedAsync(userId, cancellationToken);
                return restored;
            }
        }

        var entity = new BoardItemEntity
        {
            Id = itemId ?? Guid.NewGuid(),
            UserId = userId,
            Section = section,
            Title = ZalgoSanitizer.SanitizeAndTrim(title),
            Notes = null,
            Tags = null,
            TrackPlus = true,
            TrackMinus = true,
            ResetPeriod = (int)HabitResetPeriod.Daily,
            IsCompleted = false,
            Counter = 0,
            NegativeCounter = 0,
            // Null means "due from UTC today". It does not block streak backfill or stats for prior days. See DailySchedule.
            DailyStartDate = null,
            DailyRepeatType = (int)DailyRepeatType.Daily,
            DailyRepeatInterval = 1,
            ChecklistJson = null,
            DailyLastCompletedOn = null,
            CreatedAtUtc = utcNow,
            UpdatedAtUtc = utcNow,
            SortOrder = await GetInitialSortOrderAsync(userId, section, cancellationToken)
        };

        dbContext.BoardItems.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        var created = await ToModelWithDailyStreaksAsync(userId, entity, cancellationToken);
        await boardChangeNotifier.NotifyBoardChangedAsync(userId, cancellationToken);
        return created;
    }

    public Task<BoardMutationResult> RenameItemAsync(
        Guid userId,
        BoardSection section,
        Guid itemId,
        string title,
        DateTimeOffset? expectedUpdatedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        MutateItemAsync(
            userId,
            section,
            itemId,
            expectedUpdatedAtUtc,
            entity =>
            {
                entity.Title = ZalgoSanitizer.SanitizeAndTrim(title);
                return Task.FromResult(true);
            },
            entity => ToModelWithDailyStreaksAsync(userId, entity, cancellationToken),
            cancellationToken);

    public Task<BoardMutationResult> DeleteItemAsync(
        Guid userId,
        BoardSection section,
        Guid itemId,
        DateTimeOffset? expectedUpdatedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        MutateItemAsync(
            userId,
            section,
            itemId,
            expectedUpdatedAtUtc,
            entity =>
            {
                entity.DeletedAtUtc = entity.UpdatedAtUtc;
                return Task.FromResult(true);
            },
            null,
            cancellationToken);

    public Task<BoardMutationResult> ArchiveItemAsync(
        Guid userId,
        BoardSection section,
        Guid itemId,
        DateTimeOffset? expectedUpdatedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        MutateItemAsync(
            userId,
            section,
            itemId,
            expectedUpdatedAtUtc,
            entity =>
            {
                entity.IsArchived = true;
                return Task.FromResult(true);
            },
            entity => ToModelWithDailyStreaksAsync(userId, entity, cancellationToken),
            cancellationToken);

    public Task<BoardMutationResult> UnarchiveItemAsync(
        Guid userId,
        BoardSection section,
        Guid itemId,
        DateTimeOffset? expectedUpdatedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        MutateItemAsync(
            userId,
            section,
            itemId,
            expectedUpdatedAtUtc,
            entity =>
            {
                entity.IsArchived = false;
                return Task.FromResult(true);
            },
            entity => ToModelWithDailyStreaksAsync(userId, entity, cancellationToken),
            cancellationToken);

    public async Task<BoardSnapshot> GetArchivedSnapshotAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await readDb.BoardItems
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.DeletedAtUtc == null && x.IsArchived)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var today = await TodayAsync(userId, cancellationToken);
        return BuildSnapshot(items, today, EmptyDailyStreaks);
    }

    private static BoardSnapshot BuildSnapshot(
        IEnumerable<BoardItemEntity> items,
        DateOnly today,
        IReadOnlyDictionary<Guid, int> dailyStreaks)
    {
        var dailies = items.Where(x => x.Section == BoardSection.Daily).ToList();
        return new BoardSnapshot(
            [.. BoardOrdering.SortHabits(
                items.Where(x => x.Section == BoardSection.Habit),
                x => x.SortOrder,
                x => x.CreatedAtUtc,
                x => x.Id)
                .Select(x => ToModelWithToday(x, today, dailyStreaks))],
            [.. BoardOrdering.SortDailies(
                dailies,
                x => IsDailyEntityCompleteForToday(x, today),
                x => x.SortOrder,
                x => x.CreatedAtUtc,
                x => x.Id)
                .Select(x => ToModelWithToday(x, today, dailyStreaks))],
            [.. BoardOrdering.SortTodos(
                items.Where(x => x.Section == BoardSection.Todo),
                x => x.IsCompleted,
                x => x.DailyStartDate,
                x => x.SortOrder,
                x => x.CreatedAtUtc,
                x => x.Id)
                .Select(x => ToModelWithToday(x, today, dailyStreaks))]);
    }

    public async Task<BoardMutationResult> CompleteDailyForDateAsync(
        Guid userId,
        Guid itemId,
        DateOnly completedOn,
        DateTimeOffset? expectedUpdatedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        var today = await TodayAsync(userId, cancellationToken);
        if (completedOn >= today)
        {
            return new BoardMutationResult(BoardMutationStatus.NotFound, null);
        }

        var (entity, conflict) = await LoadAndCheckAsync(userId, BoardSection.Daily, itemId, expectedUpdatedAtUtc, cancellationToken);
        if (entity is null || conflict is not null)
        {
            return conflict ?? new BoardMutationResult(BoardMutationStatus.NotFound, null);
        }

        var model = ToModelForDailyCheck(entity, today);
        if (!DailySchedule.IsDueOnDate(model, completedOn))
        {
            return new BoardMutationResult(BoardMutationStatus.NotFound, null);
        }

        if (model.DailyLastCompletedOn == today)
        {
            return new BoardMutationResult(BoardMutationStatus.NotFound, null);
        }

        entity.DailyLastCompletedOn = completedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        entity.IsCompleted = false;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AddActivityEvent(userId, ActivityEventType.DailyComplete, itemId, null, entity.Title,
            DailyStreakCalculator.BackdatedDailyEventOccurredAt(completedOn));
        var streakMap = await ComputeDailyStreakMapAsync(userId, entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        var completed = ToModelWithDailyStreaksAsync(entity, streakMap, today);
        await boardChangeNotifier.NotifyBoardChangedAsync(userId, cancellationToken);
        return new BoardMutationResult(BoardMutationStatus.Ok, completed);
    }

    public async Task<BoardMutationResult> ToggleItemAsync(
        Guid userId,
        BoardSection section,
        Guid itemId,
        DateTimeOffset? expectedUpdatedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (section == BoardSection.Habit)
        {
            return new BoardMutationResult(BoardMutationStatus.NotFound, null);
        }

        IReadOnlyDictionary<Guid, int>? streakMap = null;
        DateOnly today = default;
        return await MutateItemAsync(
            userId,
            section,
            itemId,
            expectedUpdatedAtUtc,
            async entity =>
            {
                if (section == BoardSection.Daily)
                {
                    today = await TodayAsync(userId, cancellationToken);
                    ToggleDaily(entity, today, userId, itemId);
                    streakMap = await ComputeDailyStreakMapAsync(userId, entity, cancellationToken);
                }
                else
                {
                    ToggleTodo(entity, userId, itemId);
                }

                return true;
            },
            entity => streakMap is not null
                ? Task.FromResult(ToModelWithDailyStreaksAsync(entity, streakMap, today))
                : ToModelWithDailyStreaksAsync(userId, entity, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    ///     Computes the event-derived streak for a daily entity, including events added but not yet
    ///     saved, and applies it to <see cref="BoardItemEntity.Counter" /> so the board, edit dialog,
    ///     and statistics stay aligned after check/uncheck. This avoids Max(computed, counter) sticking on
    ///     an old manual value. The caller persists the counter in its own SaveChanges round-trip.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, int>> ComputeDailyStreakMapAsync(
        Guid userId,
        BoardItemEntity dailyEntity,
        CancellationToken cancellationToken)
    {
        var (today, dayStart) = await TodayAndDayStartAsync(userId, cancellationToken);
        var singleDailyList = new List<BoardItemEntity> { dailyEntity };
        var map = await streakCalculator.BuildDailyStreakMapAsync(userId, singleDailyList, today, dayStart, dbContext, cancellationToken);
        if (map.TryGetValue(dailyEntity.Id, out var streak))
        {
            dailyEntity.Counter = streak;
            dailyEntity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        return map;
    }

    public async Task LogTimerSessionAsync(
        Guid userId,
        TimeSpan duration,
        Guid? boardItemId,
        string? customLabel = null,
        CancellationToken cancellationToken = default)
    {
        var sec = DurationSeconds(duration);
        if (sec == 0)
        {
            return;
        }

        AddActivityEvent(userId, ActivityEventType.TimerSession, boardItemId, sec, customLabel);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static int DurationSeconds(TimeSpan duration) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, duration.TotalSeconds));

    public Task<BoardMutationResult> IncrementHabitPlusAsync(
        Guid userId,
        Guid itemId,
        DateTimeOffset? expectedUpdatedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        MutateItemAsync(
                userId,
                BoardSection.Habit,
                itemId,
                expectedUpdatedAtUtc,
                entity =>
                {
                    if (!entity.TrackPlus)
                    {
                        return Task.FromResult(false);
                    }

                    entity.Counter++;
                    AddActivityEvent(userId, ActivityEventType.HabitPlus, itemId, customLabel: entity.Title);
                    return Task.FromResult(true);
                },
                entity => ToModelWithDailyStreaksAsync(userId, entity, cancellationToken),
                cancellationToken);

    public Task<BoardMutationResult> IncrementHabitMinusAsync(
        Guid userId,
        Guid itemId,
        DateTimeOffset? expectedUpdatedAtUtc = null,
        CancellationToken cancellationToken = default) =>
        MutateItemAsync(
                userId,
                BoardSection.Habit,
                itemId,
                expectedUpdatedAtUtc,
                entity =>
                {
                    if (!entity.TrackMinus)
                    {
                        return Task.FromResult(false);
                    }

                    entity.NegativeCounter++;
                    AddActivityEvent(userId, ActivityEventType.HabitMinus, itemId, customLabel: entity.Title);
                    return Task.FromResult(true);
                },
                entity => ToModelWithDailyStreaksAsync(userId, entity, cancellationToken),
                cancellationToken);

    public Task<BoardMutationResult> UpdateHabitAsync(
        Guid userId,
        Guid itemId,
        UpdateHabitArgs args,
        CancellationToken cancellationToken = default) =>
        MutateItemAsync(
                userId,
                BoardSection.Habit,
                itemId,
                args.ExpectedUpdatedAtUtc,
                async entity =>
                {
                    var (trackPlus, trackMinus) = ResolveHabitTracks(args.TrackPlus, args.TrackMinus);

                    var newCounter = Math.Max(0, args.Counter);
                    var newNegativeCounter = Math.Max(0, args.NegativeCounter);
                    var title = args.Title ?? entity.Title;

                    await SyncHabitEventsAsync(userId, itemId, ActivityEventType.HabitPlus, entity.Counter, newCounter, title, cancellationToken);
                    await SyncHabitEventsAsync(userId, itemId, ActivityEventType.HabitMinus, entity.NegativeCounter, newNegativeCounter, title, cancellationToken);

                    await ApplyCommonEditsAsync(entity, new CommonItemEditFields(title, args.Notes, args.Tags, args.ChecklistJson, args.SortOrder),
                        userId, BoardSection.Habit, cancellationToken);
                    entity.TrackPlus = trackPlus;
                    entity.TrackMinus = trackMinus;
                    entity.ResetPeriod = (int)args.ResetPeriod;
                    entity.Counter = newCounter;
                    entity.NegativeCounter = newNegativeCounter;
                    return true;
                },
                entity => ToModelWithDailyStreaksAsync(userId, entity, cancellationToken),
                cancellationToken);

    private static (bool TrackPlus, bool TrackMinus) ResolveHabitTracks(bool trackPlus, bool trackMinus)
    {
        if (!trackPlus && !trackMinus)
        {
            return (true, true);
        }

        return (trackPlus, trackMinus);
    }

    private async Task SyncHabitEventsAsync(
        Guid userId,
        Guid itemId,
        ActivityEventType eventType,
        int currentCount,
        int newCount,
        string title,
        CancellationToken cancellationToken)
    {
        if (newCount < currentCount)
        {
            var removeCount = currentCount - newCount;
            var recentEvents = await dbContext.UserActivityEvents
                .Where(e => e.UserId == userId && e.BoardItemId == itemId && e.EventType == eventType)
                .OrderByDescending(e => e.OccurredAtUtc)
                .Take(removeCount)
                .ToListAsync(cancellationToken);
            dbContext.UserActivityEvents.RemoveRange(recentEvents);
        }
        else if (newCount > currentCount)
        {
            var addCount = newCount - currentCount;
            for (var i = 0; i < addCount; i++)
            {
                AddActivityEvent(userId, eventType, itemId, customLabel: title);
            }
        }
    }

    public Task<BoardMutationResult> UpdateTodoAsync(
        Guid userId,
        Guid itemId,
        UpdateTodoArgs args,
        CancellationToken cancellationToken = default) =>
        MutateItemAsync(
                userId,
                BoardSection.Todo,
                itemId,
                args.ExpectedUpdatedAtUtc,
                async entity =>
                {
                    var dueUtc = args.DueDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

                    await ApplyCommonEditsAsync(entity, new CommonItemEditFields(args.Title, args.Notes, args.Tags, args.ChecklistJson, args.SortOrder),
                        userId, BoardSection.Todo, cancellationToken);
                    entity.DailyStartDate = dueUtc;
                    entity.TodoRepeatIntervalDays = args.TodoRepeatIntervalDays is > 0
                        ? Math.Min(365, args.TodoRepeatIntervalDays.Value)
                        : null;

                    return true;
                },
                entity => ToModelWithDailyStreaksAsync(userId, entity, cancellationToken),
                cancellationToken);

    public async Task<BoardMutationResult> UpdateDailyAsync(
        Guid userId,
        Guid itemId,
        UpdateDailyArgs args,
        CancellationToken cancellationToken = default)
    {
        return await MutateItemAsync(
            userId,
            BoardSection.Daily,
            itemId,
            args.ExpectedUpdatedAtUtc,
            async entity =>
            {
                var today = await TodayAsync(userId, cancellationToken);
                var wasCompleteForToday = IsDailyEntityCompleteForToday(entity, today);

                var n = Math.Max(1, Math.Min(999, args.RepeatInterval));
                var startUtc = args.StartDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var streakClamped = Math.Max(0, Math.Min(9999, args.Counter));

                DateOnly? newStartD = startUtc is { } su ? DateOnly.FromDateTime(su) : null;

                await ApplyCommonEditsAsync(entity, new CommonItemEditFields(args.Title, args.Notes, args.Tags, args.ChecklistJson, args.SortOrder),
                    userId, BoardSection.Daily, cancellationToken);
                entity.DailyStartDate = startUtc;
                entity.DailyRepeatType = (int)args.Repeat;
                entity.DailyRepeatInterval = n;
                entity.Counter = streakClamped;

                // Always reconcile streak backfill, not only when Counter or schedule appear to change. Otherwise a save
                // with the same values, e.g. only the title changed, or a previously skipped run leaves no DailyComplete
                // rows, so statistics and the heatmap never match the daily streak.
                var streakNotAfter = today.AddDays(-1);
                await streakCalculator.ReconcileDailyStreakBackfillAsync(dbContext, userId, itemId,
                    new DailyBackfillArgs(newStartD, args.Repeat, n, streakClamped),
                    streakNotAfter, cancellationToken);
                DailyStreakCalculationService.ApplyManualStreakToEntity(entity, newStartD, args.Repeat, n, streakClamped, today, wasCompleteForToday);
                return true;
            },
            entity => ToModelWithDailyStreaksAsync(userId, entity, cancellationToken),
            cancellationToken);
    }

    private static readonly IReadOnlyDictionary<Guid, int> EmptyDailyStreaks =
        FrozenDictionary<Guid, int>.Empty;

    /// <summary>Maps entity to API model, querying the DB for daily streaks. Streak queries use
    /// <see cref="IDbContextFactory{ApplicationDbContext}"/> so they do not overlap the scoped
    /// context. Still await this before <see cref="IBoardChangeNotifier.NotifyBoardChangedAsync" /> for ordering.</summary>
    private async Task<BoardItem> ToModelWithDailyStreaksAsync(
        Guid userId,
        BoardItemEntity entity,
        CancellationToken cancellationToken = default)
    {
        if (entity.Section != BoardSection.Daily)
        {
            return ToModelWithToday(entity, DateOnly.MinValue, EmptyDailyStreaks);
        }

        var (today, dayStart) = await TodayAndDayStartAsync(userId, cancellationToken);
        await using var readDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var singleDailyList = new List<BoardItemEntity> { entity };
        var streaks = await streakCalculator.BuildDailyStreakMapAsync(userId, singleDailyList, today, dayStart, readDb, cancellationToken);
        return ToModelWithToday(entity, today, streaks);
    }

    private static BoardItem ToModelWithDailyStreaksAsync(
        BoardItemEntity entity,
        IReadOnlyDictionary<Guid, int> streaks,
        DateOnly today)
    {
        return ToModelWithToday(entity, today, streaks);
    }

    private static BoardItem ToModelForDailyCheck(BoardItemEntity entity, DateOnly today)
    {
        return ToModelWithToday(entity, today, EmptyDailyStreaks);
    }

    private static (DateOnly? start, DateOnly? todoDue) ResolveDates(BoardItemEntity entity)
    {
        if (entity.Section == BoardSection.Daily)
        {
            return (entity.DailyStartDate is { } d0 ? DateOnly.FromDateTime(d0) : null, null);
        }
        if (entity.Section == BoardSection.Todo)
        {
            return (null, entity.DailyStartDate is { } d1 ? DateOnly.FromDateTime(d1) : null);
        }
        return (null, null);
    }

    private static (DailyRepeatType repeat, int interval) ResolveSchedule(BoardItemEntity entity)
    {
        if (entity.Section != BoardSection.Daily)
        {
            return (DailyRepeatType.Daily, 1);
        }

        var repeat = Enum.IsDefined((DailyRepeatType)entity.DailyRepeatType)
            ? (DailyRepeatType)entity.DailyRepeatType
            : DailyRepeatType.Daily;
        var interval = entity.DailyRepeatInterval < 1 ? 1 : Math.Min(999, entity.DailyRepeatInterval);
        return (repeat, interval);
    }

    private static HabitResetPeriod ResolveResetPeriod(BoardItemEntity entity)
    {
        return Enum.IsDefined((HabitResetPeriod)entity.ResetPeriod)
            ? (HabitResetPeriod)entity.ResetPeriod
            : HabitResetPeriod.Daily;
    }

    private static BoardItem ToModelWithToday(
        BoardItemEntity entity,
        DateOnly today,
        IReadOnlyDictionary<Guid, int> dailyStreakById)
    {
        var (start, todoDue) = ResolveDates(entity);
        var (repeat, interval) = ResolveSchedule(entity);
        DateOnly? lastCompleted = entity.DailyLastCompletedOn is { } lc
            ? DateOnly.FromDateTime(lc)
            : null;
        var isCompleted = entity.Section == BoardSection.Daily
            ? IsDailyEntityCompleteForToday(entity, today)
            : entity.IsCompleted;

        int displayCounter;
        if (entity.Section == BoardSection.Daily)
        {
            displayCounter = dailyStreakById.TryGetValue(entity.Id, out var computedStreak)
                ? computedStreak
                : entity.Counter;
        }
        else
        {
            displayCounter = entity.Counter;
        }

        return new BoardItem(
            entity.Id,
            entity.Title,
            isCompleted,
            displayCounter,
            entity.Notes,
            entity.Tags,
            entity.TrackPlus,
            entity.TrackMinus,
            entity.NegativeCounter,
            ResolveResetPeriod(entity),
            start,
            repeat,
            interval,
            entity.ChecklistJson,
            lastCompleted,
            todoDue,
            entity.TodoRepeatIntervalDays,
            entity.UpdatedAtUtc,
            entity.CreatedAtUtc,
            entity.SortOrder,
            entity.IsArchived);
    }

    private static bool IsDailyEntityCompleteForToday(BoardItemEntity entity, DateOnly today)
    {
        var lastCompleted = entity.DailyLastCompletedOn is { } l ? DateOnly.FromDateTime(l) : (DateOnly?)null;
        return DailySchedule.IsCompletedForToday(lastCompleted, entity.IsCompleted, today);
    }

    private readonly record struct CommonItemEditFields(
        string Title,
        string? Notes,
        string? Tags,
        string? ChecklistJson,
        double? SortOrder);

    private async Task ApplyCommonEditsAsync(
        BoardItemEntity entity,
        CommonItemEditFields fields,
        Guid userId,
        BoardSection section,
        CancellationToken cancellationToken)
    {
        entity.Title = ZalgoSanitizer.SanitizeAndTrim(fields.Title);
        entity.Notes = string.IsNullOrWhiteSpace(fields.Notes) ? null : ZalgoSanitizer.SanitizeAndTrim(fields.Notes);
        entity.Tags = string.IsNullOrWhiteSpace(fields.Tags) ? null : ZalgoSanitizer.SanitizeAndTrim(fields.Tags);
        entity.ChecklistJson = DailyChecklistJson.Normalize(fields.ChecklistJson);

        await UpdateSortOrderIfNeededAsync(userId, section, entity, fields.SortOrder, cancellationToken);
    }

    private async Task UpdateSortOrderIfNeededAsync(
        Guid userId,
        BoardSection section,
        BoardItemEntity entity,
        double? sortOrder,
        CancellationToken cancellationToken)
    {
        if (!sortOrder.HasValue)
        {
            return;
        }

        entity.SortOrder = sortOrder.Value;
        var needsRebalance = await dbContext.BoardItems
            .AnyAsync(x => x.UserId == userId
                        && x.Section == section
                        && x.DeletedAtUtc == null
                        && x.Id != entity.Id
                        && Math.Abs(x.SortOrder - sortOrder.Value) < 1e-9,
                      cancellationToken);
        if (needsRebalance)
        {
            await RebalanceSortOrdersAsync(userId, section, cancellationToken);
        }
    }

    private void ToggleDaily(BoardItemEntity entity, DateOnly today, Guid userId, Guid itemId)
    {
        var wasCompleteForToday = IsDailyEntityCompleteForToday(entity, today);
        var lastCompleted = entity.DailyLastCompletedOn is { } l ? DateOnly.FromDateTime(l) : (DateOnly?)null;
        var (newLastCompleted, newIsCompleted) = DailySchedule.ToggleForToday(
            lastCompleted, entity.IsCompleted, today);
        entity.DailyLastCompletedOn = newLastCompleted?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        entity.IsCompleted = newIsCompleted;

        AddActivityEvent(userId,
            wasCompleteForToday ? ActivityEventType.DailyUncomplete : ActivityEventType.DailyComplete,
            itemId,
            customLabel: entity.Title);
    }

    private void ToggleTodo(BoardItemEntity entity, Guid userId, Guid itemId)
    {
        var wasCompleted = entity.IsCompleted;
        entity.IsCompleted = !entity.IsCompleted;
        AddActivityEvent(userId,
            wasCompleted ? ActivityEventType.TodoUncomplete : ActivityEventType.TodoComplete,
            itemId,
            customLabel: entity.Title);
    }

    private async Task<double> GetInitialSortOrderAsync(Guid userId, BoardSection section, CancellationToken cancellationToken)
    {
        var min = await dbContext.BoardItems
            .Where(x => x.UserId == userId && x.Section == section && x.DeletedAtUtc == null)
            .Select(x => (double?)x.SortOrder)
            .MinAsync(cancellationToken);
        return BoardItemReorder.SortOrderForNewItem(min);
    }

    private async Task RebalanceSortOrdersAsync(Guid userId, BoardSection section, CancellationToken cancellationToken)
    {
        var items = await dbContext.BoardItems
            .Where(x => x.UserId == userId && x.Section == section && x.DeletedAtUtc == null)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var seq = 1.0;
        var utcNow = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            item.SortOrder = seq;
            item.UpdatedAtUtc = utcNow;
            seq += 1.0;
        }
    }


    public async Task LogActivityAsync(
        Guid userId,
        ActivityEventType type,
        Guid? boardItemId,
        int? durationSeconds = null,
        string? itemTitleSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        AddActivityEvent(userId, type, boardItemId, durationSeconds, itemTitleSnapshot);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void AddActivityEvent(
        Guid userId,
        ActivityEventType type,
        Guid? boardItemId,
        int? durationSeconds = null,
        string? customLabel = null,
        DateTimeOffset? occurredAtUtc = null)
    {
        dbContext.UserActivityEvents.Add(new UserActivityEventEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OccurredAtUtc = occurredAtUtc ?? DateTimeOffset.UtcNow,
            EventType = type,
            BoardItemId = boardItemId,
            DurationSeconds = type == ActivityEventType.TimerSession ? durationSeconds : null,
            CustomLabel = customLabel
        });
    }
}
