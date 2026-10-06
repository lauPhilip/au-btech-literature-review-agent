using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The free grey literature searches (MLR block D), offline: each parser reads an answer in the shape the API
/// documents, keeps only http(s) addresses and cleans the text.
/// </summary>
public class GreySourceTests
{
    [Fact]
    public void StackExchangeQuestionsKeepTheirScoreAndPlainText()
    {
        const string json = """
            {"items":[
              {"tags":["llm"],"owner":{"display_name":"Ana &amp; Bo"},"is_answered":true,"view_count":1520,"answer_count":3,"score":42,
               "creation_date":1735689600,"question_id":79001,"link":"https://stackoverflow.com/questions/79001/trim-context",
               "title":"How do I trim the context window &quot;safely&quot;?","body":"<p>My agent <code>forgets</code> tools.</p>"},
              {"question_id":2,"link":"javascript:alert(1)","title":"bad"}
            ],"has_more":false,"quota_remaining":290}
            """;

        var record = Assert.Single(StackExchangeSource.Parse(json));

        Assert.Equal("stackexchange:79001", record.Id);
        Assert.Equal("How do I trim the context window \"safely\"?", record.Title);
        Assert.Equal("My agent forgets tools.", record.Summary);
        Assert.Equal("Ana & Bo", record.Producer);
        Assert.Equal("stackoverflow.com", record.Site);
        Assert.Equal("qa", record.Kind);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), record.Published);
        Assert.Equal(42, record.Signals["score"]);
        Assert.Equal(1520, record.Signals["views"]);
        Assert.Equal(3, record.Signals["answers"]);
    }

    [Fact]
    public void AHackerNewsStoryPointsToItsArticleAndAnAskStoryToItsDiscussion()
    {
        const string json = """
            {"hits":[
              {"objectID":"401","title":"Context engineering in practice","url":"https://example.org/blog/context","author":"pg","points":180,"num_comments":64,"created_at":"2025-06-01T10:00:00Z","story_text":null},
              {"objectID":"402","title":"Ask HN: How do you manage agent memory?","url":null,"author":"x","points":12,"num_comments":9,"created_at":"2025-06-02T10:00:00Z","story_text":"<p>We tried <i>summaries</i>.</p>"}
            ]}
            """;

        var records = HackerNewsSource.Parse(json);

        Assert.Equal("https://example.org/blog/context", records[0].Url);
        Assert.Equal("blogs", records[0].Kind);
        Assert.Equal("example.org", records[0].Site);
        Assert.Equal(180, records[0].Signals["points"]);
        Assert.Equal("https://news.ycombinator.com/item?id=402", records[1].Url);
        Assert.Equal("qa", records[1].Kind);
        Assert.Equal("We tried summaries.", records[1].Summary);
    }

    [Fact]
    public void GitHubRepositoriesKeepTheirStars()
    {
        const string json = """
            {"total_count":1,"items":[{"id":7,"full_name":"acme/context-kit","html_url":"https://github.com/acme/context-kit","description":"Tools for context windows","stargazers_count":950,"forks_count":40,"updated_at":"2026-01-02T00:00:00Z","owner":{"login":"acme"}}]}
            """;

        var record = Assert.Single(GitHubSource.Parse(json));

        Assert.Equal("github:acme/context-kit", record.Id);
        Assert.Equal("code", record.Kind);
        Assert.Equal("acme", record.Producer);
        Assert.Equal(950, record.Signals["stars"]);
    }

    [Fact]
    public void ZenodoKeepsOnlyGreyLiteratureInBothAnswerShapes()
    {
        // The first record has the newer shape; the others the shape Zenodo answered with in a live check
        // (resource_type with type and subtype only).
        const string json = """
            {"hits":{"total":5,"hits":[
              {"id":111,"doi":"10.5281/zenodo.111","metadata":{"title":"Context engineering report","description":"<p>A report.</p>","publication_date":"2025-03-01",
                "creators":[{"name":"Lee, K."}],"resource_type":{"id":"publication-report","title":{"en":"Report"}}},"links":{"self_html":"https://zenodo.org/records/111"}},
              {"id":112,"metadata":{"title":"A thesis on agents","publication_date":"2024","resource_type":{"type":"publication","subtype":"thesis"}}},
              {"id":10290739,"metadata":{"title":"UNCW Archaeology Lab Artifact of the Week #9","creators":[{"name":"context","affiliation":"none"}],"resource_type":{"title":"Dataset","type":"dataset"}},"links":{"self_html":"https://zenodo.org/records/10290739"}},
              {"id":114,"metadata":{"title":"A journal article","resource_type":{"type":"publication","subtype":"article"}}},
              {"id":113,"metadata":{"title":"agent-tool v1.2","resource_type":{"id":"software"}},"links":{"self_html":"https://zenodo.org/records/113"}}
            ]}}
            """;

        var records = ZenodoSource.Parse(json);

        Assert.Equal(new[] { "zenodo:111", "zenodo:112" }, records.Select(r => r.Id));
        Assert.Equal("white-papers", records[0].Kind);
        Assert.Equal("Lee, K.", records[0].Producer);
        Assert.Equal("A report.", records[0].Summary);
        Assert.Equal("theses", records[1].Kind);
        Assert.Equal("https://zenodo.org/records/112", records[1].Url);
        Assert.Equal(5, ZenodoSource.HitsOnPage(json)); // grey or not: decides whether there is a next page
        Assert.Equal(0, ZenodoSource.HitsOnPage("{\"status\":400}"));
    }

    [Fact]
    public void OpenAlexKeepsReportsThesesAndStandardsWithTheirCitations()
    {
        const string json = """
            {"meta":{"count":4},"results":[
              {"id":"https://openalex.org/W1","doi":"https://doi.org/10.1/rep","display_name":"Context engineering for agents: a report","type":"report",
               "publication_date":"2025-04-02","cited_by_count":7,
               "authorships":[{"author":{"display_name":"K. Lee"}}],
               "primary_location":{"landing_page_url":"https://example.org/reports/ce","source":{"display_name":"Example Institute"}},
               "abstract_inverted_index":{"Agents":[0],"forget":[1],"tools.":[2]}},
              {"id":"https://openalex.org/W2","doi":null,"display_name":"Memory in LLM agents","type":"dissertation","cited_by_count":0,
               "authorships":[],"primary_location":{"landing_page_url":"javascript:alert(1)","source":{"display_name":"Aarhus University"}}},
              {"id":"https://openalex.org/W3","display_name":"A journal article","type":"article"},
              {"id":"https://openalex.org/W4","display_name":"","type":"report"}
            ]}
            """;

        var records = OpenAlexGreySource.Parse(json);

        Assert.Equal(new[] { "openalex:W1", "openalex:W2" }, records.Select(r => r.Id));
        Assert.Equal("https://example.org/reports/ce", records[0].Url);
        Assert.Equal("example.org", records[0].Site);
        Assert.Equal("white-papers", records[0].Kind);
        Assert.Equal("K. Lee", records[0].Producer);
        Assert.Equal("Agents forget tools.", records[0].Summary);
        Assert.Equal(7, records[0].Signals["citations"]);
        Assert.Equal(new DateTime(2025, 4, 2, 0, 0, 0, DateTimeKind.Utc), records[0].Published);
        Assert.Equal("https://openalex.org/W2", records[1].Url); // no landing page that is a web address, no DOI
        Assert.Equal("theses", records[1].Kind);
        Assert.Equal("Aarhus University", records[1].Producer);
    }

    [Fact]
    public void OpenAlexIsSearchedForGreyWorkTypesOnly()
    {
        string url = OpenAlexGreySource.SearchUrl("context engineering", 500, "me@example.org");
        Assert.Contains("search=context%20engineering", url);
        Assert.Contains("filter=type:report|dissertation|standard", url);
        Assert.Contains("per-page=100", url);
        Assert.Contains("mailto=me%40example.org", url);
    }

    [Theory]
    [InlineData("context engineering", "q=%22context%20engineering%22")]
    [InlineData("agents", "q=agents")]
    [InlineData("\"agent memory\" OR context", "q=%22agent%20memory%22%20OR%20context")]
    public void ZenodoSearchesSeveralPlainWordsAsAPhrase(string query, string expected) =>
        Assert.Contains(expected, ZenodoSource.SearchUrl(query));

    [Fact]
    public void SearchAddressesEscapeTheQueryAndCapTheNumberOfHits()
    {
        Assert.Contains("q=context%20window%20%26%20tools", new StackExchangeSource().SearchUrl("context window & tools", 500));
        Assert.Contains("pagesize=100", new StackExchangeSource().SearchUrl("x", 500));
        Assert.Contains("hitsPerPage=1", HackerNewsSource.SearchUrl("x", 0));
        Assert.StartsWith("https://api.github.com/search/repositories?q=a%2Bb", GitHubSource.SearchUrl("a+b", 10));
        Assert.Contains("size=25&page=3", ZenodoSource.SearchUrl("x", 3)); // Zenodo refuses more than 25 a page without a key
    }

    [Fact]
    public void EveryBuiltSearchIsOneThePlanCanChoose()
    {
        foreach (var key in new[] { "stackexchange", "github", "hackernews", "openalex-grey", "zenodo" })
        {
            Assert.True(GreySourceCatalog.IsBuilt(key));
            Assert.Equal(key, GreySourceCatalog.Create(key)!.Key);
            Assert.Contains(MultivocalGuidelines.GreySearches, s => s.Key == key);
        }
        Assert.Null(GreySourceCatalog.Create("brave"));
        Assert.DoesNotContain(MultivocalGuidelines.GreySearches, s => s.Key == "devto");
    }
}
