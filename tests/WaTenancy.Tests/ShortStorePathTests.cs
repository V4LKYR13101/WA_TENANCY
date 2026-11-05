using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using WaTenancy.Web.Infrastructure;

namespace WaTenancy.Tests;

public sealed class ShortStorePathTests
{
    [Fact]
    public void LongStoresUseSeparateShortAliasesWithoutMovingFilesAndCanBindDelegateSocket()
    {
        var root=Path.Combine(Path.GetTempPath(),"wa-store-"+Guid.NewGuid().ToString("N"));
        var aliasRoot=Path.Combine(Path.GetTempPath(),"wa-link-"+Guid.NewGuid().ToString("N")[..8]);
        string? aAlias=null,bAlias=null;
        try
        {
            var paths=new ShortStorePaths(Options.Create(new AppOptions{SessionLinkRoot=aliasRoot}),new EnvironmentStub{ContentRootPath=root});
            var a=Path.Combine(root,new string('x',100),"tenant-a","number-a");var b=Path.Combine(root,new string('x',100),"tenant-b","number-b");
            Directory.CreateDirectory(a);Directory.CreateDirectory(b);File.WriteAllText(Path.Combine(a,"session-proof"),"number-a");File.WriteAllText(Path.Combine(b,"session-proof"),"number-b");
            aAlias=paths.ForCli(a);bAlias=paths.ForCli(b);
            Assert.NotEqual(aAlias,bAlias);Assert.Equal(aAlias,paths.ForCli(a));
            Assert.True(Encoding.UTF8.GetByteCount(Path.Combine(a,".send.sock"))>100);
            Assert.True(Encoding.UTF8.GetByteCount(Path.Combine(aAlias,".send.sock"))<=100);
            Assert.Equal("number-a",File.ReadAllText(Path.Combine(aAlias,"session-proof")));Assert.Equal("number-b",File.ReadAllText(Path.Combine(bAlias,"session-proof")));
            using(var socket=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified))
            {
                socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(aAlias,".send.sock")));socket.Listen(1);
                Assert.True(File.Exists(Path.Combine(a,".send.sock")));
            }
            File.WriteAllText(Path.Combine(aAlias,"new-proof"),"still the original store");Assert.True(File.Exists(Path.Combine(a,"new-proof")));
        }
        finally
        {
            // Remove each directory link itself before deleting the owned temporary trees.
            if(aAlias is not null&&Directory.Exists(aAlias))new DirectoryInfo(aAlias).Delete();
            if(bAlias is not null&&Directory.Exists(bAlias))new DirectoryInfo(bAlias).Delete();
            if(Directory.Exists(root))Directory.Delete(root,true);
            if(Directory.Exists(aliasRoot))Directory.Delete(aliasRoot,true);
        }
    }
    [Fact]
    public void AlreadyShortStorePathIsUnchanged()
    {
        var root=Path.Combine(Path.GetTempPath(),"wa-short-"+Guid.NewGuid().ToString("N")[..8]);
        var paths=new ShortStorePaths(Options.Create(new AppOptions()),new EnvironmentStub{ContentRootPath=root});
        Assert.Equal(root,paths.ForCli(root));Assert.False(Directory.Exists(root));
    }
    private sealed class EnvironmentStub:IWebHostEnvironment
    {
        public string EnvironmentName{get;set;}="Testing";
        public string ApplicationName{get;set;}="tests";
        public string ContentRootPath{get;set;}="";
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
        public string WebRootPath{get;set;}="";
        public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
}
