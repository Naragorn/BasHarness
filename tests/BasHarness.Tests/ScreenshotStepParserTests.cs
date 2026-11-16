using BasHarness.Core;
using Xunit;

namespace BasHarness.Tests;

public class ScreenshotStepParserTests
{
    [Fact]
    public void Plain_screenshot_takes_only_a_name()
    {
        var s = ScenarioParser.Parse("screenshot arena");

        Assert.Empty(s.Errors);
        Assert.Empty(s.Steps[0].Named);
    }

    [Fact]
    public void Screenshot_of_the_npc_takes_camera_placement()
    {
        var s = ScenarioParser.Parse("screenshot down of=npc distance=2 height=1.5 angle=90");

        Assert.Empty(s.Errors);
        Assert.Equal("npc", s.Steps[0].Named["of"]);
        Assert.Equal("2", s.Steps[0].Named["distance"]);
        Assert.Equal("1.5", s.Steps[0].Named["height"]);
        Assert.Equal("90", s.Steps[0].Named["angle"]);
    }

    [Theory]
    [InlineData("screenshot x of=player", "of: 'player' is not one of npc")]
    [InlineData("screenshot x of=npc distance=far", "distance: 'far' is not a number")]
    [InlineData("screenshot x of=npc height=up", "height: 'up' is not a number")]
    [InlineData("screenshot x of=npc angle=left", "angle: 'left' is not a number")]
    [InlineData("screenshot x distance=2", "distance, height and angle need of=npc")]
    public void Bad_screenshot_arguments_are_errors(string line, string expected)
    {
        var s = ScenarioParser.Parse(line);

        Assert.Contains(s.Errors, e => e.Contains(expected));
    }
}
