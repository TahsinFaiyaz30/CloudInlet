using System.Security.Cryptography;
using System.Text;
using CloudBay.Core;
using CloudBay.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class VerifiedCloudCopyCleanupTests
{
    [TestMethod]
    public async Task RemoveLocalDeletesOnlyAvailableVerifiedCloudCopiesAndRetainsIndependentData()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudBayDisconnectCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entries = new Dictionary<string,SyncEntry>();
            async Task AddAsync(string name, string text)
            {
                var path = Path.Combine(root,name); await File.WriteAllTextAsync(path,text,new UTF8Encoding(false));
                var bytes = await File.ReadAllBytesAsync(path); var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                entries[name] = new(name,new("immutable-"+name,"prefix/"+name,bytes.Length,
                    Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),modified),bytes.Length,modified);
            }
            await AddAsync("verified.txt","verified cloud content");
            await AddAsync("changed.txt","original");
            await File.WriteAllTextAsync(Path.Combine(root,"changed.txt"),"modified",new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(Path.Combine(root,"changed.txt"),entries["changed.txt"].LocalWriteUtc.UtcDateTime);
            await AddAsync("dirty.txt","still matching bytes");
            await AddAsync("unavailable.txt","cloud unavailable");
            await AddAsync("pending.txt","pending native validation");
            entries["pending.txt"] = entries["pending.txt"] with { NativeMarkPending = true };
            await AddAsync("streams.txt","verified default content");
            await File.WriteAllTextAsync(Path.Combine(root,"streams.txt")+":Personal","independent alternate stream");
            await File.WriteAllTextAsync(Path.Combine(root,"user-only.txt"),"unsynced personal file");
            var outcome = await VerifiedCloudCopyCleanup.RemoveAsync(root,entries,path=>new(false,true,Path.GetFileName(path)=="dirty.txt"),
                (_,_,_)=>throw new AssertFailedException("Ordinary files must not enter the online-only removal path."),
                (file,_)=>file.Key.EndsWith("unavailable.txt")?Task.FromException(new IOException("Cloud receipt unavailable")):Task.CompletedTask);
            Assert.AreEqual(1L,outcome.RemovedFileCount);
            Assert.AreEqual(5L,outcome.RetainedFileCount);
            Assert.IsFalse(File.Exists(Path.Combine(root,"verified.txt")));
            foreach(var name in new[]{"changed.txt","dirty.txt","unavailable.txt","pending.txt","streams.txt","user-only.txt"})
                Assert.IsTrue(File.Exists(Path.Combine(root,name)),name+" must be preserved.");
            Assert.AreEqual("independent alternate stream",await File.ReadAllTextAsync(Path.Combine(root,"streams.txt")+":Personal"));
            Assert.IsTrue(Directory.Exists(root),"Never recursively remove the user's root.");
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }

    [TestMethod]
    public async Task OnlineOnlyCleanupUsesIdentityCallbackWithoutReadingOrDownloadingPayload()
    {
        var root=Path.Combine(Path.GetTempPath(),"CloudBayOnlineCleanup-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"online.txt"); await File.WriteAllTextAsync(path,"");
            var cloud=new CloudObject("saved-immutable-version","prefix/online.txt",9_000_000,new string('a',40),DateTimeOffset.UnixEpoch);
            var entry=new SyncEntry("online.txt",cloud,cloud.Size,cloud.ModifiedUtc);
            var calls=0;
            var result=await VerifiedCloudCopyCleanup.RemoveAsync(root,new Dictionary<string,SyncEntry>{{"online.txt",entry}},
                _=>new(true,false,false),(selected,expected,_)=>{Assert.AreEqual(path,selected);Assert.AreEqual(cloud,expected);calls++;return Task.FromResult(false);},
                (_,_)=>Task.CompletedTask);
            Assert.AreEqual(1,calls);
            Assert.AreEqual(0L,result.RemovedFileCount);
            Assert.AreEqual(1L,result.RetainedFileCount);
            Assert.AreEqual(0L,new FileInfo(path).Length,"Removal must never hydrate an online-only file.");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
