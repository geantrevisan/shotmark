using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace ShotMark.Core
{
    public sealed record EditorSnapshot(BitmapSource Image, string AnnotationsJson);

    /// <summary>Undo/redo stacks of whole-document snapshots (annotations are small, images are shared).</summary>
    public sealed class EditHistory
    {
        private const int MaxSteps = 100;
        private readonly List<EditorSnapshot> _undo = new List<EditorSnapshot>();
        private readonly List<EditorSnapshot> _redo = new List<EditorSnapshot>();

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        public void Push(EditorSnapshot snapshot)
        {
            _undo.Add(snapshot);
            if (_undo.Count > MaxSteps) _undo.RemoveAt(0);
            _redo.Clear();
        }

        public EditorSnapshot? Undo(EditorSnapshot current)
        {
            if (_undo.Count == 0) return null;
            var s = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(current);
            return s;
        }

        public EditorSnapshot? Redo(EditorSnapshot current)
        {
            if (_redo.Count == 0) return null;
            var s = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(current);
            return s;
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }
    }
}
