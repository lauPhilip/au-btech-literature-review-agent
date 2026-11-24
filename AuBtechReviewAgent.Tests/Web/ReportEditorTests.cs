using AuBtechReviewAgent.Components.Paper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The checked editor on the report page (issue #413), rendered with Blazor's HtmlRenderer: who sees a way to edit,
/// which texts can be edited, the edit box in each state, the "edited" mark and the history with its undo.
/// </summary>
public class ReportEditorTests
{
    private const string Text = "Checks before an action are common [1]. Rollback is rarely tested [2].";

    private static CitationSupportResult Check(string field, int sentence, int reference, string verdict, string? quote = "a passage") => new()
    {
        Field = field, SentenceIndex = sentence, Reference = reference, Verdict = verdict, Quote = quote,
        QuoteVerified = quote != null, EvidenceBasis = "full text", Reason = "Because.",
    };

    private static ReportEdit Edit(int number, string action, int sentence, int? undoes = null, params CitationSupportResult[] checks) =>
        new(number, action, "discussionItem", sentence, "before " + number, "after " + number, new DateTime(2026, 11, 23, 14, 2, 0, DateTimeKind.Utc),
            "run owner (edit key)", checks.ToList(), "fast", "1.0", undoes);

    [Fact]
    public async Task OnlyTheOwnerWithTheEditorOnSeesAnEditButtonOnEachSentence()
    {
        var checks = new List<CitationSupportResult> { Check("discussionItem", 0, 1, CitationSupportChecker.Supported) };
        var common = new Dictionary<string, object?> { ["Field"] = "discussionItem", ["Text"] = Text, ["Checks"] = checks, ["EditName"] = "the discussion" };

        string owner = await RenderAsync<CitedProse>(new(common) { ["Editable"] = true, ["Editing"] = 1 });
        string reader = await RenderAsync<CitedProse>(new(common) { ["Editable"] = false });

        Assert.Contains("aria-label=\"Edit sentence 1 of the discussion\" aria-expanded=\"false\"", owner);
        Assert.Contains("aria-label=\"Edit sentence 2 of the discussion\" aria-expanded=\"true\"", owner);
        Assert.Contains("id=\"edit-discussionItem-0\"", owner);
        Assert.DoesNotContain(">Edit</button>", reader);
        Assert.DoesNotContain("cursor-text", reader);
        Assert.Contains("id=\"cite-discussionItem-0-1\"", reader);
    }

    [Fact]
    public void CodeWrittenPartsAndOtherReadersCanNeverEdit()
    {
        Assert.True(EditView.CanEdit(owner: true, editing: true, "discussionItem"));
        Assert.True(EditView.CanEdit(owner: true, editing: true, "synthesis-2"));
        Assert.True(EditView.CanEdit(owner: true, editing: true, "researchAnswer1"));
        Assert.False(EditView.CanEdit(owner: false, editing: true, "discussionItem"));
        Assert.False(EditView.CanEdit(owner: true, editing: false, "discussionItem"));
        foreach (string written in new[] { "artifact-n1", "artifact-r1c2", "AbstractItem", "SynthesisMethodsItem", "reviewLimitationsItem", "citationCheckSummary" })
            Assert.False(EditView.CanEdit(owner: true, editing: true, written));
    }

    [Fact]
    public async Task AnEditedSentenceCarriesAMarkThatLinksToItsEntryInTheHistory()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Field"] = "discussionItem", ["Text"] = Text, ["HistoryHref"] = "/spec-matrix/abc",
            ["Edited"] = new Dictionary<int, int> { [1] = 4 },
        };
        string html = await RenderAsync<CitedProse>(parameters);
        string plain = await RenderAsync<CitedProse>(new() { ["Field"] = "discussionItem", ["Text"] = Text });

        Assert.Contains("href=\"/spec-matrix/abc#report-edit-4\" data-jump-to=\"report-edit-4\"", html);
        Assert.Equal(1, Count(html, ">edited<"));
        Assert.DoesNotContain("edited", plain);
    }

    [Fact]
    public async Task TheEditBoxShowsTheQuotesThenProgressThenTheNewVerdicts()
    {
        var now = new List<CitationSupportResult> { Check("discussionItem", 1, 2, CitationSupportChecker.Partial, "rarely tested in practice") };
        var open = new Dictionary<string, object?>
        {
            ["Sentence"] = 1, ["Label"] = "the discussion", ["Original"] = "Rollback is rarely tested [2, 3].", ["Checks"] = now,
            ["UsedLine"] = "3 of 100 edits used", ["SourceTitle"] = (Func<int, string?>)(r => $"Study {r}"),
        };

        string idle = await RenderAsync<SentenceEditor>(open);
        Assert.Contains("Edit sentence 2 of the discussion", idle);
        Assert.Contains("3 of 100 edits used", idle);
        Assert.Contains("<label for=\"sentence-editor-text\"", idle);
        Assert.Contains("Rollback is rarely tested [2, 3].</textarea>", idle);
        Assert.Contains(">Check and save</button>", idle);
        Assert.Contains(">Cancel</button>", idle);
        Assert.Contains("ui-chip border-partly-border bg-partly-fill text-partly", idle);
        Assert.Contains("rarely tested in practice", idle);
        Assert.DoesNotContain("disabled", idle);

        string checking = await RenderAsync<SentenceEditor>(new(open) { ["Checking"] = true });
        Assert.Contains("Checking 2 citations against the papers.", checking);
        Assert.Contains("role=\"status\"", checking);
        Assert.Contains(">Checking", checking);
        Assert.Equal(3, Count(checking, "disabled"));

        string refused = await RenderAsync<SentenceEditor>(new(open) { ["Error"] = "[9] is not in the reference list." });
        Assert.Contains("role=\"alert\"", refused);
        Assert.Contains("[9] is not in the reference list.", refused);

        var saved = Edit(4, ReportEdit.Edited, 1, null,
            Check("discussionItem", 1, 2, CitationSupportChecker.Supported, "tested only in a sandbox"),
            Check("discussionItem", 1, 3, CitationSupportChecker.NotSupported, null));
        string done = await RenderAsync<SentenceEditor>(new(open) { ["Result"] = saved });
        Assert.Contains("Saved. Every citation in your sentence was checked against the papers.", done);
        Assert.Contains("ui-chip border-supported-border bg-supported-fill text-supported", done);
        Assert.Contains("ui-chip border-not-supported-border bg-not-supported-fill text-not-supported", done);
        Assert.Contains("tested only in a sandbox", done);
        Assert.Contains("stays in the text and is marked", done);
        Assert.Contains(">Done</button>", done);
        Assert.DoesNotContain("<textarea", done);
    }

    [Fact]
    public async Task TheHistoryListsEveryEditAndOffersUndoOnlyOnTheNewestOfEachSentence()
    {
        var log = new ReportEditLog
        {
            Edits =
            {
                Edit(1, ReportEdit.Edited, 0, null, Check("discussionItem", 0, 1, CitationSupportChecker.Supported)),
                Edit(2, ReportEdit.Edited, 0),
                Edit(3, ReportEdit.Undone, 0, 2),
                Edit(4, ReportEdit.Edited, 1, null, Check("discussionItem", 1, 2, CitationSupportChecker.Partial)),
            },
        };
        var parameters = new Dictionary<string, object?> { ["Log"] = log, ["FieldLabel"] = (Func<string, string>)(_ => "the discussion") };

        string owner = await RenderAsync<EditHistory>(new(parameters) { ["CanEdit"] = true });
        string reader = await RenderAsync<EditHistory>(new(parameters) { ["CanEdit"] = false });

        Assert.Contains("3 of 100 edits used", owner);
        Assert.True(owner.IndexOf("id=\"report-edit-4\"") < owner.IndexOf("id=\"report-edit-1\""), "newest first");
        Assert.Contains("Undo of edit 2 · sentence 1 of the discussion", owner);
        Assert.Contains("23 Nov 2026, 14:02 UTC", owner);
        Assert.Contains("What the check found: 1 partly supported", owner);
        Assert.Contains("Undone later (entry 3).", owner);
        Assert.Contains("aria-label=\"Undo edit 1\"", owner);
        Assert.Contains("aria-label=\"Undo edit 4\"", owner);
        Assert.DoesNotContain("aria-label=\"Undo edit 2\"", owner);
        Assert.DoesNotContain(">Undo</button>", reader);
        Assert.DoesNotContain("edits used", reader);
        Assert.Contains("id=\"report-edit-1\"", reader);
    }

    [Fact]
    public void TheEditViewCountsEditsAndFindsTheSentencesThatReadDifferently()
    {
        var log = new ReportEditLog { Edits = { Edit(1, ReportEdit.Edited, 0), Edit(2, ReportEdit.Undone, 0, 1), Edit(3, ReportEdit.Edited, 1) } };
        Assert.Equal(2, EditView.Used(log));
        Assert.Equal("2 of 100 edits used", EditView.UsedLine(log));
        Assert.Equal(new Dictionary<int, int> { [1] = 3 }, EditView.EditedIn(log, "discussionItem"));
        Assert.Empty(EditView.EditedIn(log, "conclusionItem"));
        Assert.Equal(new HashSet<int> { 3 }, EditView.Undoable(log));

        var report = new PrismaReport { DiscussionItem = Text };
        report.SynthesisSections.Add(new SynthesisSection { Field = "synthesis-1", Heading = "Recovery" });
        Assert.Equal("Rollback is rarely tested [2].", EditView.SentenceOf(report, "discussionItem", 1));
        Assert.Null(EditView.SentenceOf(report, "discussionItem", 2));
        Assert.Equal("the theme \"Recovery\"", EditView.FieldLabel(report, "synthesis-1"));
        Assert.Equal("the answer to research question 2", EditView.FieldLabel(report, "researchAnswer2"));
    }

    [Fact]
    public void TheReportPageOffersTheEditorOnlyToTheOwnerOfAFinishedRun()
    {
        string? page = ReportPageSource.Read();
        if (page == null) return; // not run from the repository (packaged tests)
        Assert.Contains("CanEditText => canEdit && !ReviewEngine.IsRunActive(State.CurrentSessionId)", page);
        Assert.Contains("Editable=\"AuBtechReviewAgent.EditView.CanEdit(CanEditText, editMode, field)\"", page);
        Assert.Contains("@if (CanEditText)", page);
        Assert.Contains("<EditHistory Log=\"editLog\" CanEdit=\"CanEditText\"", page);
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }
}
