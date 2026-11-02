using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Ordoa.Core.Util;

namespace Ordoa.Core.Localization;

public enum UiLanguage
{
    System,
    Chinese,
    English,
}

/// <summary>
/// Lightweight source-string localization. The Chinese source text is used as
/// the lookup key, so untranslated strings gracefully fall back to Chinese.
/// </summary>
public static class L10n
{
    private static readonly object Sync = new();
    private static Dictionary<string, string>? _english;
    private static UiLanguage _language = UiLanguage.System;

    public static event Action? LanguageChanged;

    public static UiLanguage Language => _language;

    public static UiLanguage EffectiveLanguage
    {
        get
        {
            if (_language != UiLanguage.System)
                return _language;

            var culture = CultureInfo.CurrentUICulture;
            var name = culture.Name;
            return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.Chinese
                : UiLanguage.English;
        }
    }

    public static bool IsEnglish => EffectiveLanguage == UiLanguage.English;

    public static void SetLanguage(UiLanguage language)
    {
        if (_language == language)
            return;

        lock (Sync)
            _language = language;

        LanguageChanged?.Invoke();
    }

    public static string T(string source)
    {
        if (string.IsNullOrEmpty(source) || !IsEnglish)
            return source;

        var map = EnsureEnglish();
        return map.TryGetValue(source, out var translated) ? translated : source;
    }

    public static string Format(string source, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, T(source), args);

    private static Dictionary<string, string> EnsureEnglish()
    {
        if (_english is not null)
            return _english;

        lock (Sync)
        {
            if (_english is not null)
                return _english;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var assembly = typeof(L10n).Assembly;
                using var stream = assembly.GetManifestResourceStream("Ordoa.Core.Localization.en.json");
                if (stream is not null)
                {
                    var loaded = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.DictionaryStringString);
                    if (loaded is not null)
                    {
                        foreach (var pair in loaded)
                            map[pair.Key] = pair.Value;
                    }
                }
            }
            catch
            {
                // Keep the empty map; source strings remain visible.
            }

            _english = map;
            return map;
        }
    }
}
