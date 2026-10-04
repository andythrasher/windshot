namespace Winshot.Editing;

/// <summary>
/// Undo/redo as whole snapshots of the annotation list. Annotations are tiny and the bitmap
/// never changes, so snapshots are cheap and far simpler than per-edit commands.
/// </summary>
internal sealed class History
{
    private const int MaxStates = 200;

    private readonly List<Annotation[]> _states = new();
    private int _index;
    private object? _lastKey;

    public History(Document document) => _states.Add(Snapshot(document));

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index < _states.Count - 1;

    /// <summary>Records the document's current state as a new undo step.</summary>
    /// <param name="coalesceKey">
    /// Consecutive commits with an equal key merge into one step, so dragging the size
    /// slider across several values undoes in one go.
    /// </param>
    public void Commit(Document document, object? coalesceKey = null)
    {
        _states.RemoveRange(_index + 1, _states.Count - _index - 1);

        if (coalesceKey is not null && Equals(coalesceKey, _lastKey) && _index > 0)
        {
            _states[_index] = Snapshot(document);
        }
        else
        {
            _states.Add(Snapshot(document));
            _index++;
            if (_states.Count > MaxStates)
            {
                _states.RemoveAt(0);
                _index--;
            }
        }
        _lastKey = coalesceKey;
    }

    public bool Undo(Document document) => MoveTo(document, _index - 1);

    public bool Redo(Document document) => MoveTo(document, _index + 1);

    private bool MoveTo(Document document, int index)
    {
        if (index < 0 || index >= _states.Count)
            return false;

        _index = index;
        _lastKey = null;

        foreach (var annotation in document.Annotations.OfType<IDisposable>())
            annotation.Dispose();
        document.Annotations.Clear();
        // Clone again so the stored state stays untouched by later edits.
        document.Annotations.AddRange(_states[index].Select(a => a.Clone()));
        return true;
    }

    private static Annotation[] Snapshot(Document document) =>
        document.Annotations.Select(a => a.Clone()).ToArray();
}
