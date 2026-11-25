using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The fingerprint of a prompt's fixed text, for the cache keys that hold a prompt's inputs but not the whole prompt
/// (screening, the multivocal quality and extraction steps, the formal appraisal). The prompt is written with a
/// placeholder in place of each piece of per-record data, and the result is hashed. An edit to the instructions then
/// changes the key on its own, so an old decision is never reused under a new prompt, even when nobody raised the
/// prompt's version (AGENTS.md rule 4). The version stays in the key, and a test pins each version to its fingerprint.
/// </summary>
public static class PromptTemplate
{
    /// <summary>The text put where a piece of per-record data goes, e.g. "{title}".</summary>
    public static string Slot(string name) => "{" + name + "}";

    /// <summary>
    /// The SHA-256 of one or more prompts written with placeholders, with Windows line endings read as Unix ones, so a
    /// checkout that turns the source files into CRLF gives the same keys.
    /// </summary>
    public static string Hash(params string[] rendered) => Hash((IEnumerable<string>)rendered);

    /// <inheritdoc cref="Hash(string[])"/>
    public static string Hash(IEnumerable<string> rendered) =>
        ReviewCache.Key(rendered.Select(r => r.Replace("\r\n", "\n")).ToArray());
}
