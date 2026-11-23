using System.Text;
using AuBtechReviewAgent;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Long synthetic grey pages for the passage selection: a title, a byline and a date at the start, many paragraphs of
/// ordinary text on the review's topic, one planted sentence for each Table 7 item that the model answers, placed in
/// the middle of the page where a blunt cut would lose it, and an about-the-author and a reference list at the end.
/// </summary>
public static class LongGreyPages
{
    /// <summary>The sentence on the page that the fake model quotes for every answer.</summary>
    public const string Anchor = "We keep a running summary of the conversation.";

    /// <summary>For each Table 7 item the model answers, and for an opinion piece's claim, a sentence that answers it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Planted = new Dictionary<string, string>
    {
        ["1.1"] = "Example Labs is a research institute funded by the national science foundation and known for its open reports.",
        ["1.2"] = "Ana Author leads the platform team at Example Corp, where the agents in this post are run.",
        ["1.3"] = "She has previously written a book on retrieval systems and several articles on agent design.",
        ["1.4"] = "As a principal engineer with ten years of experience in search, she builds these systems every day.",
        ["2.1"] = "The goal of this post is to explain how to keep the context of an agent small and useful.",
        ["2.2"] = "We measured token use on 200 tasks with a fixed benchmark and the same model for every setup.",
        ["2.3"] = "According to the documentation of the model provider and the study by Liu et al. (2024), long contexts lose recall in the middle.",
        ["2.4"] = "A limitation of these results is that we tested only one model and one kind of task.",
        ["2.5"] = "The question we answer is whether summaries beat retrieval for long conversations.",
        ["2.6"] = "The case is a customer support team of twelve developers at a mid-sized software company.",
        ["3.1"] = "Both approaches have advantages and disadvantages, and the trade-offs depend on the task at hand.",
        ["3.2"] = "The results show a 30 percent drop in tokens, measured over all tasks rather than estimated.",
        ["3.3"] = "Disclosure: this post is sponsored by the vendor of the product we compare.",
        ["3.4"] = "Our conclusion follows from the data: summaries cut cost without lowering accuracy.",
        ["5.1"] = "See also our earlier blog post on memory and the related article on retrieval for further reading.",
        ["6.1"] = "To our knowledge this is the first public comparison of the two practices on real support tickets.",
        ["6.2"] = "This finding is contrary to the common belief that longer contexts always help.",
        ["claim"] = "I believe that every agent should keep a running summary instead of the whole history.",
    };

    /// <summary>
    /// A second sentence for each item, written as pages write them and not with the selection's word lists in view,
    /// to see which items lexical selection misses when a page says it in other words.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Paraphrased = new Dictionary<string, string>
    {
        ["1.1"] = "This report comes from the working group on AI of a large open-source foundation.",
        ["1.2"] = "Maria works at a big research lab in Cambridge.",
        ["1.3"] = "Her earlier talk at KubeCon covered the same ground.",
        ["1.4"] = "I have spent the last decade building search infrastructure at a large web company.",
        ["2.1"] = "In this tutorial I walk through three ways to trim the memory of an agent.",
        ["2.2"] = "We ran each configuration five times and averaged the latency.",
        ["2.3"] = "The numbers above come from the LangChain blog and the original MemGPT paper.",
        ["2.4"] = "These numbers will not hold for every workload, so treat them as a rough guide.",
        ["2.5"] = "Can a smaller window make an agent more reliable?",
        ["2.6"] = "We looked at the logs of 40 enterprise customers using our assistant.",
        ["3.1"] = "There are good reasons to prefer the other option too, which I cover below.",
        ["3.2"] = "Across 1,000 runs the error rate fell from 12% to 7%.",
        ["3.3"] = "Full disclosure: I work for the company that makes this tool.",
        ["3.4"] = "Given these measurements, we think the summary approach is the better default.",
        ["5.1"] = "This builds on an earlier post by Simon Willison about prompt injection, linked here.",
        ["6.1"] = "Nobody else seems to have written about this failure mode yet.",
        ["6.2"] = "This goes against what most tutorials recommend.",
        ["claim"] = "In my view, retrieval is overrated for agents.",
    };

    /// <summary>A third sentence for each item, written after the word lists were last changed and never used to change them.</summary>
    public static readonly IReadOnlyDictionary<string, string> Unseen = new Dictionary<string, string>
    {
        ["1.1"] = "The guide is published by the national cybersecurity centre.",
        ["1.2"] = "Tom is part of the developer relations group at a cloud provider.",
        ["1.3"] = "He is also the author of a popular newsletter on machine learning.",
        ["1.4"] = "As head of AI at a fintech start-up, I see these problems weekly.",
        ["2.1"] = "Here I want to show why agents forget things.",
        ["2.2"] = "To compare them, I built a small harness that replays the same chats.",
        ["2.3"] = "For the theory, see Shannon (1948) and the recent survey on long-context models.",
        ["2.4"] = "I only tried this on English text, and other languages may behave differently.",
        ["2.5"] = "So does a bigger context window really help?",
        ["2.6"] = "Our users are mostly lawyers who paste long contracts into the chat.",
        ["3.1"] = "Retrieval is not always the winner; for short chats the plain history works fine.",
        ["3.2"] = "Latency went down by 200 milliseconds on average.",
        ["3.3"] = "Try our hosted memory service free for 14 days.",
        ["3.4"] = "The drop in latency is why we now ship summaries by default.",
        ["5.1"] = "The write-up on effective context engineering by a model provider makes a similar point.",
        ["6.1"] = "I could not find any other benchmark that tests this.",
        ["6.2"] = "Contrary to the popular advice, more context made our agent worse.",
        ["claim"] = "Long context windows are a trap for most teams.",
    };

    private static readonly string[] Filler =
    {
        "Agents read their context window on every step of a task.",
        "A context window holds the instructions, the tool descriptions, the history and the documents an agent works with.",
        "When the window fills up, older turns are dropped or the call fails.",
        "Many teams start with the whole conversation and trim it later.",
        "Tool descriptions take more room than most developers expect.",
        "A long system prompt is sent again with every single call.",
        "Some frameworks keep state between steps in a graph of nodes.",
        "Memory can live outside the model in a store that the agent queries.",
        "Vector stores return passages that are close in meaning to the query.",
        "Chunk size changes what the agent sees in each retrieved passage.",
        "Prompt caching lowers the price of a prefix that does not change.",
        "The order of messages in the window matters for what the model attends to.",
        "Some agents write notes to a scratchpad that is read back later.",
        "A planner agent can hand smaller tasks to workers with their own windows.",
        "Each worker then needs only the part of the context that its task touches.",
        "Logs of tool calls grow quickly when an agent loops over files.",
        "Truncating tool output is a simple way to keep the window small.",
        "Structured outputs make it easier to keep only the fields that matter.",
        "Developers often print the full prompt to see what the model was given.",
        "Small models suffer more from noise in a long window than large ones.",
        "Context rot is a name some writers use for the slow loss of focus in long sessions.",
        "Compression of old turns can keep the main facts of a session.",
        "Agents that browse the web bring in a lot of text they never use.",
        "A good tool returns only what the agent asked for.",
        "Pinning the task description at the top of the window helps the agent stay on track.",
        "Code agents read many files, so they need a way to choose which ones matter.",
        "Repository maps give an agent a short overview of a code base.",
        "Embedding models turn text into vectors for search.",
        "A reranker can sort retrieved passages before they reach the agent.",
        "Some teams keep a separate window for each sub-goal of a task.",
    };

    /// <summary>The HTML of page <paramref name="number"/>, about <paramref name="length"/> characters of text long.</summary>
    public static string Html(int number, int length, bool planted = true)
    {
        var random = new Random(number);
        var paragraphs = new List<string>();
        int total = 0;
        while (total < length - 1_200)
        {
            string p = string.Join(" ", Enumerable.Range(0, random.Next(3, 7)).Select(_ => Filler[random.Next(Filler.Length)]));
            paragraphs.Add(p);
            total += p.Length + 1;
        }
        if (planted)
        {
            // Spread the planted sentences over the middle fifth of the page, each in its own paragraph.
            var sentences = Planted.Values.ToList();
            int from = paragraphs.Count * 2 / 5, span = Math.Max(1, paragraphs.Count / 5);
            for (int i = 0; i < sentences.Count; i++) paragraphs.Insert(Math.Min(paragraphs.Count, from + i * span / sentences.Count + i), sentences[i]);
        }

        var html = new StringBuilder("<html><body><main>");
        html.Append($"<h1>Managing the context of an agent, part {number}</h1><p>By Ana Author. Published 1 May 2025.</p><p>{Anchor}</p>");
        foreach (var p in paragraphs) html.Append("<p>").Append(p).Append("</p>");
        html.Append("<h2>About the author</h2><p>Ana Author is a staff engineer at Example Corp who writes about agent tooling.</p>");
        html.Append("<h2>References</h2><p>[1] Liu et al. (2024). Lost in the middle: how language models use long contexts.</p><p>[2] Example Corp. Agent memory guide.</p>");
        return html.Append("</main></body></html>").ToString();
    }

    /// <summary>The kept text of page <paramref name="number"/>, as the page fetcher keeps it.</summary>
    public static string Text(int number, int length, bool planted = true) => PageText.FromHtml(Html(number, length, planted));
}
