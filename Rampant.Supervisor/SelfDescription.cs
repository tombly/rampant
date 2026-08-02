namespace Rampant.Supervisor;

public sealed record RevisionResult(bool Ok, string? Error, string? Text, bool WasNewSection);

/// <summary>
/// Rewrites one "## " section of SELF.md.
///
/// Section-at-a-time rather than whole-file, for two reasons. A model asked to reproduce a
/// 150-line file in order to change one paragraph will quietly lose or reword the other 149, and
/// there is nothing in the pipeline that would notice; and a bounded edit keeps the request small
/// enough to be read in full in the outcome log. Whole-file replacement was the obvious design and
/// is the wrong one.
///
/// Deliberately not a diff or a patch format. The agent describes what a section should say - the
/// same contract as a capability request, where it describes behaviour and never writes code.
///
/// Pure and static so it can be exercised against real SELF.md text without a container, a repo,
/// or a running supervisor.
/// </summary>
public static class SelfDescription
{
    /// <summary>Bounds prompt growth. SELF.md is prepended to every single turn, so an agent that
    /// appends a little each time is quietly paying for it forever, and nothing else in the system
    /// meters the OpenAI side. The seed is around 7KB; this leaves several times that for drift
    /// without letting the prompt run away.</summary>
    public const int MaxChars = 24_000;

    /// <summary>The preamble before the first "## " is not addressable. It is the part that says
    /// what this thing is, and rewriting it is a different act from revising a section - if that
    /// should be possible it should be its own decision, not a side effect of section numbering.
    /// </summary>
    public static RevisionResult Apply(string current, string section, string newText)
    {
        var heading = (section ?? string.Empty).Trim().TrimStart('#').Trim();
        if (heading.Length == 0)
            return new RevisionResult(false, "No section was named.", null, false);

        if (heading.Contains('\n') || heading.Contains('\r'))
            return new RevisionResult(false, "A section heading must be a single line.", null, false);

        var body = (newText ?? string.Empty).Trim();
        if (body.Length == 0)
        {
            return new RevisionResult(
                false,
                "The new text was empty. Sections are rewritten, never deleted - if a section no "
                + "longer applies, say what is true now instead.",
                null,
                false);
        }

        // Normalised before splitting so a CRLF file does not leave stray \r on every heading test.
        var lines = current.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        var end = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsSectionHeading(lines[i]))
                continue;

            if (start >= 0)
            {
                end = i;
                break;
            }

            if (HeadingText(lines[i]).Equals(heading, StringComparison.OrdinalIgnoreCase))
                start = i;
        }

        var replacement = $"## {heading}\n\n{body}\n";
        string text;

        if (start < 0)
        {
            text = current.TrimEnd() + "\n\n" + replacement;
        }
        else
        {
            var before = string.Join('\n', lines[..start]).TrimEnd();
            var after = string.Join('\n', lines[end..]).TrimStart('\n');

            text = before.Length == 0
                ? replacement + (after.Length > 0 ? "\n" + after : string.Empty)
                : before + "\n\n" + replacement + (after.Length > 0 ? "\n" + after : string.Empty);
        }

        if (!text.EndsWith('\n'))
            text += "\n";

        if (text.Length > MaxChars)
        {
            return new RevisionResult(
                false,
                $"That would make the file {text.Length} characters, over the {MaxChars} limit. "
                + "SELF.md is prepended to every turn, so it has to stay short. Say it in fewer words.",
                null,
                false);
        }

        return new RevisionResult(true, null, text, start < 0);
    }

    /// <summary>Only "## " counts. Deeper headings live inside a section and must not end one, and
    /// the single "# " title is the preamble, which is not addressable.</summary>
    private static bool IsSectionHeading(string line)
        => line.StartsWith("## ", StringComparison.Ordinal);

    private static string HeadingText(string line)
        => line[3..].Trim();
}
