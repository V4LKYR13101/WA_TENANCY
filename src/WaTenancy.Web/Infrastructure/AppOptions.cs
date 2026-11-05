namespace WaTenancy.Web.Infrastructure;

public sealed class AppOptions
{
    public string DataRoot { get; set; } = "../../data";
    public string WacliExecutable { get; set; } = "wacli";
    public string? SessionLinkRoot { get; set; }
    public string InternalBaseUrl { get; set; } = "http://127.0.0.1:5187";
    public string? PublicBaseUrl { get; set; }
    public int MaxConcurrentMediaDownloads { get; set; } = 4;
    public int MaxMediaAttempts { get; set; } = 8;
    public long MaxIncomingMediaBytes { get; set; } = 100 * 1024 * 1024;
    public long MaxTenantIncomingMediaBytes { get; set; } = 1024L * 1024 * 1024;
    public bool RequireHttps { get; set; }
    public bool EnableWorkers { get; set; } = true;
    public string[] DevelopmentWebhookUrls { get; set; } = [];
    public int MaxConcurrentSends { get; set; } = 4;
    public int MaxSendAttempts { get; set; } = 8;
    public int MaxConcurrentWebhooks { get; set; } = 4;
    public int MaxAccountsPerTenant { get; set; } = 20;
    public int MaxTotalWorkers { get; set; } = 100;
    public long MaxUploadBytes { get; set; } = 16 * 1024 * 1024;
    public long MaxTenantUploadBytes { get; set; } = 256 * 1024 * 1024;
    public int RetentionDays { get; set; } = 30;
    public int CommandTimeoutSeconds { get; set; } = 90;
    public int MaxMessagesPerAccount { get; set; } = 25000;
    public string MaxDatabaseSize { get; set; } = "500MB";
    public int SendSpacingMilliseconds { get; set; } = 1500;
}
public sealed class AppPaths
{
    public string Root { get; }
    public string Keys => Path.Combine(Root, "keys");
    public string Database => Path.Combine(Root, "application.db");
    public AppPaths(Microsoft.Extensions.Options.IOptions<AppOptions> options, IWebHostEnvironment env)
    {
        Root = Path.GetFullPath(options.Value.DataRoot, env.ContentRootPath);
        var webRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot")));
        if (Path.TrimEndingDirectorySeparator(Root).Equals(webRoot, StringComparison.OrdinalIgnoreCase) || Root.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DataRoot must be outside wwwroot.");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Keys);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public string Store(Guid tenantId, Guid accountId) => Path.Combine(Root, "sessions", tenantId.ToString("N"), accountId.ToString("N"));
    public string FilePath(Guid tenantId, Guid fileId) => Path.Combine(Root, "uploads", tenantId.ToString("N"), fileId.ToString("N") + ".bin");
    public string AttachmentPath(Guid tenantId, Guid id) => Path.Combine(Root, "incoming-media", tenantId.ToString("N"), id.ToString("N") + ".bin");
}
