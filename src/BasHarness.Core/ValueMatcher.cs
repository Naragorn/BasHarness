using System;
using System.Collections.Generic;
using System.Globalization;

namespace BasHarness.Core
{
    /// <summary>One entry of a ModOption's value list: its menu title and its value as text.</summary>
    public sealed class OptionValue
    {
        public OptionValue(string title, string valueText)
        {
            Title = title;
            ValueText = valueText;
        }

        public string Title { get; }
        public string ValueText { get; }
    }

    public static class ValueMatcher
    {
        private const double Tolerance = 1e-4;

        /// <summary>
        /// Index of <paramref name="wanted"/> in the option's value list: exact value text first,
        /// then numeric equality (float noise tolerated), then the menu title. -1 if none.
        /// </summary>
        public static int IndexOf(IReadOnlyList<OptionValue> values, string wanted)
        {
            if (values == null || string.IsNullOrEmpty(wanted))
            {
                return -1;
            }
            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i].ValueText, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            if (TryNumber(wanted, out var number))
            {
                for (var i = 0; i < values.Count; i++)
                {
                    if (TryNumber(values[i].ValueText, out var candidate) && Math.Abs(candidate - number) < Tolerance)
                    {
                        return i;
                    }
                }
            }
            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i].Title, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>What a press on a ButtonList does (UIList.NextValue): next index, wrapping to 0.</summary>
        public static int NextIndex(int current, int count)
        {
            if (count <= 0)
            {
                return -1;
            }
            if (current < 0 || current + 1 >= count)
            {
                return 0;
            }
            return current + 1;
        }

        private static bool TryNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
