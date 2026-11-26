using System;
using System.Globalization;
using System.Text;

namespace BasHarness.Core
{
    /// <summary>
    /// Frame bookkeeping of a film (record-start … record-stop): the film runs at a fixed frame rate in real time, so
    /// it shows the game at its own pace. Each captured picture belongs to the slot of the moment it was taken; a slot
    /// the game was too slow to fill repeats the picture before it.
    /// </summary>
    public static class FilmFrames
    {
        /// <summary>The slot (0-based frame number of the film) a picture taken <paramref name="elapsed"/> seconds after the start fills.</summary>
        public static int Slot(double elapsed, int fps) => elapsed <= 0 || fps <= 0 ? 0 : (int)Math.Floor(elapsed * fps);

        /// <summary>
        /// How many times to write a picture of slot <paramref name="slot"/> when the last written slot is
        /// <paramref name="lastWritten"/> (-1 before the first): once for its own slot and once for each skipped slot
        /// before it; 0 when its slot is already written (two pictures in one slot: the first one stays).
        /// </summary>
        public static int Writes(int slot, int lastWritten) => slot <= lastWritten ? 0 : slot - lastWritten;

        /// <summary>
        /// The film's description for the runner, which turns the raw pictures into videos (one JSON object). With
        /// <paramref name="audioRate"/> and <paramref name="audioChannels"/> above 0 the film has sound: the game's mix as
        /// raw 32-bit float samples, interleaved (record-start audio=true).
        /// </summary>
        public static string Sidecar(string name, int width, int height, int fps, string[] views, int frames, int repeated,
            int audioRate = 0, int audioChannels = 0)
        {
            var sb = new StringBuilder();
            sb.Append("{\"name\":\"").Append(Escape(name)).Append("\",\"width\":").Append(width.ToString(CultureInfo.InvariantCulture))
                .Append(",\"height\":").Append(height.ToString(CultureInfo.InvariantCulture))
                .Append(",\"fps\":").Append(fps.ToString(CultureInfo.InvariantCulture))
                .Append(",\"views\":[");
            for (var i = 0; i < views.Length; i++)
            {
                sb.Append(i > 0 ? "," : string.Empty).Append('"').Append(Escape(views[i])).Append('"');
            }
            sb.Append("],\"frames\":").Append(frames.ToString(CultureInfo.InvariantCulture))
                .Append(",\"repeated\":").Append(repeated.ToString(CultureInfo.InvariantCulture));
            if (audioRate > 0 && audioChannels > 0)
            {
                sb.Append(",\"audio\":{\"rate\":").Append(audioRate.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"channels\":").Append(audioChannels.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            return sb.Append('}').ToString();
        }

        private static string Escape(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
