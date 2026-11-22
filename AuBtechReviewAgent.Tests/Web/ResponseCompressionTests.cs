using AuBtechReviewAgent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #301: text data is sent compressed, Brotli first, while pages, Blazor's connection and the archive are not.
/// The decisions are asked of the framework's own compression provider, set up as the app sets it up.
/// </summary>
public class ResponseCompressionTests
{
    private static IResponseCompressionProvider Provider()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddTextCompression();
        return services.BuildServiceProvider().GetRequiredService<IResponseCompressionProvider>();
    }

    private static HttpContext Answer(string contentType, string acceptEncoding = "gzip, deflate, br")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Headers.AcceptEncoding = acceptEncoding;
        context.Response.ContentType = contentType;
        return context;
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/xml; charset=utf-8")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("text/markdown; charset=utf-8")]
    [InlineData("application/x-tex; charset=utf-8")]
    [InlineData("application/x-bibtex")]
    [InlineData("application/x-research-info-systems")]
    public void TextDataIsCompressedOverHttps(string contentType)
    {
        var provider = Provider();
        var context = Answer(contentType);

        Assert.True(provider.CheckRequestAcceptsCompression(context));
        Assert.True(provider.ShouldCompressResponse(context));
        Assert.Equal("br", provider.GetCompressionProvider(context)?.EncodingName);
    }

    [Theory]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("application/zip")]
    [InlineData("image/png")]
    public void PagesTheArchiveAndImagesAreNot(string contentType)
    {
        Assert.False(Provider().ShouldCompressResponse(Answer(contentType)));
    }

    [Fact]
    public void ABrowserWithoutBrotliGetsGzip()
    {
        Assert.Equal("gzip", Provider().GetCompressionProvider(Answer("application/json", "gzip, deflate"))?.EncodingName);
    }

    private sealed class CompressionFeature : IHttpsCompressionFeature
    {
        public HttpsCompressionMode Mode { get; set; } = HttpsCompressionMode.Default;
    }

    [Fact]
    public void AnAnswerWithASecretCanOptOut()
    {
        var context = new DefaultHttpContext();
        var feature = new CompressionFeature();
        context.Features.Set<IHttpsCompressionFeature>(feature);

        ResponseCompressionSetup.DoNotCompress(context);

        Assert.Equal(HttpsCompressionMode.DoNotCompress, feature.Mode);
        ResponseCompressionSetup.DoNotCompress(new DefaultHttpContext()); // no compression on this request: nothing to do
    }

    [Fact]
    public void ProgramServesStaticFilesPreCompressedAndCompressesTextBeforeTheEndpoints()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return; // not run from the repository (packaged tests)
        string program = File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Program.cs"));

        Assert.DoesNotContain("app.UseStaticFiles()", program);
        Assert.Contains("builder.Services.AddTextCompression();", program);
        Assert.True(program.IndexOf("app.UseTextCompression();", StringComparison.Ordinal) < program.IndexOf("app.MapStaticAssets();", StringComparison.Ordinal));
    }
}
