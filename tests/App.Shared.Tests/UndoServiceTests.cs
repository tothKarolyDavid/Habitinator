using App.Shared.RCL.Models;
using App.Shared.RCL.Services;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using MudBlazor;

using NSubstitute;

namespace App.Shared.Tests;

public sealed class UndoServiceTests : IDisposable
{
    private readonly ISnackbar _snackbar = Substitute.For<ISnackbar>();
    private readonly INotificationSettingsService _settingsService = Substitute.For<INotificationSettingsService>();
    private readonly INotificationSettingsRules _notificationRules = Substitute.For<INotificationSettingsRules>();
    private readonly UndoService _undoService;

    public UndoServiceTests()
    {
        _settingsService.GetAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NotificationSettings()));
        _notificationRules.UndoVisibleStateDurationMs(Arg.Any<NotificationToastDuration>())
            .Returns(12_000);

        _undoService = new UndoService(_snackbar, _settingsService, _notificationRules, NullLogger<UndoService>.Instance);
    }

    public void Dispose()
    {
        _undoService.Dispose();
    }

    [Fact]
    public void CanUndo_should_be_false_initially()
    {
        _undoService.CanUndo.Should().BeFalse();
        _undoService.LastActionDescription.Should().BeNull();
    }

    [Fact]
    public void RegisterUndo_should_push_to_stack_and_notify()
    {
        var actionCalled = false;
        var stateChangedCalled = false;
        _undoService.OnStateChanged += (_, _) => stateChangedCalled = true;

        _undoService.RegisterUndo("Test Action", () =>
        {
            actionCalled = true;
            return Task.CompletedTask;
        });

        _undoService.CanUndo.Should().BeTrue();
        _undoService.LastActionDescription.Should().Be("Test Action");
        stateChangedCalled.Should().BeTrue();
        actionCalled.Should().BeFalse();
    }

    [Fact]
    public async Task UndoAsync_should_execute_action_and_pop_from_stack()
    {
        var actionCalled = false;
        var undoPerformedCalled = false;

        _undoService.RegisterUndo("Test Action", () =>
        {
            actionCalled = true;
            return Task.CompletedTask;
        });
        _undoService.OnUndoPerformed += (_, _) => undoPerformedCalled = true;

        await _undoService.UndoAsync();

        actionCalled.Should().BeTrue();
        undoPerformedCalled.Should().BeTrue();
        _undoService.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task BeginBatch_should_group_multiple_actions_into_one()
    {
        var action1Called = false;
        var action2Called = false;

        using (_undoService.BeginBatch("Batch Action"))
        {
            _undoService.RegisterUndo("Sub 1", () =>
            {
                action1Called = true;
                return Task.CompletedTask;
            });
            _undoService.RegisterUndo("Sub 2", () =>
            {
                action2Called = true;
                return Task.CompletedTask;
            });
        }

        _undoService.CanUndo.Should().BeTrue();
        _undoService.LastActionDescription.Should().Be("Batch Action");

        await _undoService.UndoAsync();

        action1Called.Should().BeTrue();
        action2Called.Should().BeTrue();
        _undoService.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task UndoAsync_specific_id_should_only_undo_that_action()
    {
        List<string> undone = [];

        var firstId = _undoService.RegisterUndo("First", () =>
        {
            undone.Add("first");
            return Task.CompletedTask;
        }, ["item:a:title"]);
        _undoService.RegisterUndo("Second", () =>
        {
            undone.Add("second");
            return Task.CompletedTask;
        }, ["item:b:title"]);
        _undoService.RegisterUndo("Third", () =>
        {
            undone.Add("third");
            return Task.CompletedTask;
        }, ["item:c:title"]);

        await _undoService.UndoAsync(firstId);

        undone.Should().Equal("first");
        _undoService.CanUndo.Should().BeTrue();
        _undoService.LastActionDescription.Should().Be("Third");
    }

    [Fact]
    public async Task UndoAsync_out_of_order_with_overlapping_keys_should_only_undo_target_action()
    {
        List<string> undone = [];

        var firstId = _undoService.RegisterUndo("First", () =>
        {
            undone.Add("first");
            return Task.CompletedTask;
        }, ["item:x:title"]);
        _undoService.RegisterUndo("Second", () =>
        {
            undone.Add("second");
            return Task.CompletedTask;
        }, ["item:x:title"]);
        _undoService.RegisterUndo("Third", () =>
        {
            undone.Add("third");
            return Task.CompletedTask;
        }, ["item:z:notes"]);

        await _undoService.UndoAsync(firstId);

        undone.Should().Equal("first");
        _undoService.CanUndo.Should().BeTrue();
        _undoService.LastActionDescription.Should().Be("Third");

        // Remaining actions should still be undoable in LIFO order
        await _undoService.UndoAsync();
        undone.Should().Equal("first", "third");

        await _undoService.UndoAsync();
        undone.Should().Equal("first", "third", "second");
        _undoService.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task UndoAsync_prefix_keys_should_only_undo_target_action()
    {
        List<string> undone = [];

        var firstId = _undoService.RegisterUndo("Create", () =>
        {
            undone.Add("create");
            return Task.CompletedTask;
        }, ["item:x"]);
        _undoService.RegisterUndo("Edit", () =>
        {
            undone.Add("edit");
            return Task.CompletedTask;
        }, ["item:x:title"]);

        await _undoService.UndoAsync(firstId);

        undone.Should().Equal("create");
        _undoService.CanUndo.Should().BeTrue();
        _undoService.LastActionDescription.Should().Be("Edit");
    }

    [Fact]
    public async Task UndoAsync_when_actions_have_no_keys_should_only_undo_target_action()
    {
        List<string> undone = [];

        var firstId = _undoService.RegisterUndo("First", () =>
        {
            undone.Add("first");
            return Task.CompletedTask;
        });
        _undoService.RegisterUndo("Second", () =>
        {
            undone.Add("second");
            return Task.CompletedTask;
        }, ["item:y:title"]);
        _undoService.RegisterUndo("Third", () =>
        {
            undone.Add("third");
            return Task.CompletedTask;
        });

        await _undoService.UndoAsync(firstId);

        undone.Should().Equal("first");
        _undoService.CanUndo.Should().BeTrue();
        _undoService.LastActionDescription.Should().Be("Third");
    }

    [Fact]
    public async Task UndoAsync_middle_action_should_only_undo_middle_and_preserve_stack()
    {
        List<string> undone = [];

        _undoService.RegisterUndo("First", () =>
        {
            undone.Add("first");
            return Task.CompletedTask;
        });
        var secondId = _undoService.RegisterUndo("Second", () =>
        {
            undone.Add("second");
            return Task.CompletedTask;
        });
        _undoService.RegisterUndo("Third", () =>
        {
            undone.Add("third");
            return Task.CompletedTask;
        });

        await _undoService.UndoAsync(secondId);

        undone.Should().Equal("second");
        _undoService.CanUndo.Should().BeTrue();
        _undoService.LastActionDescription.Should().Be("Third");

        // Next parameterless undo pops "Third"
        await _undoService.UndoAsync();
        undone.Should().Equal("second", "third");
        _undoService.CanUndo.Should().BeTrue();
        _undoService.LastActionDescription.Should().Be("First");

        // Next parameterless undo pops "First"
        await _undoService.UndoAsync();
        undone.Should().Equal("second", "third", "first");
        _undoService.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task UndoAsync_dismisses_only_target_toast_snackbar()
    {
        var firstId = _undoService.RegisterUndo("First", () => Task.CompletedTask);
        var secondId = _undoService.RegisterUndo("Second", () => Task.CompletedTask);

        await _undoService.UndoAsync(firstId);

        _snackbar.Received(1).RemoveByKey($"habitinator-undo-{firstId:N}");
        _snackbar.DidNotReceive().RemoveByKey($"habitinator-undo-{secondId:N}");
    }

    [Fact]
    public async Task UndoAsync_older_then_newer_action()
    {
        List<string> undone = [];

        var firstId = _undoService.RegisterUndo("First", () =>
        {
            undone.Add("first");
            return Task.CompletedTask;
        }, ["item:a:title"]);
        var secondId = _undoService.RegisterUndo("Second", () =>
        {
            undone.Add("second");
            return Task.CompletedTask;
        }, ["item:b:title"]);

        await _undoService.UndoAsync(firstId);
        await _undoService.UndoAsync(secondId);

        undone.Should().Equal("first", "second");
    }

    [Fact]
    public async Task UndoAsync_nonexistent_id_should_noop()
    {
        var actionCalled = false;
        _undoService.RegisterUndo("First", () =>
        {
            actionCalled = true;
            return Task.CompletedTask;
        });

        await _undoService.UndoAsync(Guid.NewGuid());

        actionCalled.Should().BeFalse();
        _undoService.CanUndo.Should().BeTrue();
    }

    [Fact]
    public async Task UndoAsync_out_of_order_sequence_preserves_independent_undo_states()
    {
        List<string> undone = [];

        _undoService.RegisterUndo("A", () =>
        {
            undone.Add("A");
            return Task.CompletedTask;
        });
        var idB = _undoService.RegisterUndo("B", () =>
        {
            undone.Add("B");
            return Task.CompletedTask;
        });
        _undoService.RegisterUndo("C", () =>
        {
            undone.Add("C");
            return Task.CompletedTask;
        });
        var idD = _undoService.RegisterUndo("D", () =>
        {
            undone.Add("D");
            return Task.CompletedTask;
        });

        // Undo B first (older than C and D)
        await _undoService.UndoAsync(idB);
        undone.Should().Equal("B");
        _undoService.LastActionDescription.Should().Be("D");

        // Undo D
        await _undoService.UndoAsync(idD);
        undone.Should().Equal("B", "D");
        _undoService.LastActionDescription.Should().Be("C");

        // Ctrl+Z (parameterless) should undo C, then A
        await _undoService.UndoAsync();
        undone.Should().Equal("B", "D", "C");
        _undoService.LastActionDescription.Should().Be("A");

        await _undoService.UndoAsync();
        undone.Should().Equal("B", "D", "C", "A");
        _undoService.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task UndoAsync_concurrent_calls_should_not_block()
    {
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();
        List<string> undone = [];

        _undoService.RegisterUndo("First", async () =>
        {
            await tcs1.Task;
            undone.Add("first");
        });
        _undoService.RegisterUndo("Second", async () =>
        {
            await tcs2.Task;
            undone.Add("second");
        });

        var task2 = _undoService.UndoAsync();
        var task1 = _undoService.UndoAsync();

        tcs2.SetResult();
        tcs1.SetResult();

        await Task.WhenAll(task1, task2);

        undone.Should().Equal("second", "first");
    }
}

public sealed class UndoableBoardDataServiceTests
{
    private readonly IBoardDataService _inner = Substitute.For<IBoardDataService>();
    private readonly IUndoService _undoService = Substitute.For<IUndoService>();
    private readonly UndoableBoardDataService _undoableService;

    public UndoableBoardDataServiceTests()
    {
        _undoableService = new UndoableBoardDataService(_inner, _undoService);
    }

    [Fact]
    public async Task CreateItemAsync_should_register_undo_with_delete()
    {
        var item = new BoardItem(Guid.NewGuid(), "New Task");
        _inner.CreateItemAsync(BoardSection.Todo, "New Task", Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(item));
        _undoService.IsUndoing.Returns(false);

        await _undoableService.CreateItemAsync(BoardSection.Todo, "New Task");

        _undoService.Received(1).RegisterUndo(
            Arg.Is("Add \"New Task\""),
            Arg.Any<Func<Task>>(),
            Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task RenameItemAsync_should_register_undo_with_old_name()
    {
        var item = new BoardItem(Guid.NewGuid(), "Old Task");
        _inner.GetItemAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(item));
        _inner.RenameItemAsync(BoardSection.Habit, item.Id, "New Task", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(new BoardItem(item.Id, "New Task")));
        _undoService.IsUndoing.Returns(false);

        await _undoableService.RenameItemAsync(BoardSection.Habit, item.Id, "New Task");

        _undoService.Received(1).RegisterUndo(
            Arg.Is("Rename \"Old Task\" to \"New Task\""),
            Arg.Any<Func<Task>>(),
            Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task CreateItemAsync_with_zalgo_title_should_register_undo_with_sanitized_title()
    {
        const string zalgoTitle = "k\u0300\u0301\u0302\u0303\u0304a\u0300\u0301\u0302\u0303\u0304r\u0300\u0301\u0302\u0303\u0304o\u0300\u0301\u0302\u0303\u0304l\u0300\u0301\u0302\u0303\u0304y\u0300\u0301\u0302\u0303\u0304";
        var item = new BoardItem(Guid.NewGuid(), "karoly");
        _inner.CreateItemAsync(BoardSection.Todo, zalgoTitle, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(item));
        _undoService.IsUndoing.Returns(false);

        await _undoableService.CreateItemAsync(BoardSection.Todo, zalgoTitle);

        _undoService.Received(1).RegisterUndo(
            Arg.Is("Add \"karoly\""),
            Arg.Any<Func<Task>>(),
            Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task RenameItemAsync_with_zalgo_title_should_register_undo_with_sanitized_title()
    {
        var item = new BoardItem(Guid.NewGuid(), "Old Task");
        _inner.GetItemAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(item));
        const string zalgoTitle = "k\u0300\u0301\u0302\u0303\u0304a\u0300\u0301\u0302\u0303\u0304r\u0300\u0301\u0302\u0303\u0304o\u0300\u0301\u0302\u0303\u0304l\u0300\u0301\u0302\u0303\u0304y\u0300\u0301\u0302\u0303\u0304";
        _inner.RenameItemAsync(BoardSection.Habit, item.Id, zalgoTitle, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(new BoardItem(item.Id, "karoly")));
        _undoService.IsUndoing.Returns(false);

        await _undoableService.RenameItemAsync(BoardSection.Habit, item.Id, zalgoTitle);

        _undoService.Received(1).RegisterUndo(
            Arg.Is("Rename \"Old Task\" to \"karoly\""),
            Arg.Any<Func<Task>>(),
            Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task DeleteItemAsync_should_register_undo_with_recreate()
    {
        var item = new BoardItem(Guid.NewGuid(), "Deleted Todo");
        _inner.GetItemAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(item));
        _inner.DeleteItemAsync(BoardSection.Todo, item.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _undoService.IsUndoing.Returns(false);

        await _undoableService.DeleteItemAsync(BoardSection.Todo, item.Id);

        _undoService.Received(1).RegisterUndo(
            Arg.Is("Delete \"Deleted Todo\""),
            Arg.Any<Func<Task>>(),
            Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task ToggleItemAsync_should_register_undo_with_toggle()
    {
        var item = new BoardItem(Guid.NewGuid(), "Task", IsCompleted: true);
        _inner.ToggleItemAsync(BoardSection.Todo, item.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(item));
        _undoService.IsUndoing.Returns(false);

        await _undoableService.ToggleItemAsync(BoardSection.Todo, item.Id);

        _undoService.Received(1).RegisterUndo(
            Arg.Is("Complete \"Task\""),
            Arg.Any<Func<Task>>(),
            Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task UpdateHabitAsync_sort_only_should_not_register_undo()
    {
        var item = new BoardItem(Guid.NewGuid(), "Habit", SortOrder: 1.0);
        _inner.GetItemAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(item));
        _inner.UpdateHabitAsync(
                item.Id,
                new UpdateHabitArgs(
                    item.Title, item.Notes, item.Tags,
                    item.TrackPlus, item.TrackMinus, item.ResetPeriod,
                    item.Counter, item.NegativeCounter, item.ChecklistJson, 2.0),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(item with { SortOrder = 2.0 }));
        _undoService.IsUndoing.Returns(false);

        await _undoableService.UpdateHabitAsync(
            item.Id,
            new UpdateHabitArgs(
                item.Title, item.Notes, item.Tags,
                item.TrackPlus, item.TrackMinus, item.ResetPeriod,
                item.Counter, item.NegativeCounter, item.ChecklistJson, SortOrder: 2.0));

        _undoService.DidNotReceive().RegisterUndo(Arg.Any<string>(), Arg.Any<Func<Task>>(), Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task UpdateHabitAsync_with_title_change_should_register_undo()
    {
        var item = new BoardItem(Guid.NewGuid(), "Habit", SortOrder: 1.0);
        _inner.GetItemAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(item));
        _inner.UpdateHabitAsync(
                Arg.Any<Guid>(), Arg.Any<UpdateHabitArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(item with { Title = "Renamed" }));
        _undoService.IsUndoing.Returns(false);

        await _undoableService.UpdateHabitAsync(
            item.Id,
            new UpdateHabitArgs(
                "Renamed", item.Notes, item.Tags,
                item.TrackPlus, item.TrackMinus, item.ResetPeriod,
                item.Counter, item.NegativeCounter, item.ChecklistJson, SortOrder: 2.0));

        _undoService.Received(1).RegisterUndo(
            Arg.Is("Edit \"Habit\""),
            Arg.Any<Func<Task>>(),
            Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Delete_then_Edit_Undo_sequence_should_preserve_original_guid()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        var originalItem = new BoardItem(itemId, "Original Name");
        var editedItem = new BoardItem(itemId, "Edited Name");

        // Mock item lookup to return our items when requested
        _inner.GetItemAsync(itemId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<BoardItem?>(originalItem), // For the rename call
                Task.FromResult<BoardItem?>(editedItem)    // For the delete call
            );

        // Track registered callbacks
        Func<Task>? renameUndoCallback = null;
        Func<Task>? deleteUndoCallback = null;

        _undoService.RegisterUndo(Arg.Any<string>(), Arg.Any<Func<Task>>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(x =>
            {
                var desc = (string)x[0]!;
                var callback = (Func<Task>)x[1]!;
                if (desc.StartsWith("Rename", StringComparison.Ordinal))
                {
                    renameUndoCallback = callback;
                }
                else if (desc.StartsWith("Delete", StringComparison.Ordinal))
                {
                    deleteUndoCallback = callback;
                }
                return Guid.NewGuid();
            });

        // 1. Rename item from "Original Name" to "Edited Name"
        _inner.RenameItemAsync(BoardSection.Habit, itemId, "Edited Name", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BoardItem?>(editedItem));

        await _undoableService.RenameItemAsync(BoardSection.Habit, itemId, "Edited Name");

        // 2. Delete item
        _inner.DeleteItemAsync(BoardSection.Habit, itemId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        _inner.CreateItemAsync(BoardSection.Habit, "Edited Name", itemId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(editedItem));

        await _undoableService.DeleteItemAsync(BoardSection.Habit, itemId);

        // Assert: We captured both callbacks
        renameUndoCallback.Should().NotBeNull();
        deleteUndoCallback.Should().NotBeNull();

        // 3. Simulate undoing delete first, which should recreate the item using its original Guid
        await deleteUndoCallback();

        // Verify that CreateItemAsync was called with the original itemId
        await _inner.Received(1).CreateItemAsync(
            BoardSection.Habit,
            "Edited Name",
            itemId,
            Arg.Any<CancellationToken>());

        // 4. Simulate undoing the edit, the rename, second. This should rename the restored item using the original Guid.
        await renameUndoCallback();

        // Verify that RenameItemAsync was called on the original itemId to restore the original name
        await _inner.Received(1).RenameItemAsync(
            BoardSection.Habit,
            itemId,
            "Original Name",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IncrementHabitPlusAsync_undo_should_perform_relative_decrement()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        var initialItem = new BoardItem(itemId, "Habit", Counter: 3);
        var itemAfterFirstIncrement = new BoardItem(itemId, "Habit", Counter: 4);
        var itemAfterSecondIncrement = new BoardItem(itemId, "Habit", Counter: 5);

        // Item lookup and Increment setup
        _inner.GetItemAsync(itemId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<BoardItem?>(initialItem),                 // First find before increment
                Task.FromResult<BoardItem?>(itemAfterFirstIncrement),     // Find during second increment
                Task.FromResult<BoardItem?>(itemAfterSecondIncrement),    // Find during first undo execution
                Task.FromResult<BoardItem?>(itemAfterFirstIncrement)      // Find during second undo execution
            );

        _inner.IncrementHabitPlusAsync(itemId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<BoardItem?>(itemAfterFirstIncrement),
                Task.FromResult<BoardItem?>(itemAfterSecondIncrement)
            );

        Func<Task>? firstUndoCallback = null;
        Func<Task>? secondUndoCallback = null;

        _undoService.RegisterUndo(Arg.Any<string>(), Arg.Any<Func<Task>>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(x =>
            {
                var callback = (Func<Task>)x[1]!;
                if (firstUndoCallback is null)
                {
                    firstUndoCallback = callback;
                }
                else
                {
                    secondUndoCallback ??= callback;
                }
                return Guid.NewGuid();
            });

        // 1. Perform first increment. Counter goes from 3 to 4
        await _undoableService.IncrementHabitPlusAsync(itemId);

        // 2. Perform second increment. Counter goes from 4 to 5
        await _undoableService.IncrementHabitPlusAsync(itemId);

        firstUndoCallback.Should().NotBeNull();
        secondUndoCallback.Should().NotBeNull();

        // 3. Simulate undoing the first increment first, out of order.
        // The current state is Counter = 5. Undoing first increment should decrement Counter to 4.
        await firstUndoCallback();

        await _inner.Received(1).UpdateHabitAsync(
            itemId,
            Arg.Is<UpdateHabitArgs>(args =>
                args != null
                && args.Title == "Habit"
                && args.Notes == null
                && args.Tags == null
                && args.TrackPlus
                && args.TrackMinus
                && args.ResetPeriod == HabitResetPeriod.Daily
                && args.Counter == 4
                && args.NegativeCounter == 0
                && args.ChecklistJson == null),
            Arg.Any<CancellationToken>());

        // 4. Simulate undoing the second increment.
        // The current state in this scenario is Counter = 4. Undoing second increment should decrement Counter to 3.
        await secondUndoCallback();

        await _inner.Received(1).UpdateHabitAsync(
            itemId,
            Arg.Is<UpdateHabitArgs>(args =>
                args != null
                && args.Title == "Habit"
                && args.Notes == null
                && args.Tags == null
                && args.TrackPlus
                && args.TrackMinus
                && args.ResetPeriod == HabitResetPeriod.Daily
                && args.Counter == 3
                && args.NegativeCounter == 0
                && args.ChecklistJson == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Checklist_flips_should_register_per_line_conflict_keys()
    {
        var itemId = Guid.NewGuid();
        var lineA = Guid.NewGuid();
        var lineB = Guid.NewGuid();

        var jsonBothUnchecked = DailyChecklistJson.Serialize(
        [
            new DailyChecklistItem(lineA, "A"),
            new DailyChecklistItem(lineB, "B"),
        ]);
        var jsonAChecked = DailyChecklistJson.Serialize(
        [
            new DailyChecklistItem(lineA, "A", IsDone: true),
            new DailyChecklistItem(lineB, "B"),
        ]);
        var jsonBothChecked = DailyChecklistJson.Serialize(
        [
            new DailyChecklistItem(lineA, "A", IsDone: true),
            new DailyChecklistItem(lineB, "B", IsDone: true),
        ]);

        var currentJson = jsonBothUnchecked;
        _inner.GetItemAsync(itemId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<BoardItem?>(new BoardItem(itemId, "Task", ChecklistJson: currentJson)));
        _inner.UpdateTodoAsync(itemId, Arg.Any<UpdateTodoArgs>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                currentJson = ((UpdateTodoArgs)x[1]!).ChecklistJson;
                return Task.FromResult<BoardItem?>(new BoardItem(itemId, "Task", ChecklistJson: currentJson));
            });
        _undoService.IsUndoing.Returns(false);

        IReadOnlyCollection<string>? firstKeys = null;
        IReadOnlyCollection<string>? secondKeys = null;
        _undoService.RegisterUndo(Arg.Any<string>(), Arg.Any<Func<Task>>(), Arg.Any<IReadOnlyCollection<string>>())
            .Returns(x =>
            {
                var keys = (IReadOnlyCollection<string>)x[2]!;
                if (firstKeys is null)
                {
                    firstKeys = keys;
                }
                else
                {
                    secondKeys ??= keys;
                }
                return Guid.NewGuid();
            });

        await _undoableService.UpdateTodoAsync(itemId, new UpdateTodoArgs("Task", null, null, jsonAChecked, null));
        await _undoableService.UpdateTodoAsync(itemId, new UpdateTodoArgs("Task", null, null, jsonBothChecked, null));

        firstKeys.Should().BeEquivalentTo($"item:{itemId:N}:checklist:{lineA:N}");
        secondKeys.Should().BeEquivalentTo($"item:{itemId:N}:checklist:{lineB:N}");
    }

    [Fact]
    public async Task Undo_older_subtask_check_should_only_uncheck_that_subtask()
    {
        var itemId = Guid.NewGuid();
        var lineA = Guid.NewGuid();
        var lineB = Guid.NewGuid();

        var jsonBothUnchecked = DailyChecklistJson.Serialize(
        [
            new DailyChecklistItem(lineA, "A"),
            new DailyChecklistItem(lineB, "B"),
        ]);
        var jsonAChecked = DailyChecklistJson.Serialize(
        [
            new DailyChecklistItem(lineA, "A", IsDone: true),
            new DailyChecklistItem(lineB, "B"),
        ]);
        var jsonBothChecked = DailyChecklistJson.Serialize(
        [
            new DailyChecklistItem(lineA, "A", IsDone: true),
            new DailyChecklistItem(lineB, "B", IsDone: true),
        ]);

        var currentJson = jsonBothUnchecked;
        _inner.GetItemAsync(itemId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<BoardItem?>(new BoardItem(itemId, "Task", ChecklistJson: currentJson)));
        _inner.UpdateTodoAsync(itemId, Arg.Any<UpdateTodoArgs>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                currentJson = ((UpdateTodoArgs)x[1]!).ChecklistJson;
                return Task.FromResult<BoardItem?>(new BoardItem(itemId, "Task", ChecklistJson: currentJson));
            });

        var snackbar = Substitute.For<ISnackbar>();
        var settingsService = Substitute.For<INotificationSettingsService>();
        var notificationRules = Substitute.For<INotificationSettingsRules>();
        settingsService.GetAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NotificationSettings()));
        notificationRules.UndoVisibleStateDurationMs(Arg.Any<NotificationToastDuration>())
            .Returns(12_000);

        var recording = new RecordingUndoService(new UndoService(snackbar, settingsService, notificationRules, NullLogger<UndoService>.Instance));
        var undoableService = new UndoableBoardDataService(_inner, recording);

        await undoableService.UpdateTodoAsync(itemId, new UpdateTodoArgs("Task", null, null, jsonAChecked, null));
        await undoableService.UpdateTodoAsync(itemId, new UpdateTodoArgs("Task", null, null, jsonBothChecked, null));

        recording.RegisteredIds.Should().HaveCount(2);

        await recording.UndoAsync(recording.RegisteredIds[0]);

        var lines = DailyChecklistJson.Parse(currentJson);
        lines.Single(x => x.Id == lineA).IsDone.Should().BeFalse();
        lines.Single(x => x.Id == lineB).IsDone.Should().BeTrue();

        await recording.UndoAsync(recording.RegisteredIds[1]);

        lines = DailyChecklistJson.Parse(currentJson);
        lines.Single(x => x.Id == lineA).IsDone.Should().BeFalse();
        lines.Single(x => x.Id == lineB).IsDone.Should().BeFalse();
    }

    private sealed class RecordingUndoService(UndoService inner) : IUndoService
    {
        public List<Guid> RegisteredIds { get; } = [];

        public bool CanUndo => inner.CanUndo;
        public bool IsUndoing => inner.IsUndoing;
        public string? LastActionDescription => inner.LastActionDescription;

        public event EventHandler? OnStateChanged
        {
            add => inner.OnStateChanged += value;
            remove => inner.OnStateChanged -= value;
        }

        public event EventHandler? OnUndoPerformed
        {
            add => inner.OnUndoPerformed += value;
            remove => inner.OnUndoPerformed -= value;
        }

        public Guid RegisterUndo(string description, Func<Task> undoFunc)
        {
            var id = inner.RegisterUndo(description, undoFunc);
            RegisteredIds.Add(id);
            return id;
        }

        public Guid RegisterUndo(string description, Func<Task> undoFunc, IReadOnlyCollection<string> conflictKeys)
        {
            var id = inner.RegisterUndo(description, undoFunc, conflictKeys);
            RegisteredIds.Add(id);
            return id;
        }

        public IDisposable BeginBatch(string description) => inner.BeginBatch(description);
        public Task UndoAsync() => inner.UndoAsync();
        public Task UndoAsync(Guid actionId) => inner.UndoAsync(actionId);
    }

    [Fact]
    public async Task IncrementHabitPlusAsync_concurrent_undos_should_decrement_relatively_to_correct_value()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        var counter = 3;
        var title = "Habit";

        _inner.GetItemAsync(itemId, Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                var item = new BoardItem(itemId, title, Counter: counter);
                return Task.FromResult<BoardItem?>(item);
            });

        _inner.IncrementHabitPlusAsync(itemId, Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                counter++;
                var item = new BoardItem(itemId, title, Counter: counter);
                return Task.FromResult<BoardItem?>(item);
            });

        _inner.UpdateHabitAsync(
            itemId, Arg.Any<UpdateHabitArgs>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                var args = (UpdateHabitArgs)x[1]!;
                counter = args.Counter;
                var item = new BoardItem(itemId, title, Counter: counter);
                return Task.FromResult<BoardItem?>(item);
            });

        var snackbar = Substitute.For<ISnackbar>();
        var settingsService = Substitute.For<INotificationSettingsService>();
        var notificationRules = Substitute.For<INotificationSettingsRules>();
        settingsService.GetAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NotificationSettings()));
        notificationRules.UndoVisibleStateDurationMs(Arg.Any<NotificationToastDuration>())
            .Returns(12_000);

        var realUndoService = new UndoService(snackbar, settingsService, notificationRules, NullLogger<UndoService>.Instance);
        var undoableService = new UndoableBoardDataService(_inner, realUndoService);

        // Act
        // 1. Increment twice: from 3 to 4, then from 4 to 5
        await undoableService.IncrementHabitPlusAsync(itemId);
        await undoableService.IncrementHabitPlusAsync(itemId);

        counter.Should().Be(5);
        realUndoService.CanUndo.Should().BeTrue();

        // 2. Trigger both undos concurrently
        var task1 = realUndoService.UndoAsync();
        var task2 = realUndoService.UndoAsync();

        await Task.WhenAll(task1, task2);

        // Assert
        counter.Should().Be(3);
    }
}
