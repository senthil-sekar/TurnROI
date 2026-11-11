using System.Text.RegularExpressions;

namespace TurnRoi.Core.Analysis;

/// <summary>
/// Decides whether a human prompt pushes back on the previous turn (so that turn's spend was likely wasted).
/// Cues are checked on the opening of the prompt, where people put the verdict; polite openers ("no worries",
/// "no need to …") and instructions that merely start with a cue word ("stop the dev server", "no tests exist for X")
/// are excluded. A pasted build error or stack trace counts only when the previous turn changed code.
/// Measure changes against hand labels with <c>turnroi eval</c>.
/// </summary>
public static class Pushback
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Not pushback even though they open with a cue word.
    private static readonly Regex Polite = new(
        @"^\s*(no (problem|worries|rush|need|pressure|hurry|thanks)|nope,? (that'?s|looks|all) (good|fine|great)|no,? (that'?s|it'?s|looks) (fine|good|great|perfect|ok)|stop (the|a|all|any|your) (server|process|container|job|watcher|service|app|build))\b",
        Opts);

    // Verdicts at the start of the prompt.
    private static readonly (string Cue, Regex Pattern)[] Openers =
    [
        ("interrupted", new(@"^\s*\[Request interrupted by user", Opts)),
        ("no", new(@"^\s*(no+|nope|nah)\s*([,.!;:—-]|$|\s+(that|this|it|you|not|don'?t|wait|stop|please|revert|undo|wrong)\b)", Opts)),
        ("wrong", new(@"^\s*(wrong|incorrect|not (right|correct|quite)|nope)\b", Opts)),
        ("revert", new(@"^\s*(revert|undo|roll ?back|put (it|that|them) back|go back to)\b", Opts)),
        ("stop", new(@"^\s*stop\s*([,.!]|$|\s+(it|that|this|please|now|doing|\w+ing)\b)", Opts)),
        ("wait", new(@"^\s*(wait|hold on|hang on)\s*[,.!]", Opts)),
        ("wtf", new(@"^\s*(wtf|ugh|argh|seriously)\b", Opts)),
    ];

    // Verdicts anywhere in the first ~200 characters.
    private static readonly (string Cue, Regex Pattern)[] Anywhere =
    [
        ("not-wrong", new(@"\b(that|this|it)('?s| is| was) (not (right|correct|what i (asked|wanted|meant))|wrong|broken|worse)\b", Opts)),
        ("didnt-work", new(@"\b(that|this|it|the fix|your (fix|change))\s+(didn'?t|did not|doesn'?t|does not|won'?t|isn'?t) (work|help|fix|compile|build|run|pass)", Opts)),
        ("still", new(@"\b(it'?s |it is |tests? (are |is )?)?still (not\b|broken|failing|fails|wrong|the same|crash|throw|red)", Opts)),
        ("same-error", new(@"\b(same|another|new) (error|exception|failure|problem|issue)\b", Opts)),
        ("you-broke", new(@"\byou (broke|missed|forgot|ignored|deleted|removed|changed|overwrote|didn'?t)\b", Opts)),
        ("why-did-you", new(@"\bwhy (did|would) you\b", Opts)),
        ("i-said", new(@"\b(i (said|told you|asked you|asked for|meant)|not what i (asked|wanted|meant)|i didn'?t ask)\b", Opts)),
        ("dont", new(@"\b(please )?(don'?t|do not|never) (do that|change|touch|modify|delete|remove|add|rewrite|use)\b", Opts)),
        ("try-again", new(@"\b(try again|that'?s not it|nope)\b", Opts)),
        ("breaks", new(@"\b(now|this) (breaks|broke|fails|crashes|throws)\b", Opts)),
        ("doesnt-compile", new(@"\b(doesn'?t|does not|won'?t|no longer) (compile|build|run|start|work)\b", Opts)),
    ];

    // A pasted failure: compiler errors, stack traces, failing test summaries.
    private static readonly Regex ErrorPaste = new(
        @"(error (CS|TS|NU|MSB)\d{3,5}|\berror\[E\d{4}\]|Traceback \(most recent call last\)|^\s+at [\w.<>$`]+\(|Unhandled exception|npm ERR!|FAILED|Failed!\s+-\s+Failed:|\b\w+(Exception|Error): )",
        Opts | RegexOptions.Multiline);

    /// <summary>The matched cue, or null when the prompt doesn't push back.</summary>
    public static string? Detect(string prompt, bool previousTurnChangedCode = false)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var head = prompt.Length > 200 ? prompt[..200] : prompt;
        if (Polite.IsMatch(head)) return null;

        foreach (var (cue, pattern) in Openers)
            if (pattern.IsMatch(head)) return cue;
        foreach (var (cue, pattern) in Anywhere)
            if (pattern.IsMatch(head)) return cue;

        if (previousTurnChangedCode && ErrorPaste.IsMatch(prompt.Length > 4000 ? prompt[..4000] : prompt)) return "error-paste";
        return null;
    }
}
