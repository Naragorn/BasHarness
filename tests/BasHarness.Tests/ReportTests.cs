using BasHarness.Core;
using Xunit;

namespace BasHarness.Tests;

public class ReportTests
{
    [Fact]
    public void StepLine_HasAStableParseableShape()
    {
        Assert.Equal(
            "[BasHarness] STEP 3/7 PASS line 12: wait 1",
            Report.StepLine(3, 7, true, 12, "wait 1", null));
        Assert.Equal(
            "[BasHarness] STEP 4/7 FAIL line 13: expect-log x -- not found within 10s",
            Report.StepLine(4, 7, false, 13, "expect-log x", "not found within 10s"));
    }

    [Theory]
    [InlineData(7, 7, 0, "[BasHarness] RESULT PASS 7/7")]
    [InlineData(3, 7, 1, "[BasHarness] RESULT FAIL 3/7")]
    [InlineData(0, 0, 1, "[BasHarness] RESULT FAIL 0/0")]
    public void ResultLine(int passed, int total, int failed, string expected)
    {
        Assert.Equal(expected, Report.ResultLine(passed, total, failed));
    }

    [Fact]
    public void Multiline_DetailsAreFlattened_SoTheRunnerSeesOneLine()
    {
        Assert.Equal(
            "[BasHarness] STEP 1/1 FAIL line 1: quit -- a | b",
            Report.StepLine(1, 1, false, 1, "quit", "a\nb"));
    }
}
