using Avalonia.Input;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

public sealed record ShortcutDefinition(string Id, string Label, string DefaultGesture);

public sealed class ShortcutService
{
    private readonly SettingsService _settings;
    private Dictionary<string, Gesture> _bindings = new(StringComparer.Ordinal);

    public ShortcutService(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) => Reload();
        Reload();
    }

    public static IReadOnlyList<ShortcutDefinition> Definitions => CreateDefinitions();

    private static IReadOnlyList<ShortcutDefinition> CreateDefinitions() =>
    [
        new("newTab", L10n.T("新建标签页"), "Ctrl+T"),
        new("newWindow", L10n.T("新建窗口"), "Ctrl+N"),
        new("closeTab", L10n.T("关闭标签页"), "Ctrl+W"),
        new("closeSplit", L10n.T("关闭分屏"), "Ctrl+Shift+W"),
        new("reopenTab", L10n.T("恢复关闭的标签页"), "Ctrl+Shift+T"),
        new("commandPalette", L10n.T("命令面板"), "Ctrl+Shift+P"),
        new("quickPreview", L10n.T("快速预览"), "Space"),
        new("undo", L10n.T("撤销"), "Ctrl+Z"),
        new("redo", L10n.T("重做"), "Ctrl+Shift+Z"),
        new("openWith", L10n.T("打开方式"), "Ctrl+Shift+O"),
        new("batchRename", L10n.T("批量重命名"), "Ctrl+Shift+R"),
        new("editLocation", L10n.T("编辑路径"), "Ctrl+L"),
        new("search", L10n.T("搜索"), "Ctrl+F"),
        new("toggleHidden", L10n.T("显示/隐藏隐藏文件"), "Ctrl+H"),
        new("bookmark", L10n.T("添加书签"), "Ctrl+D"),
        new("settings", L10n.T("设置"), "Ctrl+,"),
        new("toggleSplit", L10n.T("切换分屏"), "Ctrl+\\"),
        new("listView", L10n.T("列表视图"), "Ctrl+1"),
        new("gridView", L10n.T("网格视图"), "Ctrl+2"),
        new("newFolder", L10n.T("新建文件夹"), "Ctrl+Shift+N"),
        new("about", L10n.T("键盘快捷键"), "F1"),
        new("rename", L10n.T("重命名"), "F2"),
        new("previewPanel", L10n.T("预览面板"), "F3"),
        new("refresh", L10n.T("刷新"), "F5"),
        new("toggleSidebar", L10n.T("显示/隐藏侧栏"), "F9"),
        new("delete", L10n.T("移到回收站"), "Delete"),
        new("permanentDelete", L10n.T("永久删除"), "Shift+Delete"),
        new("open", L10n.T("打开"), "Enter"),
        new("copy", L10n.T("复制"), "Ctrl+C"),
        new("cut", L10n.T("剪切"), "Ctrl+X"),
        new("paste", L10n.T("粘贴"), "Ctrl+V"),
        new("selectAll", L10n.T("全选"), "Ctrl+A"),
    ];

    public static string DefaultFor(string id)
        => Definitions.FirstOrDefault(item => item.Id == id)?.DefaultGesture ?? string.Empty;

    public string GetGesture(string id)
        => _settings.Current.ShortcutOverrides.TryGetValue(id, out var custom)
            ? custom
            : DefaultFor(id);

    public bool TryMatch(KeyEventArgs e, bool macOS, out string id)
    {
        foreach (var (actionId, gesture) in _bindings)
        {
            if (gesture.Matches(e.Key, e.KeyModifiers, macOS))
            {
                id = actionId;
                return true;
            }
        }

        id = string.Empty;
        return false;
    }

    public void Reload()
    {
        var bindings = new Dictionary<string, Gesture>(StringComparer.Ordinal);
        foreach (var definition in Definitions)
        {
            var text = GetGesture(definition.Id);
            if (TryParse(text, out var gesture))
                bindings[definition.Id] = gesture;
        }
        _bindings = bindings;
    }

    public static bool TryParse(string? text, out Gesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var keyToken = parts[^1];
        if (keyToken.Length == 0)
            return false;

        var modifiers = KeyModifiers.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            switch (parts[index].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                case "alt":
                case "option":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "meta":
                case "cmd":
                case "command":
                case "win":
                    modifiers |= KeyModifiers.Meta;
                    break;
                default:
                    return false;
            }
        }

        var keys = ParseKeys(keyToken);
        if (keys.Length == 0)
            return false;

        gesture = new Gesture(keys, modifiers);
        return true;
    }

    public static string Normalize(string? text)
        => TryParse(text, out var gesture) ? gesture.ToString() : (text ?? string.Empty).Trim();

    private static Key[] ParseKeys(string token)
    {
        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z')
                return [Enum.Parse<Key>(ch.ToString())];
            if (ch is >= '0' and <= '9')
                return [(Key)((int)Key.D0 + (ch - '0'))];
        }

        return token.ToLowerInvariant() switch
        {
            "," or "comma" => [Key.OemComma],
            "\\" or "backslash" => [Key.OemPipe, Key.OemBackslash],
            "/" or "slash" => [Key.OemQuestion],
            "." or "period" => [Key.OemPeriod],
            "-" or "minus" => [Key.OemMinus],
            "=" or "equals" => [Key.OemPlus],
            "[" or "leftbracket" => [Key.OemOpenBrackets],
            "]" or "rightbracket" => [Key.OemCloseBrackets],
            ";" or "semicolon" => [Key.OemSemicolon],
            "'" or "quote" => [Key.OemQuotes],
            "space" => [Key.Space],
            "delete" or "del" => [Key.Delete],
            "insert" or "ins" => [Key.Insert],
            "enter" or "return" => [Key.Enter],
            "tab" => [Key.Tab],
            "escape" or "esc" => [Key.Escape],
            "backspace" => [Key.Back],
            "home" => [Key.Home],
            "end" => [Key.End],
            "pageup" => [Key.PageUp],
            "pagedown" => [Key.PageDown],
            "up" => [Key.Up],
            "down" => [Key.Down],
            "left" => [Key.Left],
            "right" => [Key.Right],
            _ when token.StartsWith('F') &&
                   int.TryParse(token[1..], out var number) && number is >= 1 and <= 24
                => [(Key)((int)Key.F1 + number - 1)],
            _ => Enum.TryParse<Key>(token, ignoreCase: true, out var key) ? [key] : [],
        };
    }

    public readonly struct Gesture(Key[] keys, KeyModifiers modifiers)
    {
        private readonly Key[] _keys = keys;
        private readonly KeyModifiers _modifiers = modifiers;

        public bool Matches(Key key, KeyModifiers modifiers, bool macOS)
        {
            if (Array.IndexOf(_keys, key) < 0)
                return false;

            var expected = _modifiers;
            var actual = modifiers;
            if (macOS && expected.HasFlag(KeyModifiers.Control) && !expected.HasFlag(KeyModifiers.Meta))
            {
                if (actual.HasFlag(KeyModifiers.Meta) && !actual.HasFlag(KeyModifiers.Control))
                    actual = (actual & ~KeyModifiers.Meta) | KeyModifiers.Control;
            }

            return expected == actual;
        }

        public override string ToString()
        {
            var parts = new List<string>(5);
            if (_modifiers.HasFlag(KeyModifiers.Control))
                parts.Add("Ctrl");
            if (_modifiers.HasFlag(KeyModifiers.Shift))
                parts.Add("Shift");
            if (_modifiers.HasFlag(KeyModifiers.Alt))
                parts.Add("Alt");
            if (_modifiers.HasFlag(KeyModifiers.Meta))
                parts.Add("Meta");
            parts.Add(DisplayKey(_keys[0]));
            return string.Join('+', parts);
        }

        private static string DisplayKey(Key key) => key switch
        {
            Key.OemComma => "Comma",
            Key.OemPipe or Key.OemBackslash => "\\",
            Key.OemQuestion => "/",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.D0 or Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6 or Key.D7 or Key.D8 or Key.D9
                => ((char)('0' + ((int)key - (int)Key.D0))).ToString(),
            _ => key.ToString(),
        };
    }
}
