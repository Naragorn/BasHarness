using System.Linq;
using BasHarness.Core;
using Xunit;

namespace BasHarness.Tests;

public class ScenarioParserTests
{
    [Fact]
    public void SkipsBlankLinesAndComments_AndKeepsLineNumbers()
    {
        var s = ScenarioParser.Parse("# header\n\nwait-level\n  # indented comment\nquit\n");

        Assert.Empty(s.Errors);
        Assert.Equal(new[] { "wait-level", "quit" }, s.Steps.Select(x => x.Verb));
        Assert.Equal(new[] { 3, 5 }, s.Steps.Select(x => x.Line));
    }

    [Fact]
    public void QuotedPositionals_AndNamedArgs()
    {
        var s = ScenarioParser.Parse("option-exists DMCS \"Clash of Might Enabled\" category=\"Clash of Might\" display=Enabled");

        var step = Assert.Single(s.Steps);
        Assert.Equal(new[] { "DMCS", "Clash of Might Enabled" }, step.Args);
        Assert.Equal("Clash of Might", step.Named["category"]);
        Assert.Equal("Enabled", step.Named["display"]);
    }

    [Fact]
    public void RestVerbs_TakeTheWholeRemainderVerbatim_IncludingEqualsAndBrackets()
    {
        var s = ScenarioParser.Parse("expect-log [DMCS] v0.1.0 loaded  with Debug Logging=off");

        var step = Assert.Single(s.Steps);
        Assert.Equal("[DMCS] v0.1.0 loaded  with Debug Logging=off", step.Rest);
        Assert.Empty(step.Named);
    }

    [Fact]
    public void RestVerbs_StripOneOuterQuotePair()
    {
        var s = ScenarioParser.Parse("expect-no-log \"  spaced  \"");

        Assert.Equal("  spaced  ", Assert.Single(s.Steps).Rest);
    }

    [Fact]
    public void OptionalPositional_IsAccepted()
    {
        var s = ScenarioParser.Parse("wait-level\nwait-level 90");

        Assert.Empty(s.Errors);
        Assert.Equal(2, s.Steps.Count);
    }

    [Theory]
    [InlineData("fly-to-moon", "unknown verb")]
    [InlineData("wait", "expects 1")]
    [InlineData("wait 1 2", "expects 1")]
    [InlineData("set-option DMCS \"Debug Logging\"", "expects 3")]
    [InlineData("expect-log", "needs text")]
    [InlineData("option-exists DMCS X colour=red", "unknown key 'colour'")]
    [InlineData("press DMCS \"unterminated", "unterminated quote")]
    [InlineData("wait abc", "not a number")]
    [InlineData("wait-level -5", "not a number")]
    public void Errors_NameTheLineAndTheProblem(string line, string expected)
    {
        var s = ScenarioParser.Parse("wait-level\n" + line);

        var error = Assert.Single(s.Errors);
        Assert.StartsWith("line 2:", error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void EmptyScenario_IsAnError()
    {
        var s = ScenarioParser.Parse("# nothing\n");

        Assert.Contains("no steps", Assert.Single(s.Errors));
    }

    [Fact]
    public void NullText_IsAnError()
    {
        Assert.Single(ScenarioParser.Parse(null).Errors);
    }

    [Fact]
    public void WindowsLineEndings_AreHandled()
    {
        var s = ScenarioParser.Parse("wait 1\r\nquit\r\n");

        Assert.Empty(s.Errors);
        Assert.Equal("1", s.Steps[0].Args[0]);
    }

    [Fact]
    public void Describe_RendersAReadableStep()
    {
        var step = ScenarioParser.Parse("set-option DMCS \"Debug Logging\" true").Steps[0];

        Assert.Equal("set-option DMCS \"Debug Logging\" true", step.Describe());
    }
}

public class AwaitLogParserTests
{
    [Fact]
    public void AwaitLog_TakesSecondsThenText()
    {
        var s = ScenarioParser.Parse("await-log 30 : flash, sound DMCS 1");

        Assert.Empty(s.Errors);
        Assert.True(ScenarioParser.TrySplitAwait(s.Steps[0].Rest, out var seconds, out var text));
        Assert.Equal(30f, seconds);
        Assert.Equal(": flash, sound DMCS 1", text);
    }

    [Theory]
    [InlineData("soon : flash")]
    [InlineData("30")]
    [InlineData("-1 x")]
    public void AwaitLog_NeedsPositiveSecondsAndText(string rest)
    {
        Assert.False(ScenarioParser.TrySplitAwait(rest, out _, out _));
    }
}
