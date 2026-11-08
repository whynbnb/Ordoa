using Ordoa.Core.Localization;
using System.Globalization;

namespace Ordoa.Core.Util;

public static class Humanize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Size(long bytes)
    {
        if (bytes < 0)
            return "—";
        if (bytes == 0)
            return "0 B";

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value switch
        {
            >= 100 => "0",
            >= 10 => "0.0",
            _ => "0.0",
        };
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Count(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    public static string Date(DateTimeOffset? value, bool withTime = true)
    {
        if (value is null)
            return "—";

        var local = value.Value.ToLocalTime();
        var now = DateTimeOffset.Now;
        var culture = L10n.EffectiveLanguage switch
        {
            UiLanguage.English => CultureInfo.GetCultureInfo("en-US"),
            UiLanguage.Japanese => CultureInfo.GetCultureInfo("ja-JP"),
            UiLanguage.Korean => CultureInfo.GetCultureInfo("ko-KR"),
            UiLanguage.German => CultureInfo.GetCultureInfo("de-DE"),
            UiLanguage.French => CultureInfo.GetCultureInfo("fr-FR"),
            UiLanguage.Spanish => CultureInfo.GetCultureInfo("es-ES"),
            _ => CultureInfo.CurrentCulture,
        };
        var westernDate = L10n.EffectiveLanguage is UiLanguage.English or UiLanguage.German or
            UiLanguage.French or UiLanguage.Spanish;

        if (withTime)
        {
            if (local.Date == now.Date)
                return L10n.T("今天") + " " + local.ToString("HH:mm", culture);
            if (local.Date == now.Date.AddDays(-1))
                return L10n.T("昨天") + " " + local.ToString("HH:mm", culture);
            if (local.Year == now.Year)
                return local.ToString(westernDate ? "MMM d HH:mm" : "M月d日 HH:mm", culture);
            return local.ToString(westernDate ? "MMM d, yyyy" : "yyyy年M月d日", culture);
        }

        return local.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }

    public static string DateTimeFull(DateTimeOffset? value)
        => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "—";

    public static string Duration(TimeSpan value)
    {
        if (value == TimeSpan.MaxValue || value.TotalHours > 99)
            return "—";
        if (value.TotalHours >= 1)
            return L10n.Format("{0} 小时 {1} 分", (int)value.TotalHours, value.Minutes);
        if (value.TotalMinutes >= 1)
            return L10n.Format("{0} 分 {1} 秒", (int)value.TotalMinutes, value.Seconds);
        return L10n.Format("{0} 秒", Math.Max(1, (int)value.TotalSeconds));
    }

    public static string Speed(double bytesPerSecond)
        => bytesPerSecond <= 0 ? "—" : Size((long)bytesPerSecond) + "/s";

    public static string Permissions(UnixFileMode mode)
    {
        Span<char> chars = stackalloc char[9];
        var bits = (int)mode;
        ReadOnlySpan<int> masks =
        [
            0x100, 0x80, 0x40, // owner rwx
            0x20, 0x10, 0x08,  // group rwx
            0x04, 0x02, 0x01,  // other rwx
        ];
        ReadOnlySpan<char> letters = ['r', 'w', 'x', 'r', 'w', 'x', 'r', 'w', 'x'];

        for (var i = 0; i < 9; i++)
            chars[i] = (bits & masks[i]) != 0 ? letters[i] : '-';

        return new string(chars);
    }

    public static string OctalPermissions(UnixFileMode mode)
        => Convert.ToString((int)mode & 0xFFF, 8).PadLeft(3, '0');

    public static string RelativeTime(DateTimeOffset? value)
    {
        if (value is null)
            return "—";
        var delta = DateTimeOffset.Now - value.Value;
        if (delta.TotalSeconds < 60)
            return L10n.T("刚刚");
        if (delta.TotalMinutes < 60)
            return L10n.Format("{0} 分钟前", (int)delta.TotalMinutes);
        if (delta.TotalHours < 24)
            return L10n.Format("{0} 小时前", (int)delta.TotalHours);
        if (delta.TotalDays < 30)
            return L10n.Format("{0} 天前", (int)delta.TotalDays);
        return value.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }
}
