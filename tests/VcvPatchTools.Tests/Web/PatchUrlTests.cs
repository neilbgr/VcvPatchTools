using VcvPatchTools.Web;

namespace VcvPatchTools.Tests;

public class PatchUrlTests
{
    [Theory]
    [InlineData("https://raw.githubusercontent.com/neilbgr/Aluminium/main/docs/Subsequent37-Duo.vcv",
        "https://raw.githubusercontent.com/neilbgr/Aluminium/main/docs/Subsequent37-Duo.vcv", "Subsequent37-Duo.vcv")]
    [InlineData("https://github.com/neilbgr/Aluminium/blob/main/docs/Subsequent37-Duo.vcv",
        "https://raw.githubusercontent.com/neilbgr/Aluminium/main/docs/Subsequent37-Duo.vcv", "Subsequent37-Duo.vcv")]
    [InlineData("https://github.com/neilbgr/Aluminium/raw/main/docs/My%20Patch.vcv",
        "https://raw.githubusercontent.com/neilbgr/Aluminium/main/docs/My%20Patch.vcv", "My Patch.vcv")]
    [InlineData("https://example.com/patches/get?id=3", "https://example.com/patches/get?id=3", "get")]
    [InlineData("https://example.com/", "https://example.com/", "patch.vcv")]
    public void ResolvesWhereToFetchAndWhatToCallIt(string text, string expectedUri, string expectedName)
    {
        Assert.True(PatchUrl.TryResolve(text, out Uri? uri, out string? fileName));

        Assert.Equal(expectedUri, uri!.AbsoluteUri);
        Assert.Equal(expectedName, fileName);
    }

    [Theory]
    [InlineData("Solar42f.vcv", "Solar42f.vcv")]
    [InlineData("Meditations on Death", "Meditations on Death")]
    [InlineData("", "download")]
    [InlineData("   ", "download")]
    [InlineData(null, "download")]
    public void AGivenNameReplacesTheOneFromTheUrl(string? name, string expectedName)
    {
        Assert.True(PatchUrl.TryResolve("https://patchstorage.com/api/beta/patches/197171/files/197170/download/", out Uri? uri, out string? fileName, name));

        Assert.Equal("https://patchstorage.com/api/beta/patches/197171/files/197170/download/", uri!.AbsoluteUri);
        Assert.Equal(expectedName, fileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/samples/AmbientJam.vcv.bin")]
    [InlineData("http://example.com/patch.vcv")]
    [InlineData("file:///C:/patch.vcv")]
    [InlineData("javascript:alert(1)")]
    public void RefusesAnythingButAnAbsoluteHttpsUrl(string text) =>
        Assert.False(PatchUrl.TryResolve(text, out _, out _));
}