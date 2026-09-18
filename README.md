# TraceableAI

A tool for running systematic literature reviews (SLRs) that you can check afterwards. It follows the PRISMA 2020 reporting standard and keeps a record of every step, so you can trace any statement in the final report back to the paper it came from.

Built at the Department of Business Development and Technology (BTECH), Aarhus University.

[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
[![LLM Engine](https://img.shields.io/badge/Engine-Mistral%20Large-purple.svg)](https://mistral.ai/)
[![Reporting](https://img.shields.io/badge/Reporting-PRISMA%202020-green.svg)](https://prisma-statement.org/)
[![OSSYM](https://img.shields.io/badge/OSSYM-2026-003B5C.svg)](https://opensearchfoundation.org/)

## What it's for

Most tools that write literature reviews for you have the same problem: the text reads well, but the citations behind it are often wrong, mismatched, or made up. That makes the output hard to trust, and it can't be used for a review that has to follow PRISMA 2020, where you're expected to show how you got to your conclusions.

TraceableAI is an attempt to fix that. Instead of just handing you a finished write-up, it saves a full record of what it searched for, which papers it kept or dropped and why, and which source each statement in the report is based on. If something looks off, you can go back and check it.

## What it does

- **Ties claims to sources.** Before writing the review, it builds an outline that maps each point to the papers that support it, then writes the text against that outline instead of filling in generic placeholders.
- **Checks its own citations.** Every reference number in the generated text is checked against the actual reference list. If the model invents a number that doesn't exist (for example, citing source 56 when there are only 53), it's removed, and every removal is written to a `citation-audit.json` file so you can see what was caught.
- **Searches each source a few different ways.** Rather than running one exact phrase, it rephrases the query from a few angles (a wording variant, a narrower sub-topic, a method-focused version) to catch papers a single phrasing would miss, and removes duplicates.
- **Keeps the result count predictable.** You can set a maximum number of results per source (for example, 3). That's a hard limit no matter how many query variations run, which keeps test runs small and cheap.
- **Saves the whole run.** After a review finishes you can download a single .zip with the report, a step-by-step log of every decision, the source PDFs it used, and the citation audit.
- **Shows a live preview.** The finished review is laid out as a two-column academic page with charts for publication year and where the papers came from.

## How it works

A run goes through five steps:

1. **Search.** It queries open and academic sources (arXiv, ScienceDirect/Scopus, IEEE Xplore, Google Scholar, and ResearchGate), using a few phrasings of your query for each one.
2. **Screen.** Each candidate paper is checked by the language model against your inclusion and exclusion rules, with an optional peer-review-only filter.
3. **Download.** The PDFs of the papers that pass are saved into a working folder (`PapersWorkspace/`).
4. **Read the PDFs.** It extracts the text page by page (using PdfPig, a C# library) and splits it into short overlapping chunks, each tagged with the paper it came from.
5. **Write the report.** It sends those chunks to Mistral Large and gets back the PRISMA checklist items, with the citation check described above applied to the text.

## System architecture

![System Architecture](./diagram%20for%20traceability%20AI%20review%20agent.png)

## What you get after a run

Each run clears out the previous one and leaves a clean set of files you can download as a .zip:

```text
├── PapersWorkspace/            # PDFs of the papers used in this run (cleared each run)
│   ├── Framework_Design_Patterns.pdf
│   └── Trustworthy_System_Safety.pdf
├── prisma-report.json          # The finished PRISMA checklist items
├── transparent-process.json    # A full log of every screening decision
├── citation-audit.json         # Any invalid citation numbers that were removed
└── grounded-outline.txt         # The claim-to-source outline used to write the review
```
