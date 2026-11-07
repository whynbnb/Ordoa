using Ordoa.App.ViewModels;

namespace Ordoa.App.Services;

public sealed record TabDragPayload(MainViewModel Source, TabViewModel Tab);

/// <summary>Hands a dragged tab to another window without serializing its panes.</summary>
public sealed class TabDragService
{
    private const int MaxPending = 16;
    private readonly object _sync = new();
    private readonly Dictionary<string, TabDragPayload> _pending = new(StringComparer.Ordinal);

    public string Register(MainViewModel source, TabViewModel tab)
    {
        var token = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            if (_pending.Count >= MaxPending)
                _pending.Clear();
            _pending[token] = new TabDragPayload(source, tab);
        }
        return token;
    }

    public bool TryTake(string token, out TabDragPayload? payload)
    {
        lock (_sync)
            return _pending.Remove(token, out payload);
    }

    public void Cancel(string token)
    {
        lock (_sync)
            _pending.Remove(token);
    }
}
