using System.IO.Compression;
using System.Text;
using Ordoa.Core.Services;

namespace Ordoa.Core.Util;

/// <summary>
/// Opens ZIP files written by legacy tools that did not set the UTF-8 flag.
/// .NET decodes those names with the platform default and often produces U+FFFD;
/// this helper retries with UTF-8, GBK, Shift-JIS and CP437 and picks the most
/// plausible result. A global override is available in Settings.
/// </summary>
public static class ZipEncodingHelper
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    static ZipEncodingHelper()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static ZipLegacyEncoding Preferred { get; set; } = ZipLegacyEncoding.Auto;

    public static Encoding? Resolve(string archivePath)
    {
        if (Preferred != ZipLegacyEncoding.Auto)
            return ToEncoding(Preferred);

        if (!HasSuspiciousNames(archivePath))
            return null;

        var candidates = new List<(Encoding Encoding, string[] Names)>();
        AddCandidate(candidates, StrictUtf8, archivePath);
        AddCandidate(candidates, Encoding.GetEncoding(936), archivePath);
        AddCandidate(candidates, Encoding.GetEncoding(932), archivePath);
        AddCandidate(candidates, Encoding.GetEncoding(437), archivePath);

        if (candidates.Count == 0)
            return null;

        var best = candidates
            .OrderByDescending(candidate => Score(candidate.Names))
            .First();
        return best.Names.Any(name => name.Contains('\uFFFD')) ? null : best.Encoding;
    }

    public static ZipArchive OpenRead(string archivePath, FileShare share = FileShare.Read)
    {
        var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, share);
        try
        {
            var encoding = Resolve(archivePath);
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, encoding);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static ZipArchive OpenUpdate(string archivePath)
    {
        var encoding = Resolve(archivePath);
        var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false, encoding);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static string DisplayName(ZipLegacyEncoding encoding) => encoding switch
    {
        ZipLegacyEncoding.Utf8 => "UTF-8",
        ZipLegacyEncoding.Gbk => "GBK / GB18030",
        ZipLegacyEncoding.ShiftJis => "Shift-JIS",
        ZipLegacyEncoding.Cp437 => "CP437",
        _ => "Auto",
    };

    private static Encoding? ToEncoding(ZipLegacyEncoding encoding) => encoding switch
    {
        ZipLegacyEncoding.Utf8 => new UTF8Encoding(false, false),
        ZipLegacyEncoding.Gbk => Encoding.GetEncoding(936),
        ZipLegacyEncoding.ShiftJis => Encoding.GetEncoding(932),
        ZipLegacyEncoding.Cp437 => Encoding.GetEncoding(437),
        _ => null,
    };

    private static bool HasSuspiciousNames(string archivePath)
    {
        try
        {
            using var archive = new ZipArchive(new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.Read), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.Contains('\uFFFD') ||
                    name.Any(character => character is >= '\u2500' and <= '\u259F' ||
                                          character is >= '\uFF61' and <= '\uFF9F'))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Let the caller surface the real archive error.
        }

        return false;
    }

    private static void AddCandidate(List<(Encoding Encoding, string[] Names)> candidates,
        Encoding encoding, string archivePath)
    {
        try
        {
            using var archive = new ZipArchive(new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.Read), ZipArchiveMode.Read, leaveOpen: false, encoding);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            if (names.Any(name => name.Contains('\uFFFD')))
                return;
            candidates.Add((encoding, names));
        }
        catch
        {
            // The byte sequence is not valid for this encoding.
        }
    }

    private static double Score(IEnumerable<string> names)
    {
        var score = 0d;
        foreach (var name in names)
        {
            foreach (var character in name)
            {
                score += character switch
                {
                    >= '\u4E00' and <= '\u9FFF' => 2,
                    >= '\u3040' and <= '\u30FF' => 4,
                    >= '\uAC00' and <= '\uD7AF' => 3,
                    >= '\uFF61' and <= '\uFF9F' => -4,
                    >= '\u2500' and <= '\u259F' => -4,
                    '\uFFFD' => -100,
                    >= '\u00C0' and <= '\u00FF' => -1,
                    >= '\uE000' and <= '\uF8FF' => -5,
                    _ when char.IsLetterOrDigit(character) => 1,
                    _ when char.IsControl(character) => -20,
                    _ => 0,
                };
            }
        }
        return score;
    }
}
