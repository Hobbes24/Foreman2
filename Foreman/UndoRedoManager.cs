using System.Collections.Generic;
namespace Foreman
{
    /// <summary>
    /// Snapshot-based undo/redo manager.
    /// Call PushUndoState(snapshot) BEFORE any mutating operation.
    /// Undo(current) and Redo(current) each return the snapshot to restore.
    /// </summary>
    internal class UndoRedoManager
    {
        private const int MaxHistory = 50;
        // Most-recent state at front; oldest at back.
        private readonly LinkedList<string> undoStack = new LinkedList<string>();
        private readonly Stack<string> redoStack = new Stack<string>();
        public bool CanUndo => undoStack.Count > 0;
        public bool CanRedo => redoStack.Count > 0;
        /// <summary>
        /// Pushes the current (pre-mutation) state and clears the redo stack.
        /// Call this BEFORE any graph mutation.
        /// </summary>
        public void PushUndoState(string snapshotJson)
        {
            undoStack.AddFirst(snapshotJson);
            while (undoStack.Count > MaxHistory)
                undoStack.RemoveLast();
            redoStack.Clear();
        }
        /// <summary>
        /// Undo: saves current state for redo, returns the state to restore.
        /// Returns null if nothing to undo.
        /// </summary>
        public string Undo(string currentStateJson)
        {
            if (!CanUndo) return null;
            redoStack.Push(currentStateJson);
            string stateToRestore = undoStack.First.Value;
            undoStack.RemoveFirst();
            return stateToRestore;
        }
        /// <summary>
        /// Redo: saves current state for undo, returns the state to restore.
        /// Returns null if nothing to redo.
        /// </summary>
        public string Redo(string currentStateJson)
        {
            if (!CanRedo) return null;
            undoStack.AddFirst(currentStateJson);
            while (undoStack.Count > MaxHistory)
                undoStack.RemoveLast();
            return redoStack.Pop();
        }
        /// <summary>Resets both stacks (call when loading a fresh graph).</summary>
        public void Clear()
        {
            undoStack.Clear();
            redoStack.Clear();
        }
    }
}
