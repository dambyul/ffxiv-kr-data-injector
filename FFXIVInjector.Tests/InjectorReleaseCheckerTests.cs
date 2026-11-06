using System.Net;
using FFXIVInjector.Core;

internal static class InjectorReleaseCheckerTests
{
    public static async Task Run()
    {
        foreach (string tag in new[] { "1.6.1", "v1.6.1", "Release-1.6.1", "Hotfix-1.6.1" })
            if (InjectorReleaseChecker.ParseVersion(tag) != new Version(1,6,1)) throw new Exception("Tag parsing failed");
        foreach (string tag in new[] { "garbage", "1.6.1-beta", "Release-1.6.1-extra" })
        {
            try { InjectorReleaseChecker.ParseVersion(tag); throw new Exception("Invalid tag accepted"); }
            catch (InvalidDataException) { }
        }
        using var client = new HttpClient(new Handler("""{"tag_name":"Release-1.10.0","draft":false,"prerelease":false}"""));
        var latest = await InjectorReleaseChecker.GetLatestAsync(client);
        if (latest <= new Version(1,6,1)) throw new Exception("Versions must compare numerically");
        using var prerelease = new HttpClient(new Handler("""{"tag_name":"Release-1.7.0","draft":false,"prerelease":true}"""));
        try { await InjectorReleaseChecker.GetLatestAsync(prerelease); throw new Exception("Prerelease accepted"); }
        catch (InvalidDataException) { }
        using var unavailable = new HttpClient(new Handler("{}", HttpStatusCode.Forbidden));
        try { await InjectorReleaseChecker.GetLatestAsync(unavailable); throw new Exception("API failure accepted"); }
        catch (HttpRequestException) { }
        Console.WriteLine("PASS: injector release tag parsing, numeric comparison, stable filtering and API failure.");
    }

    private sealed class Handler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != "/repos/dambyul/ffxiv-kr-data-injector/releases/latest" || request.Headers.UserAgent.Count == 0)
                throw new Exception("Unexpected GitHub request");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }
}
