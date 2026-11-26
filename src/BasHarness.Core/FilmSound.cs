using System;
using System.Globalization;

namespace BasHarness.Core
{
    /// <summary>
    /// Keeps a film's sound on the film's clock (real time since record-start). The game's mixer hands over its samples in
    /// blocks; when it stalls (a long frame, a garbage collection), fewer samples arrive than time passed, and everything
    /// after the stall would play early against the pictures. Such a stretch is filled with silence before the next block.
    /// </summary>
    public static class FilmSound
    {
        /// <summary>
        /// Frames of silence to write before the next block: none while the sound is on time, ahead, or less than
        /// <paramref name="toleranceSeconds"/> behind; otherwise as many as bring it to <paramref name="elapsedSeconds"/>.
        /// </summary>
        public static long SilenceBefore(long writtenFrames, double elapsedSeconds, int rate, double toleranceSeconds)
        {
            if (rate <= 0)
            {
                return 0;
            }
            var due = (long)Math.Floor(elapsedSeconds * rate);
            return due - writtenFrames > toleranceSeconds * rate ? due - writtenFrames : 0;
        }

        /// <summary>The stretches filled with silence during one film, for the step's report.</summary>
        public sealed class Gaps
        {
            private int _count;
            private double _total;
            private double _longest;
            private double _longestAt;

            public void Add(double atSeconds, double seconds)
            {
                _count++;
                _total += seconds;
                if (seconds > _longest)
                {
                    _longest = seconds;
                    _longestAt = atSeconds;
                }
            }

            public string Describe()
            {
                if (_count == 0)
                {
                    return "no gaps";
                }
                return _count.ToString(CultureInfo.InvariantCulture) + (_count == 1 ? " gap" : " gaps") + " filled with silence, "
                    + _total.ToString("0.00", CultureInfo.InvariantCulture) + " s in all, longest "
                    + _longest.ToString("0.00", CultureInfo.InvariantCulture) + " s at " + _longestAt.ToString("0.00", CultureInfo.InvariantCulture) + " s";
            }
        }
    }
}
