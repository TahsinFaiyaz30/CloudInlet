using CloudBay.Core.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class PathRulesTests
{
    [DataTestMethod]
    [DataRow("../outside.txt")]
    [DataRow("folder/../../outside.txt")]
    [DataRow("C:/outside.txt")]
    [DataRow("folder/stream:secret")]
    [DataRow("CON.txt")]
    [DataRow("x/LPT1.log")]
    [DataRow("trailing.")]
    [DataRow("trailing ")]
    [DataRow("empty//name")]
    [DataRow("back\\slash.txt")]
    public void RejectsUnsafeCloudNames(string value) => Assert.ThrowsException<InvalidDataException>(() => PathRules.ValidateRelative(value));
    [TestMethod]
    public void ValidNamesAndExclusionsPreserveExpectedSemantics()
    {
        Assert.AreEqual("Photos/2026/photo.jpg", PathRules.FromKey("CloudBay/Photos/2026/photo.jpg", "CloudBay/"));
        Assert.IsTrue(PathRules.IsExcluded("project/node_modules/index.js", ["node_modules"]));
        Assert.IsTrue(PathRules.IsExcluded("Documents/~$draft.docx", ["~$*"]));
        Assert.IsFalse(PathRules.IsExcluded("Documents/final.docx", ["~$*"]));
        Assert.IsTrue(PathRules.IsExcluded(".cloudbay/transfers/partial", []));
    }
}
