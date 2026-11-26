using BasHarness.Core;
using Xunit;

namespace BasHarness.Tests;

/// <summary>
/// The film's sound stays on the film's clock: when the game's mixer stalls (a long frame, a garbage collection), it hands
/// over fewer samples than the time that passed, and the sound after the stall would come early. The missing stretch is
/// filled with silence (2026-11-26: a ragdoll film of 16.33 s had only 15.23 s of sound).
/// </summary>
public class FilmSoundTests
{
    [Fact]
    public void OnTime_NeedsNoSilence() => Assert.Equal(0, FilmSound.SilenceBefore(48000, 1.0, 48000, 0.04));

    [Fact]
    public void Ahead_NeedsNoSilence() => Assert.Equal(0, FilmSound.SilenceBefore(49000, 1.0, 48000, 0.04));

    [Fact]
    public void BehindWithinTolerance_NeedsNoSilence() => Assert.Equal(0, FilmSound.SilenceBefore(48000 - 1900, 1.0, 48000, 0.04));

    [Fact]
    public void BehindBeyondTolerance_IsFilledUpToNow() => Assert.Equal(24000, FilmSound.SilenceBefore(24000, 1.0, 48000, 0.04));

    [Fact]
    public void FirstBlock_RightAfterTheStart_NeedsNoSilence() => Assert.Equal(0, FilmSound.SilenceBefore(0, 0.01, 48000, 0.04));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoRate_NeedsNoSilence(int rate) => Assert.Equal(0, FilmSound.SilenceBefore(0, 5.0, rate, 0.04));

    [Fact]
    public void Gaps_AreSummedWithTheLongest()
    {
        var gaps = new FilmSound.Gaps();
        gaps.Add(4.2, 0.8);
        gaps.Add(9.0, 0.3);
        Assert.Equal("2 gaps filled with silence, 1.10 s in all, longest 0.80 s at 4.20 s", gaps.Describe());
    }

    [Fact]
    public void NoGaps_SaySo() => Assert.Equal("no gaps", new FilmSound.Gaps().Describe());
}
