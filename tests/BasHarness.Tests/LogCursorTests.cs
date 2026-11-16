using BasHarness.Core;
using Xunit;

namespace BasHarness.Tests;

public class LogCursorTests
{
    [Fact]
    public void FindsTextAfterTheMark_Only()
    {
        var cursor = new LogCursor();
        const string before = "[DMCS] Debug Logging on\n";
        cursor.Mark(before.Length);

        Assert.False(cursor.ContainsSinceMark(before, "Debug Logging on"));
        Assert.True(cursor.ContainsSinceMark(before + "[DMCS] Debug Logging on\n", "Debug Logging on"));
    }

    [Fact]
    public void WithoutMark_SearchesEverything()
    {
        Assert.True(new LogCursor().ContainsSinceMark("abc", "b"));
    }

    [Fact]
    public void ShrunkLog_ResetsTheMark_InsteadOfThrowing()
    {
        var cursor = new LogCursor();
        cursor.Mark(1000);

        Assert.True(cursor.ContainsSinceMark("short text", "short"));
    }

    [Theory]
    [InlineData(null, "x", false)]
    [InlineData("x", null, false)]
    [InlineData("x", "", false)]
    public void NullOrEmpty_IsNoMatch(string log, string text, bool expected)
    {
        Assert.Equal(expected, new LogCursor().ContainsSinceMark(log, text));
    }

    [Fact]
    public void NegativeMark_IsClampedToZero()
    {
        var cursor = new LogCursor();
        cursor.Mark(-5);

        Assert.True(cursor.ContainsSinceMark("abc", "a"));
    }
}
