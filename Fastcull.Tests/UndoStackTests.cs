using System;
using System.Collections.Generic;
using System.Linq;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// PRD 1.9's command stack. Tested with fake commands so the semantics - ordering, the redo
/// branch, the capacity bound, and what happens when a command refuses - are pinned independently
/// of what any real command does to a photograph.
/// </summary>
public class UndoStackTests
{
    /// <summary>Records what it was told to do, and can be made to refuse.</summary>
    private sealed class Fake : IUndoableCommand
    {
        private readonly List<string> _log;
        private readonly string _name;

        public Fake(List<string> log, string name) { _log = log; _name = name; }

        public bool FailUndo { get; init; }
        public bool FailExecute { get; init; }

        public string Description => _name;

        public bool Execute()
        {
            if (FailExecute) return false;
            _log.Add($"do:{_name}");
            return true;
        }

        public bool Undo()
        {
            if (FailUndo) return false;
            _log.Add($"undo:{_name}");
            return true;
        }
    }

    private static (UndoStack Stack, List<string> Log) Fresh(int capacity = UndoStack.DefaultCapacity)
        => (new UndoStack(capacity), new List<string>());

    // ---- Capacity ----

    [Fact]
    public void TheDefaultCapacityMeetsThePrdFloor()
    {
        Assert.True(UndoStack.DefaultCapacity >= 200, "PRD 1.9 asks for at least 200 entries");
        Assert.True(UndoStack.MinimumCapacity >= 200);
        Assert.Equal(UndoStack.DefaultCapacity, new UndoStack().Capacity);
    }

    [Fact]
    public void ACapacityBelowThePrdFloorIsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new UndoStack(199));

    [Fact]
    public void TwoHundredActionsAreAllUndoable()
    {
        var (stack, log) = Fresh();
        for (var i = 0; i < 200; i++) stack.Push(new Fake(log, $"c{i}"));

        Assert.Equal(200, stack.Count);

        for (var i = 0; i < 200; i++) Assert.Equal(UndoOutcome.Done, stack.Undo().Outcome);

        Assert.False(stack.CanUndo);
        Assert.Equal(200, log.Count);

        // Undone newest-first.
        Assert.Equal("undo:c199", log[0]);
        Assert.Equal("undo:c0", log[^1]);
    }

    [Fact]
    public void PastCapacityTheOldestIsDropped()
    {
        var (stack, log) = Fresh(200);
        for (var i = 0; i < 250; i++) stack.Push(new Fake(log, $"c{i}"));

        Assert.Equal(200, stack.Count);

        // The 200 most recent survive; c49 and earlier are gone.
        for (var i = 0; i < 200; i++) stack.Undo();
        Assert.Equal("undo:c50", log[^1]);
        Assert.False(stack.CanUndo);
    }

    // ---- Ordering and the redo branch ----

    [Fact]
    public void RedoReappliesInTheOriginalOrder()
    {
        var (stack, log) = Fresh();
        foreach (var n in new[] { "a", "b", "c" }) stack.Push(new Fake(log, n));

        stack.Undo(); stack.Undo();
        log.Clear();

        Assert.Equal(UndoOutcome.Done, stack.Redo().Outcome);
        Assert.Equal(UndoOutcome.Done, stack.Redo().Outcome);

        Assert.Equal(new[] { "do:b", "do:c" }, log);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void ANewActionDiscardsTheRedoBranch()
    {
        // The redone future no longer follows from the present, so it must not be reachable.
        var (stack, log) = Fresh();
        foreach (var n in new[] { "a", "b", "c" }) stack.Push(new Fake(log, n));

        stack.Undo(); stack.Undo();
        Assert.True(stack.CanRedo);

        stack.Push(new Fake(log, "d"));

        Assert.False(stack.CanRedo);
        Assert.Equal(2, stack.Count);      // a, d
    }

    [Fact]
    public void UndoAndRedoAtTheEndsDoNothing()
    {
        var (stack, log) = Fresh();

        Assert.Equal(UndoOutcome.NothingToDo, stack.Undo().Outcome);
        Assert.Equal(UndoOutcome.NothingToDo, stack.Redo().Outcome);

        stack.Push(new Fake(log, "a"));
        Assert.Equal(UndoOutcome.NothingToDo, stack.Redo().Outcome);

        stack.Undo();
        Assert.Equal(UndoOutcome.NothingToDo, stack.Undo().Outcome);
    }

    // ---- Failure ----

    [Fact]
    public void AFailedUndoIsReportedAndDoesNotBlockEarlierActions()
    {
        // The case that matters: a photo purged from the Recycle Bin cannot be restored. If that
        // command sat at the top refusing to move, undo would appear broken outright.
        var (stack, log) = Fresh();
        stack.Push(new Fake(log, "old"));
        stack.Push(new Fake(log, "stuck") { FailUndo = true });

        var failed = stack.Undo();
        Assert.Equal(UndoOutcome.Failed, failed.Outcome);
        Assert.Contains("stuck", failed.Message);
        Assert.Contains("earlier actions can still be undone", failed.Message);

        // And the one behind it still works.
        Assert.Equal(UndoOutcome.Done, stack.Undo().Outcome);
        Assert.Equal(new[] { "undo:old" }, log.Where(l => l.StartsWith("undo:")));
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void AFailedUndoLeavesNoBrokenEntryBehind()
    {
        var (stack, log) = Fresh();
        stack.Push(new Fake(log, "stuck") { FailUndo = true });

        stack.Undo();

        Assert.Equal(0, stack.Count);
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void AFailedRedoIsReportedAndDropped()
    {
        var (stack, log) = Fresh();
        stack.Push(new Fake(log, "a") { FailExecute = true });
        stack.Undo();

        var failed = stack.Redo();

        Assert.Equal(UndoOutcome.Failed, failed.Outcome);
        Assert.Equal(0, stack.Count);
    }

    // ---- Housekeeping ----

    [Fact]
    public void ClearEmptiesBothDirections()
    {
        var (stack, log) = Fresh();
        stack.Push(new Fake(log, "a"));
        stack.Push(new Fake(log, "b"));
        stack.Undo();

        stack.Clear();

        Assert.Equal(0, stack.Count);
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void ChangedFiresOnEveryMutation()
    {
        var (stack, log) = Fresh();
        var fired = 0;
        stack.Changed += () => fired++;

        stack.Push(new Fake(log, "a"));    // 1
        stack.Undo();                      // 2
        stack.Redo();                      // 3
        stack.Clear();                     // 4

        Assert.Equal(4, fired);
    }

    [Fact]
    public void PushingDoesNotExecuteTheCommand()
    {
        // Push records an action that has ALREADY happened; executing here would apply it twice.
        var (stack, log) = Fresh();
        stack.Push(new Fake(log, "a"));

        Assert.Empty(log);
    }
}
