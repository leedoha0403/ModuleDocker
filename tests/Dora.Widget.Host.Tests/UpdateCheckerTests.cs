using System.IO;
using System.Security.Cryptography;
using Dora.Widget.Host.Update;

namespace Dora.Widget.Host.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v0.2.0", "0.1.0+65f549a", true)]
    [InlineData("v0.1.0", "0.1.0+65f549a", false)]
    [InlineData("v0.1.0", "0.1.0-internal+abc", false)]
    [InlineData("v0.1.1", "0.1.0-internal", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("v0.1.0", "0.2.0", false)]
    public void Compares_versions_ignoring_suffixes(string tag, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(tag, current));

    [Fact]
    public void Unparseable_versions_are_never_newer()
    {
        Assert.False(UpdateChecker.IsNewer("latest", "0.1.0"));
        Assert.False(UpdateChecker.IsNewer("v0.2.0", "dev"));
    }

    [Fact]
    public void Checksum_is_verified_and_a_missing_entry_can_be_required()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "payload");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

            Assert.True(UpdateChecker.VerifyChecksum($"{hash}  ModuleDock.exe\n", "ModuleDock.exe", file, requireEntry: true));
            Assert.True(UpdateChecker.VerifyChecksum($"{hash.ToLowerInvariant()} *ModuleDock.exe", "moduledock.exe", file, requireEntry: true));
            Assert.False(UpdateChecker.VerifyChecksum($"{new string('0', 64)}  ModuleDock.exe", "ModuleDock.exe", file, requireEntry: true));
            Assert.False(UpdateChecker.VerifyChecksum($"{hash}  other.zip", "ModuleDock.exe", file, requireEntry: true));
            Assert.True(UpdateChecker.VerifyChecksum($"{hash}  other.zip", "ModuleDock.exe", file, requireEntry: false));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Release_assets_are_found_by_their_fixed_names()
    {
        var release = new UpdateRelease("v0.2.0", "https://example", new[]
        {
            new UpdateAsset("ModuleDock-v0.2.0-win-x64.zip", "u1", 1),
            new UpdateAsset("ModuleDock.exe", "u2", 2),
            new UpdateAsset("SHA256SUMS.txt", "u3", 3),
        });
        Assert.Equal("u2", release.FindExe(SelfUpdater.ExeAssetName)?.BrowserDownloadUrl);
        Assert.Equal("u1", release.FindZip()?.BrowserDownloadUrl);
        Assert.Equal("u3", release.FindChecksums()?.BrowserDownloadUrl);
    }
}
