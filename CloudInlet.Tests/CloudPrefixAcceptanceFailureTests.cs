using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudInlet.Core.B2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class CloudPrefixAcceptanceFailureTests
{
    [DataTestMethod]
    [DataRow("service_unavailable", "service_unavailable")]
    [DataRow("secret-provider-echo", "request_failed")]
    public async Task WrappedProviderFailureRecordsOnlySanitizedStatusAndCode(string code, string expected)
    {
        using var store = new B2CloudStore(new RejectedProvider(code));
        var provider = await Assert.ThrowsExceptionAsync<B2RequestException>(() => store.ConnectAsync(new("test-id", "test-key")));
        var diagnostic = CloudPrefixAcceptanceFailure.From(new IOException("secret-wrapper", provider));
        Assert.IsNotNull(diagnostic);
        Assert.AreEqual(400, diagnostic.Status);
        Assert.AreEqual(expected, diagnostic.Code);
        var json = JsonSerializer.Serialize(diagnostic);
        Assert.IsFalse(json.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("test-key", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OtherFailuresDoNotExposeTheirMessages()
    {
        Assert.IsNull(CloudPrefixAcceptanceFailure.From(new IOException("secret-wrapper", new Exception("secret-inner"))));
    }

    private sealed class RejectedProvider(string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = JsonContent.Create(new { code, message = "secret-provider-echo" }) });
    }
}
