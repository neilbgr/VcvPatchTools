using VcvPatchTools.Core;

namespace VcvPatchTools.Tests;

public class ProductVersionTests
{
    [Fact]
    public void The_commit_is_shortened_to_eight_digits() => Assert.Equal("0.1.0 (d6a0afb1)", ProductVersion.Format("0.1.0+d6a0afb1f2e3d4c5b6a79881726354a4b3c2d1e0"));

    [Fact]
    public void A_version_without_commit_is_shown_as_is() => Assert.Equal("0.1.0-rc1", ProductVersion.Format("0.1.0-rc1"));

    [Fact]
    public void The_built_version_starts_with_the_project_version() => Assert.StartsWith(typeof(ProductVersion).Assembly.GetName().Version!.ToString(3), ProductVersion.Display);
}