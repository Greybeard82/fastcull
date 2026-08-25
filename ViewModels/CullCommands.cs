using System;
using Fastcull.Models;
using Fastcull.Services;

namespace Fastcull.ViewModels
{
    /// <summary>
    /// A rating or flag change on one photo (PRD 1.9).
    ///
    /// **Carries the exact prior CullState, not a direction.** Undo restores the value the photo
    /// actually held, which is the only thing that works for the jump keys: undoing `Z` on a photo
    /// that was at four stars has to put back four stars, and "one step up the ladder" would put
    /// back Unflagged. It also makes undo correct for a photo whose rating was changed several
    /// times - each command knows its own pair.
    ///
    /// Deliberately does not capture the cursor. Where the cursor should go afterwards is the
    /// caller's decision (see MainViewModel.ApplyUndoResult), because it is a presentation
    /// question rather than part of what the action changed.
    /// </summary>
    internal sealed class RatingCommand : IUndoableCommand
    {
        private readonly FilmstripItemViewModel _item;
        private readonly CullState _before;
        private readonly CullState _after;
        private readonly Action<FilmstripItemViewModel, CullState> _apply;

        public RatingCommand(FilmstripItemViewModel item, CullState before, CullState after,
                             Action<FilmstripItemViewModel, CullState> apply)
        {
            _item = item;
            _before = before;
            _after = after;
            _apply = apply;
        }

        public FilmstripItemViewModel Item => _item;

        public string Description => $"the rating change on {_item.Photo.FileName}";

        public bool Execute()
        {
            _apply(_item, _after);
            return true;
        }

        public bool Undo()
        {
            _apply(_item, _before);
            return true;
        }
    }

    /// <summary>
    /// A Recycle Bin delete (PRD 2.1.2), made reversible by PRD 1.9.
    ///
    /// Undo restores the file from the Recycle Bin and puts the photo back at its original
    /// position in the sequence. It can genuinely fail - the bin may have been emptied - and says
    /// so by returning false rather than throwing; the stack then reports it and drops the command
    /// so the rest of the history stays usable.
    ///
    /// The restore is attempted BEFORE the sequence is touched. Re-inserting a photo whose file
    /// did not come back would put a row in the filmstrip pointing at nothing, which is the
    /// mirror of the failure PRD 2.1.2 already forbids in the other direction.
    /// </summary>
    internal sealed class DeleteCommand : IUndoableCommand
    {
        private readonly FilmstripItemViewModel _item;
        private readonly int _index;
        private readonly Func<FilmstripItemViewModel, int, bool> _remove;
        private readonly Action<FilmstripItemViewModel, int> _reinsert;

        public DeleteCommand(FilmstripItemViewModel item, int index,
                             Func<FilmstripItemViewModel, int, bool> remove,
                             Action<FilmstripItemViewModel, int> reinsert)
        {
            _item = item;
            _index = index;
            _remove = remove;
            _reinsert = reinsert;
        }

        public FilmstripItemViewModel Item => _item;

        /// <summary>Where it sat before it was deleted, which is where undo puts it back.</summary>
        public int Index => _index;

        public string Description => $"deleting {_item.Photo.FileName}";

        public bool Execute() => _remove(_item, _index);

        public bool Undo()
        {
            if (!RecycleBin.TryRestore(_item.Photo.FilePath)) return false;

            _reinsert(_item, _index);
            return true;
        }
    }
}
