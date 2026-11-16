using BasHarness.Core;
using Xunit;

namespace BasHarness.Tests;

public class ValueMatcherTests
{
    private static readonly OptionValue[] BoolValues = { new("Disabled", "False"), new("Enabled", "True") };
    private static readonly OptionValue[] Floats = { new("0.00", "0"), new("0.25", "0.25"), new("0.30", "0.3") };

    [Theory]
    [InlineData("true", 1)]      // value text, case-insensitive
    [InlineData("False", 0)]
    [InlineData("enabled", 1)]   // title, case-insensitive
    [InlineData("Disabled", 0)]
    [InlineData("maybe", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    public void Bools_MatchValueOrTitle(string wanted, int expected)
    {
        Assert.Equal(expected, ValueMatcher.IndexOf(BoolValues, wanted));
    }

    [Theory]
    [InlineData("0.3", 2)]
    [InlineData("0.30", 2)]       // numeric compare, not string compare
    [InlineData("0.25", 1)]
    [InlineData("0.2999999", 2)]  // float noise tolerated
    [InlineData("0.5", -1)]
    public void Floats_CompareNumerically(string wanted, int expected)
    {
        Assert.Equal(expected, ValueMatcher.IndexOf(Floats, wanted));
    }

    [Fact]
    public void ValueTextWins_OverTitle()
    {
        // A title that looks like another entry's value must not shadow the exact value match.
        var values = new[] { new OptionValue("1", "0"), new OptionValue("one", "1") };

        Assert.Equal(1, ValueMatcher.IndexOf(values, "1"));
    }

    [Fact]
    public void NullList_ReturnsMinusOne()
    {
        Assert.Equal(-1, ValueMatcher.IndexOf(null, "x"));
    }

    [Theory]
    [InlineData(0, 1, 0)]  // single-value button: wraps to itself, like UIList.NextValue
    [InlineData(0, 2, 1)]
    [InlineData(1, 2, 0)]
    [InlineData(-1, 3, 0)] // unknown current -> first
    [InlineData(0, 0, -1)] // nothing to press
    public void NextIndex_MirrorsButtonListWrap(int current, int count, int expected)
    {
        Assert.Equal(expected, ValueMatcher.NextIndex(current, count));
    }
}
