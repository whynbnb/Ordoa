using System.IO.Compression;
using System.Text;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

public sealed class ZipEncodingTests
{
    [Fact]
    public void AutoDetect_DecodesLegacyGbkNames()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-gbk-{Guid.NewGuid():N}.zip");
        try
        {
            WriteStoredZip(path, "中文.txt", Encoding.GetEncoding(936).GetBytes("中文.txt"));
            var old = ZipEncodingHelper.Preferred;
            try
            {
                ZipEncodingHelper.Preferred = ZipLegacyEncoding.Auto;
                using var archive = ZipEncodingHelper.OpenRead(path);
                Assert.Equal("中文.txt", archive.Entries[0].FullName);
            }
            finally
            {
                ZipEncodingHelper.Preferred = old;
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ManualOverride_UsesRequestedEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-sjis-{Guid.NewGuid():N}.zip");
        try
        {
            WriteStoredZip(path, "日本語.txt", Encoding.GetEncoding(932).GetBytes("日本語.txt"));
            var old = ZipEncodingHelper.Preferred;
            try
            {
                ZipEncodingHelper.Preferred = ZipLegacyEncoding.ShiftJis;
                using var archive = ZipEncodingHelper.OpenRead(path);
                Assert.Equal("日本語.txt", archive.Entries[0].FullName);
            }
            finally
            {
                ZipEncodingHelper.Preferred = old;
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void WriteStoredZip(string path, string name, byte[] rawName)
    {
        var data = Encoding.ASCII.GetBytes("hello");
        var crc = Crc32(data);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        var localOffset = (uint)stream.Position;
        writer.Write(0x04034b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(crc);
        writer.Write((uint)data.Length);
        writer.Write((uint)data.Length);
        writer.Write((ushort)rawName.Length);
        writer.Write((ushort)0);
        writer.Write(rawName);
        writer.Write(data);

        var centralOffset = (uint)stream.Position;
        writer.Write(0x02014b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(crc);
        writer.Write((uint)data.Length);
        writer.Write((uint)data.Length);
        writer.Write((ushort)rawName.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(localOffset);
        writer.Write(rawName);

        var centralSize = (uint)stream.Position - centralOffset;
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(centralSize);
        writer.Write(centralOffset);
        writer.Write((ushort)0);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
