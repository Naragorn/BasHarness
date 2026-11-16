using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BasHarness.Core
{
    /// <summary>One scenario line.</summary>
    public sealed class Step
    {
        public Step(int line, string verb, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> named, string rest, string source)
        {
            Line = line;
            Verb = verb;
            Args = args;
            Named = named;
            Rest = rest;
            Source = source;
        }

        public int Line { get; }
        public string Verb { get; }
        public IReadOnlyList<string> Args { get; }
        public IReadOnlyDictionary<string, string> Named { get; }

        /// <summary>For text verbs (expect-log ...): everything after the verb.</summary>
        public string Rest { get; }

        public string Source { get; }

        public string Describe() => Source;
    }

    public sealed class Scenario
    {
        public Scenario(List<Step> steps, List<string> errors)
        {
            Steps = steps;
            Errors = errors;
        }

        public List<Step> Steps { get; }
        public List<string> Errors { get; }
    }

    /// <summary>
    /// Line-based scenario language: <c>verb arg "quoted arg" key=value key="quoted value"</c>,
    /// '#' comments. Verbs with free text (expect-log ...) take the rest of the line verbatim.
    /// </summary>
    public static class ScenarioParser
    {
        private sealed class Spec
        {
            public Spec(int minArgs, int maxArgs, bool rest, params string[] keys)
            {
                MinArgs = minArgs;
                MaxArgs = maxArgs;
                Rest = rest;
                Keys = keys;
            }

            public int MinArgs { get; }
            public int MaxArgs { get; }
            public bool Rest { get; }
            public string[] Keys { get; }
            public bool NumericFirstArg { get; set; }
        }

        private static readonly Dictionary<string, Spec> Specs = new Dictionary<string, Spec>
        {
            ["wait-level"] = new Spec(0, 1, false) { NumericFirstArg = true },
            ["wait"] = new Spec(1, 1, false) { NumericFirstArg = true },
            ["mark"] = new Spec(0, 0, false),
            ["expect-log"] = new Spec(0, 0, true),
            ["expect-no-log"] = new Spec(0, 0, true),
            ["option-exists"] = new Spec(2, 2, false, "category", "display", "type", "save", "interaction", "tooltip"),
            ["set-option"] = new Spec(3, 3, false),
            ["press"] = new Spec(2, 2, false),
            ["expect-option"] = new Spec(3, 3, false),
            ["save-options"] = new Spec(0, 0, false),
            ["screenshot"] = new Spec(1, 1, false),
            ["quit"] = new Spec(0, 0, false),
        };

        public static IEnumerable<string> Verbs => Specs.Keys;

        public static Scenario Parse(string text)
        {
            var steps = new List<Step>();
            var errors = new List<string>();
            if (text == null)
            {
                errors.Add("scenario text is null");
                return new Scenario(steps, errors);
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i].Trim();
                if (raw.Length == 0 || raw.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                var error = ParseLine(i + 1, raw, out var step);
                if (error != null)
                {
                    errors.Add("line " + (i + 1) + ": " + error);
                }
                else
                {
                    steps.Add(step);
                }
            }
            if (steps.Count == 0 && errors.Count == 0)
            {
                errors.Add("scenario has no steps");
            }
            return new Scenario(steps, errors);
        }

        private static string ParseLine(int lineNumber, string raw, out Step step)
        {
            step = null;
            var space = raw.IndexOf(' ');
            var verb = space < 0 ? raw : raw.Substring(0, space);
            var remainder = space < 0 ? string.Empty : raw.Substring(space + 1).Trim();
            if (!Specs.TryGetValue(verb, out var spec))
            {
                return "unknown verb '" + verb + "' (known: " + string.Join(", ", Specs.Keys) + ")";
            }

            if (spec.Rest)
            {
                var rest = StripOuterQuotes(remainder);
                if (rest.Length == 0)
                {
                    return verb + " needs text";
                }
                step = new Step(lineNumber, verb, new string[0], new Dictionary<string, string>(), rest, raw);
                return null;
            }

            var tokenError = Tokenize(remainder, out var tokens);
            if (tokenError != null)
            {
                return tokenError;
            }

            var args = new List<string>();
            var named = new Dictionary<string, string>();
            foreach (var token in tokens)
            {
                if (!token.Quoted && TrySplitNamed(token.Text, out var key, out var value))
                {
                    if (!spec.Keys.Contains(key))
                    {
                        return verb + ": unknown key '" + key + "'" + (spec.Keys.Length > 0 ? " (known: " + string.Join(", ", spec.Keys) + ")" : string.Empty);
                    }
                    named[key] = value;
                }
                else
                {
                    args.Add(token.Text);
                }
            }

            if (args.Count < spec.MinArgs || args.Count > spec.MaxArgs)
            {
                var expected = spec.MinArgs == spec.MaxArgs ? spec.MinArgs.ToString(CultureInfo.InvariantCulture) : spec.MinArgs + "-" + spec.MaxArgs;
                return verb + " expects " + expected + " argument(s), got " + args.Count;
            }
            if (spec.NumericFirstArg && args.Count > 0 && !TryParseSeconds(args[0], out _))
            {
                return verb + ": '" + args[0] + "' is not a number of seconds >= 0";
            }

            step = new Step(lineNumber, verb, args, named, null, raw);
            return null;
        }

        public static bool TryParseSeconds(string text, out float seconds)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0f;
        }

        private struct Token
        {
            public string Text;
            public bool Quoted;
        }

        private static string Tokenize(string text, out List<Token> tokens)
        {
            tokens = new List<Token>();
            var current = new StringBuilder();
            var inQuotes = false;
            var hadQuotes = false;
            var started = false;
            foreach (var c in text)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    hadQuotes = true;
                    started = true;
                    continue;
                }
                if (c == ' ' && !inQuotes)
                {
                    if (started)
                    {
                        tokens.Add(new Token { Text = current.ToString(), Quoted = hadQuotes && !current.ToString().Contains("=") });
                        current.Clear();
                        hadQuotes = false;
                        started = false;
                    }
                    continue;
                }
                current.Append(c);
                started = true;
            }
            if (inQuotes)
            {
                return "unterminated quote";
            }
            if (started)
            {
                tokens.Add(new Token { Text = current.ToString(), Quoted = hadQuotes && !current.ToString().Contains("=") });
            }
            return null;
        }

        private static bool TrySplitNamed(string token, out string key, out string value)
        {
            key = null;
            value = null;
            var eq = token.IndexOf('=');
            if (eq <= 0)
            {
                return false;
            }
            var candidate = token.Substring(0, eq);
            if (!candidate.All(ch => (ch >= 'a' && ch <= 'z') || ch == '-'))
            {
                return false;
            }
            key = candidate;
            value = token.Substring(eq + 1);
            return true;
        }

        private static string StripOuterQuotes(string text)
        {
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
            {
                return text.Substring(1, text.Length - 2);
            }
            return text;
        }
    }
}
