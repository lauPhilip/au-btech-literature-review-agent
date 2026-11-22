using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The year of a code repository (#249): GitHub gives the last push as updated_at, so every repository of a live run
/// was cited as 2026. A repository is cited by the year it was created, and its last push is kept as its last update.
/// </summary>
public class GitHubRepositoryYearTests
{
    // Recorded from the GitHub search API's answer shape, cut to the fields the source reads.
    private const string Answer = """
        {"total_count":2,"items":[
          {"id":1,"full_name":"muratcankoylan/Agent-Skills-for-Context-Engineering","html_url":"https://github.com/muratcankoylan/Agent-Skills-for-Context-Engineering","description":"Skills for context engineering","stargazers_count":120,"forks_count":9,"created_at":"2024-03-14T08:00:00Z","updated_at":"2026-10-30T12:00:00Z","pushed_at":"2026-10-29T17:45:00Z","owner":{"login":"muratcankoylan"}},
          {"id":2,"full_name":"acme/context-kit","html_url":"https://github.com/acme/context-kit","description":"Tools","stargazers_count":3,"forks_count":0,"created_at":"2025-01-02T00:00:00Z","updated_at":"2025-12-01T00:00:00Z","owner":{"login":"acme"}}
        ]}
        """;

    [Fact]
    public void ARepositoryIsDatedByItsCreationAndKeepsItsLastPush()
    {
        var records = GitHubSource.Parse(Answer);

        Assert.Equal(new DateTime(2024, 3, 14, 8, 0, 0, DateTimeKind.Utc), records[0].Published);
        Assert.Equal(new DateTime(2026, 10, 29, 17, 45, 0, DateTimeKind.Utc), records[0].Updated); // pushed_at, not updated_at
        Assert.Equal(new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc), records[1].Updated); // no pushed_at: updated_at
    }

    private static GreyReference Reference()
    {
        var record = GitHubSource.Parse(Answer)[0];
        var ledger = new MultivocalLedger { Sources = { new FoundGreySource { Record = record } } };
        var extraction = new MultivocalExtractionFile
        {
            Sources = { new GreyExtraction { Address = MultivocalSearcher.AddressKey(record.Url), Title = record.Title, Url = record.Url } },
        };
        return Assert.Single(GreyReferences.Build(ledger, new MultivocalPagesFile(), extraction));
    }

    [Fact]
    public void TheReferenceUsesTheCreationYearAndSaysWhenItWasLastUpdated()
    {
        var reference = Reference();

        Assert.Equal(2024, reference.Year);
        Assert.Equal("muratcankoylan (2024). muratcankoylan/Agent-Skills-for-Context-Engineering. *github.com*. https://github.com/muratcankoylan/Agent-Skills-for-Context-Engineering (last updated 2026-10-29)",
            GreyReferences.Line(reference));
    }

    [Fact]
    public void BibTeXAndRisCarryTheCreationYearWithTheLastUpdateInANote()
    {
        var reference = Reference();

        string bib = GreyReferences.ToBibTeX(new[] { reference });
        Assert.Contains("year = {2024}", bib);
        Assert.Contains("@misc{muratcankoylan2024muratcankoylan,", bib);
        Assert.Contains("; last updated 2026-10-29}", bib);
        Assert.DoesNotContain("2026}", bib.Replace("2026-10-29}", ""));

        string ris = GreyReferences.ToRis(new[] { reference });
        Assert.Contains("PY  - 2024", ris);
        Assert.Contains("N1  - Last updated 2026-10-29", ris);
    }

    [Fact]
    public void ARunStoredBeforeThisKeepsWhatItStored()
    {
        // An older ledger has no last update and its Published is the old updated_at; it is cited as it was.
        var old = new GreyRecord("github:acme/kit", "acme/kit", "", "https://github.com/acme/kit", "acme", "github.com", "code",
            new DateTime(2026, 1, 2), new Dictionary<string, long>());
        var ledger = new MultivocalLedger { Sources = { new FoundGreySource { Record = old } } };
        var extraction = new MultivocalExtractionFile { Sources = { new GreyExtraction { Address = MultivocalSearcher.AddressKey(old.Url), Title = old.Title, Url = old.Url } } };

        var reference = Assert.Single(GreyReferences.Build(ledger, new MultivocalPagesFile(), extraction));

        Assert.Equal(2026, reference.Year);
        Assert.DoesNotContain("last updated", GreyReferences.Line(reference));
    }
}
