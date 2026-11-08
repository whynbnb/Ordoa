using Ordoa.Core.FileSystems;
using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class ChecksumAlgorithmTests
{
    [Theory]
    [InlineData(ChecksumAlgorithm.Md5, "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData(ChecksumAlgorithm.Sha1, "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData(ChecksumAlgorithm.Sha256, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData(ChecksumAlgorithm.Sha512, "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f")]
    public async Task Compute_MatchesKnownDigest(ChecksumAlgorithm algorithm, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ordoa-checksum-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "abc");
            var actual = await ChecksumService.ComputeAsync(LocalFileSystem.Instance, path, algorithm);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData(32, ChecksumAlgorithm.Md5)]
    [InlineData(40, ChecksumAlgorithm.Sha1)]
    [InlineData(64, ChecksumAlgorithm.Sha256)]
    [InlineData(128, ChecksumAlgorithm.Sha512)]
    public void DetectAlgorithm_UsesDigestLength(int length, ChecksumAlgorithm expected)
        => Assert.Equal(expected, ChecksumService.DetectAlgorithm(new string('a', length)));
}
