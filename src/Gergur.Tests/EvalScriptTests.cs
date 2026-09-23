using System.Diagnostics;
using Gergur.Tabs;
using Xunit;

namespace Gergur.Tests;

/// <summary>
/// /eval pastes the caller's source into two generated scripts: a probe that decides
/// whether it is an expression without running it, and a wrapper that awaits it. Both
/// are text built by string concatenation, and a wrapper that does not parse does not
/// fail loudly: the page never posts a result back and the call reports a script that
/// never settled, fifteen seconds later, for every single evaluation.
///
/// So these hand the generated text to a real JavaScript parser rather than reading it.
/// Asserting on the shape of the string would pass against a missing bracket.
/// </summary>
public sealed class EvalScriptTests
{
    /// <summary>
    /// Parses with node, which checks.sh already needs and probes for. Returns null when
    /// the source parses, and the parser's complaint when it does not.
    /// </summary>
    private static string? SyntaxErrorIn(string javascript)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gergur-eval-{Guid.NewGuid():n}.js");
        File.WriteAllText(path, javascript);
        try
        {
            using var node = Process.Start(new ProcessStartInfo("node", $"--check \"{path}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(node);
            // Both pipes drained before waiting. Reading one to the end while the other
            // fills its buffer is the classic child-process deadlock, and nothing in
            // checks.sh puts a timeout around dotnet test, so it would hang the gate
            // rather than fail it.
            var errorText = node.StandardError.ReadToEndAsync();
            var outputText = node.StandardOutput.ReadToEndAsync();
            Assert.True(node.WaitForExit(30_000), "node did not finish parsing");
            Assert.True(Task.WaitAll([errorText, outputText], 5_000), "node did not close its pipes");
            return node.ExitCode == 0 ? null : errorText.Result;
        }
        finally
        {
            try { File.Delete(path); } catch { /* a temp file that outlives the run is not a failure */ }
        }
    }

    /// <summary>Runs the script under node and returns what it evaluated to, as text.</summary>
    private static string Evaluate(string javascript)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gergur-eval-{Guid.NewGuid():n}.js");
        File.WriteAllText(path, $"process.stdout.write(String({javascript}));");
        try
        {
            using var node = Process.Start(new ProcessStartInfo("node", $"\"{path}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(node);
            var printed = node.StandardOutput.ReadToEndAsync();
            var complaint = node.StandardError.ReadToEndAsync();
            Assert.True(node.WaitForExit(30_000), "node did not finish");
            Assert.True(Task.WaitAll([printed, complaint], 5_000), "node did not close its pipes");
            Assert.True(node.ExitCode == 0, complaint.Result);
            return printed.Result;
        }
        finally
        {
            try { File.Delete(path); } catch { /* a temp file that outlives the run is not a failure */ }
        }
    }

    /// <summary>
    /// Worked out once, not once per assertion, and it is a hard requirement rather than
    /// a reason to skip: these tests passing on a machine without node would be 22 green
    /// results proving nothing, which this project treats as worse than no test at all.
    /// checks.sh already requires node for the adblock suite.
    /// </summary>
    private static readonly Lazy<bool> Node = new(() =>
    {
        try { return SyntaxErrorIn("1") is null; }
        catch { return false; }
    });

    private static void RequireNode()
        => Assert.True(Node.Value, "node is needed to parse the generated scripts and was not found on PATH");

    public static TheoryData<string> Expressions() => new()
    {
        "document.title",
        "1 + 1",
        "fetch('/x').then(function (r) { return r.json(); })",
        "(async () => (await fetch('/x')).status)()",
        "{ a: 1 }",                       // an object literal, which is also a block
        "document.title // which page is this",
        "`a ${1} b`",
        "document.querySelectorAll('a').length",
        "\"a string with ) and } in it\"",
    };

    [Theory]
    [MemberData(nameof(Expressions))]
    public void TheProbeParsesForEveryExpressionWorthProbing(string js)
    {
        RequireNode();
        Assert.Null(SyntaxErrorIn(Tab.ExpressionProbe(js)));
    }

    [Theory]
    [MemberData(nameof(Expressions))]
    public void TheWrapperParsesForEveryExpressionWorthAwaiting(string js)
    {
        RequireNode();
        Assert.Null(SyntaxErrorIn(Tab.AwaitingWrapper(js, "abc123")));
    }

    [Fact]
    public void AnExpressionEndingInACommentDoesNotEatTheWrapper()
    {
        // The reason the source gets a line of its own. On one line, the // swallows the
        // brackets that close the function around it, and a perfectly good expression is
        // mistaken for statements on every call that ends in a comment.
        RequireNode();
        Assert.Null(SyntaxErrorIn(Tab.ExpressionProbe("location.href // where are we")));
        Assert.Null(SyntaxErrorIn(Tab.AwaitingWrapper("location.href // where are we", "abc123")));
    }

    [Theory]
    [InlineData("var a = 1; a + 1")]
    [InlineData("const t = document.title; t.length")]
    [InlineData("if (true) { 1 } else { 2 }")]
    public void StatementsFailTheProbeSoTheyRunTheWayTheyAlwaysDid(string js)
    {
        // Not a defect: this is the signal. A script of several statements cannot live
        // inside "return (...)", so the probe refuses to parse and /eval falls back to
        // running it exactly as ExecuteScriptAsync always has, completion value and all.
        RequireNode();
        Assert.NotNull(SyntaxErrorIn(Tab.ExpressionProbe(js)));
    }

    [Fact]
    public void TheProbeAndTheComparisonUseTheSameMarker()
    {
        // They used to be two copies of the same literal in two files. Change one and
        // every test here stays green while every evaluation silently stops being awaited,
        // because the probe's answer no longer matches what the caller compares it to.
        Assert.Contains(Tab.ExpressionMarker, Tab.ExpressionProbe("1"));
        Assert.Equal("\"" + Tab.ExpressionMarker + "\"", Tab.ExpressionMarkerJson);
    }

    [Fact]
    public void TheProbeAnswersItsMarkerForAnExpression()
    {
        // The whole decision rests on this string coming back, so run the probe for real
        // and read what it evaluates to rather than trusting that it says the right thing.
        RequireNode();
        Assert.Equal(Tab.ExpressionMarker, Evaluate(Tab.ExpressionProbe("1 + 1")));
    }

    [Fact]
    public void TheProbeNeverRunsWhatItIsParsing()
    {
        // The whole point of parsing rather than guessing from the text: a fetch or a
        // click must not happen twice because the browser had to work out which shape the
        // source was. Run the probe over something that would announce itself, and read
        // back whether it did. Asserting that the generated text contains the word
        // "unused" would pass against a probe that called it.
        RequireNode();
        string answer = Evaluate($$"""
            (function () {
                var ran = false;
                function sideEffect() { ran = true; return 1; }
                var marker = {{Tab.ExpressionProbe("sideEffect()")}};
                return marker + " ran=" + ran;
            })()
            """);

        Assert.Equal(Tab.ExpressionMarker + " ran=false", answer);
    }

    [Fact]
    public void TheTokenIsEmbeddedAsJsonRatherThanPasted()
    {
        // It is generated here and not by the caller, but a token pasted between quotes
        // is a habit that stops being safe the moment somebody passes one in.
        Assert.Contains("var token = \"abc123\";", Tab.AwaitingWrapper("1", "abc123"));
    }
}
