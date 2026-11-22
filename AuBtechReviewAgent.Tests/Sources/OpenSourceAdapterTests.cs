using System.Net;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The open-source adapters (OpenAlex, Semantic Scholar, Crossref, Unpaywall), PDF size limits and rate limits.
/// The adapters are tested on stored responses in the documented API formats; they have not been run
/// against the live APIs from the test environment.
/// </summary>
public class OpenSourceAdapterTests
{
    [Fact]
    public void OpenAlexWorksAreParsedWithAbstractDoiAndOpenAccessPdf()
    {
        const string json = """
        {"meta":{"count":1},"results":[{
          "id":"https://openalex.org/W4390000001",
          "doi":"https://doi.org/10.3390/fi16010001",
          "display_name":"Agentic loops in practice",
          "publication_year":2024,
          "authorships":[{"author":{"display_name":"Jane Doe"}},{"author":{"display_name":"Kim Lee"}}],
          "primary_location":{"source":{"display_name":"Future Internet","type":"journal"}},
          "best_oa_location":{"pdf_url":"https://www.mdpi.com/fi16010001.pdf"},
          "abstract_inverted_index":{"loops":[1],"Agent":[0],"recover.":[2]}
        },{
          "id":"https://openalex.org/W4390000002","doi":null,"display_name":"A preprint",
          "publication_year":2025,"primary_location":{"source":{"display_name":"arXiv (Cornell University)","type":"repository"}}
        }]}
        """;
        var papers = OpenAlexSource.ParseWorks(json);

        Assert.Equal(2, papers.Count);
        var p = papers[0];
        Assert.Equal("OPENALEX_W4390000001", p.Id);
        Assert.Equal("Agent loops recover.", p.Abstract);
        Assert.Equal("10.3390/fi16010001", p.Doi);
        Assert.Equal("Future Internet", p.JournalSource);
        Assert.Equal("https://www.mdpi.com/fi16010001.pdf", p.PdfUrl);
        Assert.Equal(new[] { "Jane Doe", "Kim Lee" }, p.Authors);
        Assert.Equal("", papers[1].JournalSource); // repository -> treated as a preprint
        Assert.Equal(("OpenAlex", "OpenAlex API"), PrismaReviewEngine.SourceOfPaper(p.Id));
    }

    [Fact]
    public void SemanticScholarPapersGetAnArxivDoiWhenTheyHaveNoOtherDoi()
    {
        const string json = """
        {"total":2,"data":[
          {"paperId":"abc123","title":"Agent governance","abstract":"We audit agents.","year":2025,"venue":"",
           "authors":[{"name":"Ola Nordmann"}],"externalIds":{"ArXiv":"2501.01234"},"url":"https://www.semanticscholar.org/paper/abc123",
           "openAccessPdf":{"url":"https://arxiv.org/pdf/2501.01234"}},
          {"paperId":"def456","title":"Agent memory","abstract":null,"year":null,"venue":"ICSE",
           "authors":[],"externalIds":{"DOI":"10.1145/1234567"}}
        ]}
        """;
        var papers = SemanticScholarSource.Parse(json);

        Assert.Equal(2, papers.Count);
        Assert.Equal("S2_abc123", papers[0].Id);
        Assert.Equal("10.48550/arXiv.2501.01234", papers[0].Doi);
        Assert.Equal("https://arxiv.org/pdf/2501.01234", papers[0].PdfUrl);
        Assert.Equal("10.1145/1234567", papers[1].Doi);
        Assert.Equal("", papers[1].Abstract);
    }

    [Fact]
    public void CrossrefItemsAreParsedAndJatsAbstractsAreCleaned()
    {
        const string json = """
        {"status":"ok","message":{"items":[{
          "DOI":"10.1016/J.EPSR.2025.109876","title":["Grid agents"],"type":"journal-article",
          "author":[{"given":"Jane","family":"Doe"}],"container-title":["Electric Power Systems Research"],
          "issued":{"date-parts":[[2025,3]]},
          "abstract":"<jats:title>Abstract</jats:title><jats:p>Agents &amp; grids work well.</jats:p>",
          "URL":"https://doi.org/10.1016/j.epsr.2025.109876",
          "link":[{"URL":"https://example.org/tdm.xml","content-type":"text/xml","content-version":"vor"},
                  {"URL":"https://example.org/paper.pdf","content-type":"application/pdf","content-version":"vor"}]
        }]}}
        """;
        var p = Assert.Single(CrossrefSource.Parse(json));

        Assert.StartsWith("CROSSREF_", p.Id);
        Assert.Equal("Grid agents", p.Title);
        Assert.Equal("Agents & grids work well.", p.Abstract);
        Assert.Equal("Electric Power Systems Research", p.JournalSource);
        Assert.Equal("https://example.org/paper.pdf", p.PdfUrl);
        Assert.Equal(2025, ApaCitationBuilder.ExtractYear(p.PublishedDate));
    }

    // The next three tests use records copied (shortened) from live API responses on 2026-09-28, to pin the
    // shapes the adapters actually meet: nulls, empty strings, family-first names and escaped HTML.

    [Fact]
    public void LiveOpenAlexShapesWithNullAbstractAndNullPdfAreHandled()
    {
        const string json = """
        {"meta":{"count":258823},"results":[
          {"id":"https://openalex.org/W2981731882","doi":"https://doi.org/10.1016/j.inffus.2019.12.012",
           "display_name":"Explainable Artificial Intelligence (XAI): Concepts, taxonomies, opportunities and challenges toward responsible AI",
           "publication_year":2019,"authorships":[{"author":{"display_name":"Alejandro Barredo Arrieta"}}],
           "primary_location":{"source":{"display_name":"Information Fusion","type":"journal"}},
           "best_oa_location":{"pdf_url":"http://hdl.handle.net/20.500.11824/1166"},"abstract_inverted_index":null},
          {"id":"https://openalex.org/W4406728221","doi":"https://doi.org/10.1109/access.2025.3532853",
           "display_name":"Agentic AI: Autonomous Intelligence for Complex Goals—A Comprehensive Survey","publication_year":2025,
           "authorships":[{"author":{"display_name":"Deepak Bhaskar Acharya"}}],
           "primary_location":{"source":{"display_name":"IEEE Access","type":"journal"}},
           "best_oa_location":{"pdf_url":null},"abstract_inverted_index":{"Agentic":[0],"AI,":[1],"an":[2],"emerging":[3],"paradigm":[4]}},
          {"id":"https://openalex.org/W2896457183","doi":"https://doi.org/10.4230/lipics.cosit.2022.18","display_name":"A repository record",
           "publication_year":2018,"authorships":[{"author":{"display_name":"Kefallinos, Dionysios"}}],
           "primary_location":{"source":null},"best_oa_location":null,"abstract_inverted_index":null}
        ]}
        """;
        var papers = OpenAlexSource.ParseWorks(json);

        Assert.Equal(3, papers.Count);
        Assert.Equal("", papers[0].Abstract);
        Assert.Equal("10.1109/access.2025.3532853", papers[1].Doi);
        Assert.Null(papers[1].PdfUrl);
        Assert.Equal("Agentic AI, an emerging paradigm", papers[1].Abstract);
        Assert.Equal("", papers[2].JournalSource);
        Assert.Equal("Kefallinos, D.", ApaCitationBuilder.FormatAuthor(papers[2].Authors[0]));
    }

    [Fact]
    public void LiveSemanticScholarEmptyPdfUrlIsIgnored()
    {
        const string json = """
        {"total":35653,"offset":0,"next":5,"data":[
          {"paperId":"181744792430299bb2660af7a74cfa4a4f155291","externalIds":{"DOI":"10.63282/3050-9416.ijaibdcms-v6i1p122","CorpusId":288682746},
           "url":"https://www.semanticscholar.org/paper/181744792430299bb2660af7a74cfa4a4f155291",
           "title":"Agentic AI Frameworks for Autonomous Enterprise Software Development Workflows",
           "venue":"International Journal of AI, BigData, Computational and Management Studies","year":2025,
           "openAccessPdf":{"url":"","status":null,"license":null,"disclaimer":"Notice: ..."},
           "publicationTypes":["JournalArticle"],"authors":[{"authorId":"2438775194","name":"Yasodhara Srinivas Aluri"}],
           "abstract":"Enterprise software engineering has grown at a fast pace."},
          {"paperId":"7ef12e7f28b538a044f3b2c2af4160446723a200","externalIds":{"ArXiv":"2512.23480","DOI":"10.1109/ICCA66035.2025.11430751"},
           "title":"Agentic AI for Autonomous Defense in Software Supply Chain Security","venue":"International Conferences on Computing Advancements",
           "year":2025,"openAccessPdf":{"url":"","status":null},"authors":[{"authorId":null,"name":"Mohammad Riyaz Belgaum"}],"abstract":null}
        ]}
        """;
        var papers = SemanticScholarSource.Parse(json);

        Assert.Equal(2, papers.Count);
        Assert.Null(papers[0].PdfUrl);
        Assert.Equal("10.1109/ICCA66035.2025.11430751", papers[1].Doi); // the publisher DOI wins over the arXiv one
        Assert.Equal(2025, ApaCitationBuilder.ExtractYear(papers[1].PublishedDate));
    }

    [Fact]
    public void LiveCrossrefAbstractWithEscapedHtmlIsCleaned()
    {
        const string json = """
        {"status":"ok","message":{"total-results":915123,"items":[
          {"DOI":"10.2139/ssrn.5342108","title":["Does Agentic AI Require New Policy Frameworks?"],
           "author":[{"given":"Sarah","family":"Lam","sequence":"first","affiliation":[]}],
           "issued":{"date-parts":[[2025]]},"type":"posted-content",
           "abstract":"<jats:p>&lt;span&gt;Does the emergence of agentic AI require new policy frameworks?&lt;/span&gt;</jats:p>",
           "URL":"https://doi.org/10.2139/ssrn.5342108","link":[]},
          {"DOI":"10.1109/ms.2025.3622209","title":["Agentic AI Frameworks Under the Microscope: What Works, What Doesn’t"],
           "author":[{"given":"Karthik","family":"Vaidhyanathan","sequence":"first"}],"container-title":["IEEE Software"],
           "issued":{"date-parts":[[2026,1]]},"type":"journal-article","URL":"https://doi.org/10.1109/ms.2025.3622209",
           "link":[{"URL":"http://xplorestaging.ieee.org/ielx8/52/11316879/11316910.pdf?arnumber=11316910","content-type":"unspecified","content-version":"vor","intended-application":"similarity-checking"}]}
        ]}}
        """;
        var papers = CrossrefSource.Parse(json);

        Assert.Equal("Does the emergence of agentic AI require new policy frameworks?", papers[0].Abstract);
        Assert.Equal("", papers[0].JournalSource); // posted content (a preprint) has no venue
        Assert.Null(papers[1].PdfUrl);             // "unspecified" links are text-mining copies, not open PDFs
        Assert.Equal("IEEE Software", papers[1].JournalSource);
    }

    [Fact]
    public void UnpaywallPdfLinkIsTakenFromTheBestOrAnyOpenLocation()
    {
        Assert.Equal("https://a.org/x.pdf", DocumentRAGUtility.ParseUnpaywallPdfUrl("""{"best_oa_location":{"url_for_pdf":"https://a.org/x.pdf"}}"""));
        Assert.Equal("https://b.org/y.pdf", DocumentRAGUtility.ParseUnpaywallPdfUrl("""{"best_oa_location":{"url_for_pdf":null},"oa_locations":[{"url_for_pdf":null},{"url_for_pdf":"https://b.org/y.pdf"}]}"""));
        Assert.Null(DocumentRAGUtility.ParseUnpaywallPdfUrl("""{"best_oa_location":null,"oa_locations":[]}"""));
    }

    [Fact]
    public async Task PdfDownloadsStopAtTheSizeLimit()
    {
        using var tooBig = new MemoryStream(new byte[5000]);
        using var small = new MemoryStream(new byte[1000]);
        using var output1 = new MemoryStream();
        using var output2 = new MemoryStream();
        Assert.False(await DocumentRAGUtility.CopyWithLimitAsync(tooBig, output1, maxBytes: 4096));
        Assert.True(await DocumentRAGUtility.CopyWithLimitAsync(small, output2, maxBytes: 4096));
    }

    private class RateLimitedHandler : HttpMessageHandler
    {
        private readonly TimeSpan _retryAfter;
        public int Calls;
        public RateLimitedHandler(TimeSpan retryAfter) => _retryAfter = retryAfter;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Refuse(_retryAfter));
        }

        // The HttpClient that called the handler owns and disposes the response.
        private static HttpResponseMessage Refuse(TimeSpan retryAfter) => new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}"),
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter) },
        };
    }

    [Fact]
    public async Task ARateLimitThatAsksForALongWaitIsNotRetried()
    {
        // OpenAlex once the day's budget is used up: four capped waits of 30 s were refused again, 2 minutes a query.
        var handler = new RateLimitedHandler(TimeSpan.FromHours(1));
        using var http = new HttpClient(handler);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            OpenSourceHttp.GetStringAsync("https://limited.example.org/works", client: http));

        Assert.Equal(1, handler.Calls);
        Assert.Contains("asked to wait 60 min", ex.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARateLimitWithAShortWaitIsStillRetried()
    {
        var handler = new RateLimitedHandler(TimeSpan.Zero);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            OpenSourceHttp.GetStringAsync("https://limited.example.org/works", maxAttempts: 3, client: http));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public void TheOpenAlexKeyTravelsInAHeaderNotInTheAddress()
    {
        Assert.Null(OpenAlexSource.AuthHeaders(""));
        Assert.Null(OpenAlexSource.AuthHeaders(null));
        Assert.Equal("Bearer abc123", OpenAlexSource.AuthHeaders(" abc123 ")!["Authorization"]);
        Assert.Equal("", new OpenSourcesOptions().OpenAlexApiKey); // optional, and never in appsettings.json
    }
}
