using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class SqliteRuntimeTests
{
    [TestMethod]
    public void BundledNativeSqliteIncludesAggregateMemorySafetyFix()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var actual = (string)command.ExecuteScalar()!;
        Console.WriteLine("Loaded native SQLite: " + actual);
        // GHSA-2m69-gcr7-jv3q / CVE-2025-6965 affects native SQLite before
        // 3.50.2. Check the loaded binary so stale transitive/native assets fail.
        Assert.IsTrue(Version.TryParse(actual, out var version) && version >= new Version(3, 50, 2),
            "The bundled native SQLite must include the aggregate memory-safety fix; loaded " + actual + ".");
    }
}
