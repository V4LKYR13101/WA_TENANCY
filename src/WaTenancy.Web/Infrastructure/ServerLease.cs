namespace WaTenancy.Web.Infrastructure;
public sealed class ServerLease : IDisposable
{
    private readonly FileStream lease;
    public ServerLease(AppPaths paths) => lease = new FileStream(Path.Combine(paths.Root, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public void Dispose() => lease.Dispose();
}
