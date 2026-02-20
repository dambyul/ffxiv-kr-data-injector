namespace FFXIVInjector.Core;

public static class Config
{
    /// <summary>
    /// The base URL for downloading resources (CSV zip, Font zip, data.json).
    /// Defaults to the public CloudFront endpoint.
    /// </summary>
    public static string ResourceBaseUrl { get; set; } = Secrets.ResourceBaseUrl;
}
