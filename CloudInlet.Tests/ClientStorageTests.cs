using System.Text.Json;
using CloudInlet.Application;
using CloudInlet.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ClientStorageTests
{
    [TestMethod]
    public void LockedDiagnosticsDoNotPreventStartupOrInMemoryActivity()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "activity.jsonl");
            var original = JsonSerializer.Serialize(new ActivityEvent(DateTimeOffset.UtcNow, ActivityKind.Information, "", "Earlier activity"));
            File.WriteAllText(path, original);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var storage = new ClientStorage(directory);
                Assert.AreEqual(0, storage.Activity.Count);
                var next = new ActivityEvent(DateTimeOffset.UtcNow, ActivityKind.Information, "", "Current activity");
                Assert.IsTrue(storage.Log(next));
                Assert.AreEqual(next, storage.Activity.Single());
            }
            Assert.AreEqual(original, File.ReadAllText(path), "Unavailable diagnostic history must be preserved.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void InvalidDiagnosticRecordsDoNotHideValidNeighborsOrPreventStartup()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "activity.jsonl");
            var earlier = new ActivityEvent(DateTimeOffset.UtcNow.AddMinutes(-1), ActivityKind.Error, "report.txt", "Retry required");
            var later = new ActivityEvent(DateTimeOffset.UtcNow, ActivityKind.Upload, "report.txt", "Uploaded");
            var original = string.Join(Environment.NewLine, JsonSerializer.Serialize(earlier),
                "{\"Kind\":7,\"Path\":null,\"Message\":\"Invalid error path\"}",
                "{\"Kind\":6,\"Path\":\"\",\"Message\":null}",
                "{\"Kind\":999,\"Path\":\"\",\"Message\":\"Unknown kind\"}",
                "{interrupted", JsonSerializer.Serialize(later));
            File.WriteAllText(path, original);

            var storage = new ClientStorage(directory);

            CollectionAssert.AreEqual(new[] { later, earlier }, storage.Activity.ToArray());
            Assert.AreEqual(original, File.ReadAllText(path));
            Assert.IsTrue(storage.Log(earlier with { Time = DateTimeOffset.UtcNow }),
                "A later successful operation must clear suppression of a previous error.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CloudInlet.StorageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
