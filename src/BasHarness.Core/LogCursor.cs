using System;

namespace BasHarness.Core
{
    /// <summary>"Has this text appeared in the log since the last mark?"</summary>
    public sealed class LogCursor
    {
        private int _offset;

        public void Mark(int logLength)
        {
            _offset = Math.Max(0, logLength);
        }

        public bool ContainsSinceMark(string log, string text)
        {
            if (log == null || string.IsNullOrEmpty(text))
            {
                return false;
            }
            var start = _offset <= log.Length ? _offset : 0;
            return log.IndexOf(text, start, StringComparison.Ordinal) >= 0;
        }
    }
}
