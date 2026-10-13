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
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/samples/AmbientJam.vcv.bin")]
    [InlineData("http://example.com/patch.vcv")]
    [InlineData("file:///C:/patch.vcv")]
    [InlineData("javascript:alert(1)")]
    public void RefusesAnythingButAnAbsoluteHttpsUrl(string text) =>
        Assert.False(PatchUrl.TryResolve(text, out _, out _));
}