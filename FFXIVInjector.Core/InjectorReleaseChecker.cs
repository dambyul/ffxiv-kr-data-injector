using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFXIVInjector.Core;

public static class InjectorReleaseChecker
{
    public const string ReleasesUrl = "https://github.com/dambyul/ffxiv-kr-data-injector/releases";
    public static Version ParseVersion(string text)
    {
        var match = Regex.Match(text, @"^(?:(?:release|hotfix)[- ]|v)?(\d+\.\d+\.\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
            throw new InvalidDataException("Unrecognized injector release version");
        return version;
    }

    public static async Task<Version> GetLatestAsync(HttpClient client, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/dambyul/ffxiv-kr-data-injector/releases/latest");
        request.Headers.UserAgent.ParseAdd("FFXIVInjector-release-check");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Expected a published stable release");
        return ParseVersion(release.GetProperty("tag_name").GetString() ?? "");
    }
}
