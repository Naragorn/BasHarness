using BasHarness.Core;
using Xunit;

namespace BasHarness.Tests;

public class FilmFramesTests
{
    [Theory]
    [InlineData(0.0, 60, 0)]
    [InlineData(0.016, 60, 0)]
    [InlineData(0.0167, 60, 1)]
    [InlineData(1.0, 30, 30)]
    [InlineData(-0.5, 30, 0)]
    [InlineData(1.0, 0, 0)]
    public void Slot_IsTheFrameOfThatMoment(double elapsed, int fps, int slot) => Assert.Equal(slot, FilmFrames.Slot(elapsed, fps));

    [Fact]
    public void First_Picture_IsWrittenOnceForSlotZero() => Assert.Equal(1, FilmFrames.Writes(0, -1));

    [Fact]
    public void NextSlot_IsWrittenOnce() => Assert.Equal(1, FilmFrames.Writes(5, 4));

    [Fact]
    public void SkippedSlots_RepeatThePicture() => Assert.Equal(3, FilmFrames.Writes(7, 4));

    [Fact]
    public void ASecondPictureInTheSameSlot_IsDropped()
    {
        Assert.Equal(0, FilmFrames.Writes(4, 4));
        Assert.Equal(0, FilmFrames.Writes(3, 4));
    }

    [Fact]
    public void Sidecar_IsOneJsonObject()
    {
        Assert.Equal(
            "{\"name\":\"twist\",\"width\":640,\"height\":360,\"fps\":60,\"views\":[\"side\",\"first\"],\"frames\":120,\"repeated\":3}",
            FilmFrames.Sidecar("twist", 640, 360, 60, new[] { "side", "first" }, 120, 3));
        Assert.Contains("\"name\":\"a\\\"b\"", FilmFrames.Sidecar("a\"b", 1, 1, 1, new string[0], 0, 0));
    }
}
