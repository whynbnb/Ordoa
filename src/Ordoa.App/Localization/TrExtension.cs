using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ordoa.Core.Localization;

namespace Ordoa.App.Localization;

/// <summary>XAML markup extension: <c>{loc:Tr 设置}</c>.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var index = TrProxy.Instance.Register(Key);
        return new Binding($"Values[{index}]")
        {
            Source = TrProxy.Instance,
            Mode = BindingMode.OneWay,
        };
    }
}

/// <summary>
/// Holds one slot per distinct localized string and refreshes every binding
/// when the UI language changes.
/// </summary>
public sealed class TrProxy : INotifyPropertyChanged
{
    private static readonly TrProxy Shared = new();

    private readonly object _sync = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _keys = new();
    private IReadOnlyList<string> _values = Array.Empty<string>();

    private TrProxy()
    {
        L10n.LanguageChanged += () => Dispatcher.UIThread.Post(Refresh);
    }

    public static TrProxy Instance => Shared;

    public IReadOnlyList<string> Values => _values;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal int Register(string key)
    {
        lock (_sync)
        {
            if (_index.TryGetValue(key, out var existing))
                return existing;

            var index = _keys.Count;
            _keys.Add(key);
            _index[key] = index;
            Rebuild();
            return index;
        }
    }

    private void Refresh()
    {
        lock (_sync)
            Rebuild();

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Values)));
    }

    private void Rebuild()
    {
        _values = _keys.Select(L10n.T).ToArray();
    }
}
