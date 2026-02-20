namespace FFXIVInjector.Core;

// Example configuration file.
// Copy this file to 'Secrets.cs' and fill in the values below.
// 'Secrets.cs' is ignored by git to preventing sensitive data leaks.
public static class Secrets
{
    // The base URL for downloading resources (CloudFront/S3)
    public const string ResourceBaseUrl = "https://your-cloudfront-url.net/";

    // The official Discord invite link
    public const string DiscordUrl = "https://discord.gg/your-invite-code";
}
