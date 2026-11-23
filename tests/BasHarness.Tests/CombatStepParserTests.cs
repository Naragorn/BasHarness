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
            "spawn-npc HumanMale distance=3 angle=180 brain=HumanMedium facing=180 container=CivilianMeleeSword",
            "run 1",
            "expect-player-speed 2.5",
            "expect-player-speed 0 0.5",
            "hit-npc RightHand velocity=6 damage=1 push=0 type=Pierce by=thrown item=DaggerCommon swing=0",
            "expect-npc staggered true within=1.5",
            "expect-npc stagger Full",
            "stop-run",
            "turn-player 180",
            "turn-player -90",
            "player-invincible true",
            "player-grab SwordShortCommon hand=left",
            "player-parry offset=0.4 swing=5",
            "player-push 0.15 2.2 back=false",
            "player-release hand=right",
            "player-strike-weapon twist=600 seconds=0.3 approach=0.3 settle=0.2 phase=ready",
            "record-start twist fps=60 width=640 height=360 views=side,first,npc,hand distance=2 rise=0.6 angle=90",
            "record-stop",
            "frame-rate 90",
            "npc-grab DaggerCommon",
            "npc-grab SwordShortCommon hand=left",
            "expect-npc holding true",
            "expect-npc brain HumanDummy"));

        Assert.Empty(s.Errors);
        Assert.Equal(new[] { "spawn-npc", "run", "expect-player-speed", "expect-player-speed", "hit-npc", "expect-npc", "expect-npc", "stop-run", "turn-player", "turn-player", "player-invincible", "player-grab", "player-parry", "player-push", "player-release", "player-strike-weapon", "record-start", "record-stop", "frame-rate", "npc-grab", "npc-grab", "expect-npc", "expect-npc" },
            s.Steps.Select(x => x.Verb));
        Assert.Equal("HumanMedium", s.Steps[0].Named["brain"]);
        Assert.Equal("1.5", s.Steps[5].Named["within"]);
        Assert.Equal("180", s.Steps[0].Named["facing"]);
        Assert.Equal("CivilianMeleeSword", s.Steps[0].Named["container"]);
        Assert.Equal("Pierce", s.Steps[4].Named["type"]);
        Assert.Equal("thrown", s.Steps[4].Named["by"]);
        Assert.Equal("0", s.Steps[4].Named["swing"]);
    }

    [Fact]
    public void HitNpc_TakesAKick_AndMayHitADeadNpc()
    {
        var s = ScenarioParser.Parse("wait-level\nhit-npc Torso by=foot velocity=9 dead=true");

        Assert.Empty(s.Errors);
        Assert.Equal("foot", s.Steps[1].Named["by"]);
        Assert.Equal("true", s.Steps[1].Named["dead"]);
    }

    [Fact]
    public void HitNpc_TakesASectionAndAContactOffset()
    {
        var s = ScenarioParser.Parse("wait-level\nhit-npc Torso section=Lower at=0,-0.08,0.08\nhit-npc Neck at=0.01,0.05,0.05 section=Full");

        Assert.Empty(s.Errors);
        Assert.Equal("Lower", s.Steps[1].Named["section"]);
        Assert.Equal("0,-0.08,0.08", s.Steps[1].Named["at"]);
        Assert.True(ScenarioParser.TryParseVector(s.Steps[1].Named["at"], out var x, out var y, out var z));
        Assert.Equal((0f, -0.08f, 0.08f), (x, y, z));
    }

    [Theory]
    [InlineData("1,2")]
    [InlineData("1,2,3,4")]
    [InlineData("a,0,0")]
    [InlineData("0,,0")]
    [InlineData("")]
    public void TryParseVector_RefusesAnythingButThreeNumbers(string text)
    {
        Assert.False(ScenarioParser.TryParseVector(text, out _, out _, out _));
    }

    [Fact]
    public void NpcAttack_AndPlayerGrabDropped_Parse()
    {
        var s = ScenarioParser.Parse("wait-level\nnpc-attack Attack\nnpc-attack end\nplayer-grab-dropped\nplayer-grab-dropped hand=left");

        Assert.Empty(s.Errors);
        Assert.Equal(new[] { "wait-level", "npc-attack", "npc-attack", "player-grab-dropped", "player-grab-dropped" }, s.Steps.Select(x => x.Verb));
        Assert.Equal("left", s.Steps[4].Named["hand"]);
    }

    [Fact]
    public void PlayerGrabNpc_AndPlayerSlam_Parse()
    {
        var s = ScenarioParser.Parse("wait-level\nplayer-grab-npc RightHand\nplayer-grab-npc LeftArm section=Lower hand=left\nplayer-slam 0,-1.2,0.2 speed=6 hold=0.5 back=false\nplayer-slam 0,-1,0");

        Assert.Empty(s.Errors);
        Assert.Equal(new[] { "wait-level", "player-grab-npc", "player-grab-npc", "player-slam", "player-slam" }, s.Steps.Select(x => x.Verb));
        Assert.Equal("left", s.Steps[2].Named["hand"]);
        Assert.Equal("Lower", s.Steps[2].Named["section"]);
        Assert.Equal("0,-1.2,0.2", s.Steps[3].Args[0]);
        Assert.Equal("6", s.Steps[3].Named["speed"]);
    }

    [Fact]
    public void PlayerEyes_Parses()
    {
        var s = ScenarioParser.Parse("wait-level\nplayer-eyes");

        Assert.Empty(s.Errors);
        Assert.Equal("player-eyes", s.Steps[1].Verb);
    }

    [Fact]
    public void PlaceBlock_Parses()
    {
        var s = ScenarioParser.Parse("wait-level\nplace-block 0,-0.35,0\nplace-block 0.1,-0.4,0.05 size=0.5\nplace-block 0,-0.1,0 size=0.12 from=npc-hand\nplace-block 0,-0.4,0 from=held");

        Assert.Empty(s.Errors);
        Assert.Equal("0.5", s.Steps[2].Named["size"]);
        Assert.Equal("npc-hand", s.Steps[3].Named["from"]);
    }

    [Fact]
    public void PlayerStab_Parses()
    {
        var s = ScenarioParser.Parse("wait-level\nplayer-stab Torso\nplayer-stab Head section=Upper speed=3 depth=0.2 from=0.4 step=false");

        Assert.Empty(s.Errors);
        Assert.Equal(new[] { "wait-level", "player-stab", "player-stab" }, s.Steps.Select(x => x.Verb));
        Assert.Equal("Torso", s.Steps[1].Args[0]);
        Assert.Equal("Upper", s.Steps[2].Named["section"]);
        Assert.Equal("3", s.Steps[2].Named["speed"]);
        Assert.Equal("0.2", s.Steps[2].Named["depth"]);
        Assert.Equal("0.4", s.Steps[2].Named["from"]);
        Assert.Equal("false", s.Steps[2].Named["step"]);
    }

    [Theory]
    [InlineData("player-stab", "expects 1")]
    [InlineData("player-stab Torso section=Hips", "section: 'Hips' is not one of Full, Lower, Mid, Upper")]
    [InlineData("player-stab Torso speed=0", "speed: '0' is not a number above 0")]
    [InlineData("player-stab Torso depth=deep", "depth: 'deep' is not a number above 0")]
    [InlineData("player-stab Torso from=0", "from: '0' is not a number above 0")]
    [InlineData("player-stab Torso step=maybe", "step: 'maybe' is not one of true, false")]
    [InlineData("player-stab Torso hand=left", "unknown key 'hand'")]
    public void PlayerStabErrors_NameTheProblem(string line, string expected)
    {
        var s = ScenarioParser.Parse("wait-level\n" + line);

        var error = Assert.Single(s.Errors);
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData("player-grab-npc", "expects 1")]
    [InlineData("player-grab-npc RightHand hand=both", "hand: 'both' is not one of left, right")]
    [InlineData("player-grab-npc RightHand section=Hips", "section: 'Hips' is not one of Full, Lower, Mid, Upper")]
    [InlineData("player-eyes 1.6", "expects 0")]
    [InlineData("place-block", "expects 1")]
    [InlineData("place-block below", "'below' is not three numbers right,up,forward")]
    [InlineData("place-block 0,-0.1,0 from=floor", "from: 'floor' is not one of held, npc-hand")]
    [InlineData("place-block 0,-0.3,0 size=0", "size: '0' is not a number above 0")]
    [InlineData("place-block 0,-0.3,0 size=big", "size: 'big' is not a number above 0")]
    [InlineData("player-slam", "expects 1")]
    [InlineData("player-slam down", "'down' is not three numbers right,up,forward")]
    [InlineData("player-slam 0,0,0", "the move is 0 m long")]
    [InlineData("player-slam 0,-1,0 speed=fast", "speed: 'fast' is not a number above 0")]
    [InlineData("player-slam 0,-1,0 speed=0", "speed: '0' is not a number above 0")]
    [InlineData("player-slam 0,-1,0 back=maybe", "back: 'maybe' is not one of true, false")]
    [InlineData("player-slam 0,-1,0 hold=-1", "hold: '-1' is not a number of seconds >= 0")]
    public void GrabAndSlamErrors_NameTheProblem(string line, string expected)
    {
        var s = ScenarioParser.Parse("wait-level\n" + line);

        var error = Assert.Single(s.Errors);
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData("npc-attack", "expects 1")]
    [InlineData("npc-attack Swing", "'Swing' is not one of WindUp, Attack, FollowThrough, End, Cancel")]
    [InlineData("player-grab-dropped now", "expects 0")]
    [InlineData("player-grab-dropped hand=both", "hand: 'both' is not one of left, right")]
    [InlineData("spawn-npc", "expects 1")]
    [InlineData("spawn-npc HumanMale distance=far", "distance: 'far' is not a number")]
    [InlineData("spawn-npc HumanMale angle=x", "angle: 'x' is not a number")]
    [InlineData("spawn-npc HumanMale colour=red", "unknown key 'colour'")]
    [InlineData("spawn-npc HumanMale facing=back", "facing: 'back' is not a number")]
    [InlineData("run", "expects 1")]
    [InlineData("run fast", "is not a number")]
    [InlineData("run 1.5", "between 0 and 1")]
    [InlineData("run -0.1", "between 0 and 1")]
    [InlineData("stop-run now", "expects 0")]
    [InlineData("expect-player-speed", "expects 1-2")]
    [InlineData("expect-player-speed x", "is not a number")]
    [InlineData("expect-player-speed 3 2", "max 2 is below min 3")]
    [InlineData("hit-npc Torso swing=fast", "swing: 'fast' is not a number")]
    [InlineData("hit-npc Torso at=0,1", "at: '0,1' is not three numbers right,up,forward")]
    [InlineData("hit-npc Torso section=Hips", "section: 'Hips' is not one of Full, Lower, Mid, Upper")]
    [InlineData("player-grab", "expects 1")]
    [InlineData("player-grab SwordShortCommon hand=both", "hand: 'both' is not one of left, right")]
    [InlineData("player-parry now", "expects 0")]
    [InlineData("player-parry offset=x", "offset: 'x' is not a number")]
    [InlineData("player-parry swing=hard", "swing: 'hard' is not a number")]
    [InlineData("player-release now", "expects 0")]
    [InlineData("player-strike-weapon twist=fast", "twist: 'fast' is not a number")]
    [InlineData("record-start", "expects 1")]
    [InlineData("record-start x fps=fast", "fps: 'fast'")]
    [InlineData("record-start x views=side,top", "views: 'side,top' is not a list of side, first, npc, hand")]
    [InlineData("record-start x rise=up", "rise: 'up' is not a number")]
    [InlineData("record-stop now", "expects 0")]
    [InlineData("frame-rate", "expects 1")]
    [InlineData("frame-rate fast", "'fast' is not a whole number of frames per second above 0")]
    [InlineData("frame-rate 0", "'0' is not a whole number of frames per second above 0")]
    [InlineData("player-strike-weapon seconds=-1", "seconds: '-1' is not a number of seconds >= 0")]
    [InlineData("player-strike-weapon settle=soon", "settle: 'soon' is not a number of seconds >= 0")]
    [InlineData("player-strike-weapon depth=deep", "depth: 'deep' is not a number")]
    [InlineData("player-strike-weapon phase=later", "phase: 'later' is not one of all, ready, strike")]
    [InlineData("player-release hand=both", "hand: 'both' is not one of left, right")]
    [InlineData("player-push 0.1", "expects 2")]
    [InlineData("player-push far 2", "'far' is not a number of metres")]
    [InlineData("player-push 0.1 -1", "'-1' is not a number of seconds >= 0")]
    [InlineData("player-push 0.1 2 back=maybe", "back: 'maybe' is not one of true, false")]
    [InlineData("turn-player", "expects 1")]
    [InlineData("player-invincible", "expects 1")]
    [InlineData("player-invincible yes", "'yes' is not one of true, false")]
    [InlineData("npc-tk-throw now", "expects 0")]
    [InlineData("npc-tk-throw speed=far", "speed: 'far' is not a number")]
    [InlineData("player-cast", "expects 1")]
    [InlineData("player-cast Ice", "'Ice' is not one of Fire, Lightning, Gravity")]
    [InlineData("player-cast Fire speed=fast", "speed: 'fast' is not a number")]
    [InlineData("player-cast Fire hand=both", "hand: 'both' is not one of left, right")]
    [InlineData("player-skill MightyKick", "expects 2")]
    [InlineData("player-skill MightyKick bought", "'bought' is not one of true, false")]
    [InlineData("turn-player left", "'left' is not a number")]
    [InlineData("npc-grab", "expects 1")]
    [InlineData("npc-grab DaggerCommon hand=both", "hand: 'both' is not one of left, right")]
    [InlineData("hit-npc", "expects 1")]
    [InlineData("hit-npc Torso by=elbow", "by: 'elbow' is not one of hand, held, thrown, spell, foot")]
    [InlineData("hit-npc Torso dead=maybe", "dead: 'maybe' is not one of true, false")]
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

    [Fact]
    public void PlayerCast_TakesAnElement_AndOptionalSpeedAndHand()
    {
        var s = ScenarioParser.Parse("wait-level\nplayer-cast Fire speed=12\nplayer-cast Lightning hand=left\nplayer-cast Gravity");

        Assert.Empty(s.Errors);
        Assert.Equal(new[] { "Fire", "Lightning", "Gravity" }, s.Steps.Skip(1).Select(x => x.Args[0]));
        Assert.Equal("12", s.Steps[1].Named["speed"]);
        Assert.Equal("left", s.Steps[2].Named["hand"]);
    }

    [Fact]
    public void NpcTkThrow_TakesAnOptionalSpeed()
    {
        var s = ScenarioParser.Parse("wait-level\nnpc-tk-throw\nnpc-tk-throw speed=6");

        Assert.Empty(s.Errors);
        Assert.Equal("6", s.Steps[2].Named["speed"]);
    }

    [Fact]
    public void PlayerSkill_TakesASkillIdAndTrueOrFalse()
    {
        var s = ScenarioParser.Parse("wait-level\nplayer-skill MightyKick true\nplayer-skill DMCS_Parry false");

        Assert.Empty(s.Errors);
        Assert.Equal(new[] { "player-skill", "player-skill" }, s.Steps.Skip(1).Select(x => x.Verb));
        Assert.Equal(new[] { "DMCS_Parry", "false" }, s.Steps[2].Args);
    }
}

public class CombatMathTests
{
    [Theory]
    [InlineData(90, 0, 90)]
    [InlineData(90, 180, 270)]
    [InlineData(270, 180, 90)]
    [InlineData(10, -30, 340)]
    public void FacingYaw_TurnsFromLookingAtThePlayer(float lookAtPlayer, float facing, float expected)
    {
        Assert.Equal(expected, CombatMath.FacingYaw(lookAtPlayer, facing), 3);
    }

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

    [Theory]
    [InlineData(1.2f, 6f, 0f, 0f)]       // start
    [InlineData(1.2f, 6f, 0.1f, 0.5f)]   // halfway after 0.1 s at 6 m/s
    [InlineData(1.2f, 6f, 0.2f, 1f)]     // there
    [InlineData(1.2f, 6f, 0.5f, 1f)]     // stays there
    [InlineData(1.2f, 6f, -1f, 0f)]      // clock before the start
    [InlineData(0f, 6f, 0.1f, 1f)]       // no way to go
    public void MoveShare_IsTheShareOfTheWayCoveredAtThatSpeed(float metres, float speed, float elapsed, float expected)
    {
        Assert.Equal(expected, CombatMath.MoveShare(metres, speed, elapsed), 3);
    }

    [Fact]
    public void MoveSeconds_IsTheWayOverTheSpeed()
    {
        Assert.Equal(0.2f, CombatMath.MoveSeconds(1.2f, 6f), 3);
        Assert.Equal(0f, CombatMath.MoveSeconds(0f, 6f), 3);
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
    [InlineData("tree", "false", "False", true)]
    [InlineData("tree", "False", "True", false)]
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
