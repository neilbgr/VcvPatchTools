namespace VcvPatchTools.Web;

/// <summary>
/// The <c>?url=</c> link: where to fetch a patch from, and the file name to show for it.
/// GitHub page links (<c>/blob/</c>, <c>/raw/</c>) are turned into raw.githubusercontent.com,
/// which serves the file itself and lets other sites fetch it (CORS).
/// </summary>
public static class PatchUrl
{
    public static bool TryResolve(string text, out Uri? uri, out string? fileName)
    {
        uri = null;
        fileName = null;
        // HTTPS only: the app is served over HTTPS, so the browser would block plain HTTP anyway.
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        uri = FromGitHubPage(parsed) ?? parsed;
        string last = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1]);
        fileName = last.Length > 0 ? last : "patch.vcv";
        return true;
    }

    /// <summary>github.com/owner/repo/(blob|raw)/branch/path → raw.githubusercontent.com/owner/repo/branch/path.</summary>
    private static Uri? FromGitHubPage(Uri uri)
    {
        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || segments.Length < 5 || segments[2] is not ("blob" or "raw"))
        {
            return null;
        }
        return new Uri($"https://raw.githubusercontent.com/{segments[0]}/{segments[1]}/{string.Join('/', segments[3..])}");
    }
}