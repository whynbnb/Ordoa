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
    Japanese,
    Korean,
    German,
    French,
    Spanish,
}

/// <summary>
/// Lightweight source-string localization. The Chinese source text is used as
/// the lookup key. Missing entries fall back to English, then to the Chinese
/// source string, so adding a language never leaves blank labels.
/// </summary>
public static class L10n
{
    private static readonly object Sync = new();
    private static readonly Dictionary<UiLanguage, Dictionary<string, string>> Maps = [];
    private static UiLanguage _language = UiLanguage.System;

    public static event Action? LanguageChanged;

    public static UiLanguage Language => _language;

    public static UiLanguage EffectiveLanguage
    {
        get
        {
            if (_language != UiLanguage.System)
                return _language;

            var name = CultureInfo.CurrentUICulture.Name;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return UiLanguage.Chinese;
            if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                return UiLanguage.Japanese;
            if (name.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
                return UiLanguage.Korean;
            if (name.StartsWith("de", StringComparison.OrdinalIgnoreCase))
                return UiLanguage.German;
            if (name.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
                return UiLanguage.French;
            if (name.StartsWith("es", StringComparison.OrdinalIgnoreCase))
                return UiLanguage.Spanish;
            return UiLanguage.English;
        }
    }

    public static bool IsEnglish => EffectiveLanguage == UiLanguage.English;

    public static bool IsChinese => EffectiveLanguage == UiLanguage.Chinese;

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
        var language = EffectiveLanguage;
        if (string.IsNullOrEmpty(source) || language == UiLanguage.Chinese)
            return source;

        var map = EnsureMap(language);
        if (map.TryGetValue(source, out var translated))
            return translated;

        if (language != UiLanguage.English &&
            EnsureMap(UiLanguage.English).TryGetValue(source, out translated))
        {
            return translated;
        }

        return source;
    }

    public static string Format(string source, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, T(source), args);

    private static Dictionary<string, string> EnsureMap(UiLanguage language)
    {
        lock (Sync)
        {
            if (Maps.TryGetValue(language, out var existing))
                return existing;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var assembly = typeof(L10n).Assembly;
                using var stream = assembly.GetManifestResourceStream(
                    "Ordoa.Core.Localization." + ResourceName(language) + ".json");
                if (stream is not null)
                {
                    var loaded = JsonSerializer.Deserialize(stream,
                        CoreJsonContext.Default.DictionaryStringString);
                    if (loaded is not null)
                    {
                        foreach (var pair in loaded)
                            map[pair.Key] = pair.Value;
                    }
                }
            }
            catch
            {
                // Keep the fallback chain usable.
            }

            Maps[language] = map;
            return map;
        }
    }

    private static string ResourceName(UiLanguage language) => language switch
    {
        UiLanguage.Chinese => "zh",
        UiLanguage.Japanese => "ja",
        UiLanguage.Korean => "ko",
        UiLanguage.German => "de",
        UiLanguage.French => "fr",
        UiLanguage.Spanish => "es",
        _ => "en",
    };
}
