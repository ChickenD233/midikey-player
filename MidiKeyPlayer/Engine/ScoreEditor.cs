using MidiKeyPlayer.Midi;

namespace MidiKeyPlayer.Engine;

/// <summary>可编辑的旋律谱面：增删改 + 撤销重做。列表始终按起始时间排序。</summary>
public sealed class ScoreEditor
{
    private const int MaxUndo = 200;

    private readonly List<RawNote> _notes = new();
    private readonly List<List<RawNote>> _undo = new();
    private readonly List<List<RawNote>> _redo = new();

    public IReadOnlyList<RawNote> Notes => _notes;
    public int Count => _notes.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public double TotalSeconds => _notes.Count == 0 ? 0 : _notes.Max(n => n.End);

    /// <summary>整批换谱。这是新基线，清空撤销栈。</summary>
    public void Reset(IEnumerable<RawNote> notes)
    {
        _notes.Clear();
        _notes.AddRange(notes);
        Sort();
        _undo.Clear();
        _redo.Clear();
    }

    public void Clear() => Reset(Array.Empty<RawNote>());

    /// <summary>新增一个音，返回它在排序后的下标。</summary>
    public int Add(int pitch, double start, double end)
    {
        Snapshot();
        var n = Make(pitch, start, end);
        _notes.Add(n);
        Sort();
        return _notes.IndexOf(n);
    }

    /// <summary>改一个音，返回它在重新排序后的下标。</summary>
    public int Update(int index, int pitch, double start, double end)
    {
        // 越界下标原样返回会让调用方把无效值存成选中下标，统一返回 -1
        if (index < 0 || index >= _notes.Count) return -1;
        Snapshot();
        var n = Make(pitch, start, end);
        _notes[index] = n;
        Sort();
        return _notes.IndexOf(n);
    }

    /// <summary>
    /// 整批替换（卷帘一次手势提交的结果）。仍压一次快照，
    /// 所以「拖动一组音」在撤销栈里只算一步，而不是每个音一步。
    /// </summary>
    public void ReplaceAll(IEnumerable<RawNote> notes)
    {
        Snapshot();
        _notes.Clear();
        _notes.AddRange(notes);
        Sort();
    }

    public void DeleteAt(int index)
    {
        if (index < 0 || index >= _notes.Count) return;
        Snapshot();
        _notes.RemoveAt(index);
    }

    /// <summary>该时刻正在响的音符下标；没有返回 -1。</summary>
    public int IndexAt(double seconds)
    {
        for (int i = _notes.Count - 1; i >= 0; i--)
            if (_notes[i].Start <= seconds && seconds < _notes[i].End) return i;
        return -1;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(new List<RawNote>(_notes));
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(new List<RawNote>(_notes));
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    private void Restore(List<RawNote> snapshot)
    {
        _notes.Clear();
        _notes.AddRange(snapshot);
    }

    /// <summary>改动前留快照。RawNote 是 init-only，所以浅拷贝列表就够。</summary>
    private void Snapshot()
    {
        _undo.Add(new List<RawNote>(_notes));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Sort() => _notes.Sort((a, b) => a.Start.CompareTo(b.Start));

    private static RawNote Make(int pitch, double start, double end)
    {
        double s = Math.Max(0, start);
        double e = Math.Max(s + 0.01, end);
        return new RawNote { Pitch = Math.Clamp(pitch, 0, 127), Start = s, End = e };
    }
}
