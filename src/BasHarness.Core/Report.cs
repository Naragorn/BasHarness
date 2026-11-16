namespace BasHarness.Core
{
    /// <summary>The log lines the runner parses. Keep in sync with runner/harness_logic.py.</summary>
    public static class Report
    {
        public const string Prefix = "[BasHarness]";

        public static string StepLine(int index, int total, bool passed, int line, string step, string detail)
        {
            var text = Prefix + " STEP " + index + "/" + total + " " + (passed ? "PASS" : "FAIL") + " line " + line + ": " + step;
            if (!string.IsNullOrEmpty(detail))
            {
                text += " -- " + Flatten(detail);
            }
            return text;
        }

        public static string ResultLine(int passed, int total, int failed)
        {
            var verdict = failed == 0 && total > 0 ? "PASS" : "FAIL";
            return Prefix + " RESULT " + verdict + " " + passed + "/" + total;
        }

        private static string Flatten(string text)
        {
            return text.Replace("\r\n", " | ").Replace("\n", " | ");
        }
    }
}
