using CloudBay.Core.OneDrive;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudBay.Tests;

[TestClass]
public sealed class OneDriveSignInConfigurationTests
{
    [TestMethod]
    public void WhitespaceOverridesUseTheNormalPublicClientAuthority()
    {
        var configured = OneDriveSignInConfiguration.Resolve(" \t ", "\r\n ");
        var normal = OneDriveSignInConfiguration.Resolve();
        Assert.AreEqual(normal, configured);
        Assert.AreEqual("common", configured.Tenant);
        var request = new OneDriveAuthClient(configured.ClientId, configured.Tenant)
            .CreateAuthorizationRequest(new("http://localhost:5000/callback/"));
        StringAssert.Contains(request.AuthorizationUri.AbsolutePath, "/common/oauth2/v2.0/authorize");
    }

    [TestMethod]
    public void CustomApplicationWithoutTenantKeepsPersonalAndWorkAccountSignIn()
    {
        var configured = OneDriveSignInConfiguration.Resolve(" {AABCAD61-318B-400D-9804-E8C129173570} ");
        Assert.AreEqual("aabcad61-318b-400d-9804-e8c129173570", configured.ClientId);
        Assert.AreEqual("common", configured.Tenant);
    }

    [TestMethod]
    public void TenantOverrideCanUseTheBuiltInApplicationAndTakesPrecedenceOverTenantMode()
    {
        var configured = OneDriveSignInConfiguration.Resolve(tenantOverride: " example.onmicrosoft.com ", useYxrczTenant: true);
        Assert.AreEqual(OneDriveSignInConfiguration.Resolve().ClientId, configured.ClientId);
        Assert.AreEqual("example.onmicrosoft.com", configured.Tenant);
        var request = new OneDriveAuthClient(configured.ClientId, configured.Tenant)
            .CreateAuthorizationRequest(new("http://localhost:5000/callback/"));
        StringAssert.Contains(request.AuthorizationUri.AbsolutePath, "/example.onmicrosoft.com/oauth2/v2.0/authorize");
    }

    [DataTestMethod]
    [DataRow("not-an-application-id")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    public void InvalidApplicationOverrideCannotSilentlyFallBack(string clientId)
    {
        Assert.ThrowsException<ArgumentException>(() => OneDriveSignInConfiguration.Resolve(clientId));
    }

    [DataTestMethod]
    [DataRow("https://login.microsoftonline.com/common")]
    [DataRow("common/../organizations")]
    [DataRow("tenant name")]
    [DataRow("common?prompt=none")]
    public void InvalidAuthorityOverrideCannotSilentlyFallBack(string tenant)
    {
        Assert.ThrowsException<ArgumentException>(() => OneDriveSignInConfiguration.Resolve(tenantOverride: tenant));
    }
}
