using System.Linq;
using BasHarness.Core;
using Xunit;

namespace BasHarness.Tests;

public class CombatStepParserTests
{
    [Fact]
    public void CombatVerbs_ParseWithTheirArguments()
    {
        var s = ScenarioParser.Parse(string.Join("\n",
            "spawn-npc HumanMale distance=3 angle=180 brain=HumanMedium",
            "run 1",
            "expect-player-speed 2.5",
            "expect-player-speed 0 0.5",
            "hit-npc RightHand velocity=6 damage=1 push=0",
            "expect-npc staggered true within=1.5",
            "expect-npc stagger Full",
            "stop-run",
            "turn-player 180",
            "turn-player -90"));

        Assert.Empty(s.Errors);
        Assert.Equal(new[] { "spawn-npc", "run", "expect-player-speed", "expect-player-speed", "hit-npc", "expect-npc", "expect-npc", "stop-run", "turn-player", "turn-player" },
            s.Steps.Select(x => x.Verb));
        Assert.Equal("HumanMedium", s.Steps[0].Named["brain"]);
        Assert.Equal("1.5", s.Steps[5].Named["within"]);
    }

    [Theory]
    [InlineData("spawn-npc", "expects 1")]
    [InlineData("spawn-npc HumanMale distance=far", "distance: 'far' is not a number")]
    [InlineData("spawn-npc HumanMale angle=x", "angle: 'x' is not a number")]
    [InlineData("spawn-npc HumanMale colour=red", "unknown key 'colour'")]
    [InlineData("run", "expects 1")]
    [InlineData("run fast", "is not a number")]
    [InlineData("run 1.5", "between 0 and 1")]
    [InlineData("run -0.1", "between 0 and 1")]
    [InlineData("stop-run now", "expects 0")]
    [InlineData("expect-player-speed", "expects 1-2")]
    [InlineData("expect-player-speed x", "is not a number")]
    [InlineData("expect-player-speed 3 2", "max 2 is below min 3")]
    [InlineData("turn-player", "expects 1")]
    [InlineData("turn-player left", "'left' is not a number")]
    [InlineData("hit-npc", "expects 1")]
    [InlineData("hit-npc Head velocity=x", "velocity: 'x' is not a number")]
    [InlineData("hit-npc Head push=1.5", "push: '1.5' is not a whole number")]
    [InlineData("expect-npc staggered", "expects 2")]
    [InlineData("expect-npc mood happy", "unknown NPC property 'mood'")]
    [InlineData("expect-npc staggered true within=-1", "within: '-1' is not a number of seconds >= 0")]
    public void CombatVerbErrors_NameTheProblem(string line, string expected)
    {
        var s = ScenarioParser.Parse("wait-level\n" + line);

        var error = Assert.Single(s.Errors);
        Assert.StartsWith("line 2:", error);
        Assert.Contains(expected, error);
    }
}

public class CombatMathTests
{
    [Theory]
    [InlineData(0, 1, 0, 2, 0, 2)]     // straight ahead
    [InlineData(0, 1, 180, 2, 0, -2)]  // behind
    [InlineData(0, 1, 90, 1, 1, 0)]    // to the right (clockwise, as Unity's yaw)
    [InlineData(1, 0, 90, 1, 0, -1)]   // facing +x, right is -z
    public void OffsetXZ_TurnsTheForwardDirectionClockwise(float fx, float fz, float angle, float distance, float ex, float ez)
    {
        var (x, z) = CombatMath.OffsetXZ(fx, fz, angle, distance);

        Assert.Equal(ex, x, 3);
        Assert.Equal(ez, z, 3);
    }

    [Fact]
    public void OffsetXZ_NormalisesTheForwardDirection()
    {
        var (x, z) = CombatMath.OffsetXZ(0, 5, 0, 2);

        Assert.Equal(0, x, 3);
        Assert.Equal(2, z, 3);
    }

    [Fact]
    public void OffsetXZ_WithoutAForwardDirection_UsesWorldForward()
    {
        var (x, z) = CombatMath.OffsetXZ(0, 0, 0, 2);

        Assert.Equal(0, x, 3);
        Assert.Equal(2, z, 3);
    }

    [Theory]
    [InlineData(3.0, 2.5, null, true)]
    [InlineData(2.0, 2.5, null, false)]
    [InlineData(0.2, 0.0, 0.5, true)]
    [InlineData(0.6, 0.0, 0.5, false)]
    [InlineData(2.5, 2.5, 2.5, true)]
    public void InRange_MinInclusive_MaxOptionalInclusive(double value, double min, double? max, bool expected)
    {
        Assert.Equal(expected, CombatMath.InRange((float)value, (float)min, max.HasValue ? (float?)max.Value : null));
    }

    [Theory]
    [InlineData("staggered", "true", "True", true)]
    [InlineData("staggered", "false", "True", false)]
    [InlineData("stagger", "full", "Full", true)]
    [InlineData("state", "Destabilized", "Alive", false)]
    public void NpcProperty_MatchesCaseInsensitively(string property, string expected, string actual, bool ok)
    {
        Assert.True(NpcProperty.IsKnown(property));
        Assert.Equal(ok, NpcProperty.Matches(expected, actual));
    }

    [Fact]
    public void NpcProperty_UnknownNames_AreRejected()
    {
        Assert.False(NpcProperty.IsKnown("mood"));
        Assert.False(NpcProperty.IsKnown(null));
    }
}
