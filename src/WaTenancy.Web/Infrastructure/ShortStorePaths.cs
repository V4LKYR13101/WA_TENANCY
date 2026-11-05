using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace WaTenancy.Web.Infrastructure;

// wacli's per-store .send.sock must fit within the OS Unix-domain socket path limit.
// Directory aliases preserve the original store, including its authentication keys.
public sealed class ShortStorePaths(IOptions<AppOptions> options,IWebHostEnvironment environment)
{
    private readonly object gate=new();
    public string ForCli(string store)
    {
        var canonical=Path.TrimEndingDirectorySeparator(Path.GetFullPath(store));
        if(Encoding.UTF8.GetByteCount(Path.Combine(canonical,".send.sock"))<=100)return canonical;
        var root=Path.GetFullPath(options.Value.SessionLinkRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"wt-sessions"));
        var webRoot=Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(environment.WebRootPath)?Path.Combine(environment.ContentRootPath,"wwwroot"):environment.WebRootPath));
        if(root.Equals(webRoot,StringComparison.OrdinalIgnoreCase)||root.StartsWith(webRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("SessionLinkRoot must be outside wwwroot.");
        var identity=OperatingSystem.IsWindows()?canonical.ToUpperInvariant():canonical;
        var alias=Path.Combine(root,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant());
        if(Encoding.UTF8.GetByteCount(Path.Combine(alias,".send.sock"))>100)throw new InvalidOperationException("Configure WaTenancy:SessionLinkRoot as a shorter private directory for wacli's delegate socket.");
        lock(gate)
        {
            Directory.CreateDirectory(canonical);Directory.CreateDirectory(root);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            if(!Path.Exists(alias))
            {
                if(OperatingSystem.IsWindows())CreateJunction(alias,canonical);
                else Directory.CreateSymbolicLink(alias,canonical);
            }
            var target=new DirectoryInfo(alias).ResolveLinkTarget(returnFinalTarget:true);
            if(target is null||!Path.TrimEndingDirectorySeparator(target.FullName).Equals(canonical,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidOperationException("The session alias points to an unexpected directory.");
        }
        return alias;
    }
    private static void CreateJunction(string alias,string target)
    {
        var executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("-NoProfile");info.ArgumentList.Add("-NonInteractive");info.ArgumentList.Add("-Command");
        // Values are passed through the environment; paths are never interpolated into shell code.
        info.ArgumentList.Add("New-Item -ItemType Junction -Path $env:WA_TENANCY_ALIAS -Value $env:WA_TENANCY_TARGET -ErrorAction Stop | Out-Null");
        info.Environment["WA_TENANCY_ALIAS"]=alias;info.Environment["WA_TENANCY_TARGET"]=target;
        using var process=Process.Start(info)??throw new InvalidOperationException("Could not create the short session path.");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(15000)){process.Kill(entireProcessTree:true);throw new InvalidOperationException("Creating the short session path timed out.");}
        Task.WhenAll(stdout,stderr).GetAwaiter().GetResult();
        if(process.ExitCode!=0)throw new InvalidOperationException("Could not create the session junction. Check permissions on SessionLinkRoot.");
    }
}
