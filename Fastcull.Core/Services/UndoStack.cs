using System;
using System.Collections.Generic;

namespace Fastcull.Services
{
    /// <summary>
    /// One reversible action, per PRD 1.9's command stack.
    ///
    /// <see cref="Execute"/> is what redo calls, so a command must be re-appliable rather than
    /// only reversible - which is why commands carry both the value they set and the value they
    /// replaced, instead of a one-way delta.
    ///
    /// Both directions return a bool rather than throwing. Some of these touch the filesystem
    /// (a delete, a restore from the Recycle Bin) and can legitimately fail; a failure has to be
    /// reportable without unwinding the stack.
    /// </summary>
    public interface IUndoableCommand
    {
        /// <summary>Short, user-facing. Shown when an undo cannot be performed.</summary>
        string Description { get; }

        bool Execute();
        bool Undo();
    }

    public enum UndoOutcome
    {
        /// <summary>Nothing left in that direction.</summary>
        NothingToDo,
        Done,

        /// <summary>The command refused - the file is gone, the disk is locked. Reported, not thrown.</summary>
        Failed,
    }

    public readonly record struct UndoResult(UndoOutcome Outcome, IUndoableCommand? Command, string? Message)
    {
        public static readonly UndoResult Nothing = new(UndoOutcome.NothingToDo, null, null);
    }

    /// <summary>
    /// PRD 1.9's undo/redo stack.
    ///
    /// A list plus a cursor rather than two stacks: the redo branch is simply everything above the
    /// cursor, which makes "a new action discards the redo branch" a truncation instead of a
    /// second collection to keep in step.
    ///
    /// **Bounded, and old entries are dropped from the bottom.** PRD 1.9 asks for at least 200; the
    /// default is 256 because the cost is one object reference per entry and the workflow this
    /// protects is a fast one where the mistake being undone may be several dozen keypresses back.
    /// </summary>
    public sealed class UndoStack
    {
        /// <summary>PRD 1.9's floor is 200. Anything below it is not this feature.</summary>
        public const int MinimumCapacity = 200;

        public const int DefaultCapacity = 256;

        private readonly List<IUndoableCommand> _commands = new();

        /// <summary>How many commands are currently applied. Everything above this is redoable.</summary>
        private int _cursor;

        public UndoStack(int capacity = DefaultCapacity)
        {
            if (capacity < MinimumCapacity)
                throw new ArgumentOutOfRangeException(nameof(capacity),
                    $"PRD 1.9 requires at least {MinimumCapacity} entries.");

            Capacity = capacity;
        }

        public int Capacity { get; }

        /// <summary>Commands currently held, applied and redoable together.</summary>
        public int Count => _commands.Count;

        public bool CanUndo => _cursor > 0;
        public bool CanRedo => _cursor < _commands.Count;

        /// <summary>Raised whenever the stack changes, so a view can enable or disable its controls.</summary>
        public event Action? Changed;

        /// <summary>
        /// Records an action that has ALREADY been performed.
        ///
        /// Push-after-execute rather than execute-through-the-stack, because the actions this
        /// records are driven by keystrokes that must take effect in the same frame they are
        /// pressed (PRD 3.5). Routing them through the stack first would put an indirection in
        /// front of the app's most latency-sensitive path for no benefit.
        /// </summary>
        public void Push(IUndoableCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);

            // A new action makes the redo branch unreachable - it described a future that no
            // longer follows from the present.
            if (_cursor < _commands.Count)
                _commands.RemoveRange(_cursor, _commands.Count - _cursor);

            _commands.Add(command);
            _cursor = _commands.Count;

            while (_commands.Count > Capacity)
            {
                _commands.RemoveAt(0);
                _cursor--;
            }

            Changed?.Invoke();
        }

        public UndoResult Undo()
        {
            if (!CanUndo) return UndoResult.Nothing;

            var command = _commands[_cursor - 1];

            if (!command.Undo())
            {
                // Dropped rather than left in place. A command that cannot be undone - the photo
                // was purged from the Recycle Bin - would otherwise sit at the top of the stack
                // refusing to move and blocking every earlier action behind it, which reads as
                // "undo is broken" rather than "that one thing cannot be undone".
                _commands.RemoveAt(_cursor - 1);
                _cursor--;
                Changed?.Invoke();

                return new UndoResult(UndoOutcome.Failed, command,
                    $"Could not undo {command.Description}. It has been dropped from the undo history; earlier actions can still be undone.");
            }

            _cursor--;
            Changed?.Invoke();

            return new UndoResult(UndoOutcome.Done, command, null);
        }

        public UndoResult Redo()
        {
            if (!CanRedo) return UndoResult.Nothing;

            var command = _commands[_cursor];

            if (!command.Execute())
            {
                _commands.RemoveAt(_cursor);
                Changed?.Invoke();

                return new UndoResult(UndoOutcome.Failed, command,
                    $"Could not redo {command.Description}. It has been dropped from the history.");
            }

            _cursor++;
            Changed?.Invoke();

            return new UndoResult(UndoOutcome.Done, command, null);
        }

        /// <summary>
        /// Empties the history. Called when the folder changes: the commands close over items from
        /// the previous sequence, and undoing one after the sequence was replaced would write a
        /// rating onto a photo that is no longer on screen.
        /// </summary>
        public void Clear()
        {
            if (_commands.Count == 0 && _cursor == 0) return;

            _commands.Clear();
            _cursor = 0;
            Changed?.Invoke();
        }
    }
}
