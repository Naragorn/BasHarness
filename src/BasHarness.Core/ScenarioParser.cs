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

            /// <summary>Extra checks on the parsed arguments; returns an error or null.</summary>
            public Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string>, string> Validate { get; set; }
        }

        /// <summary>BrainModuleAttack.AttackStage names, in the game's order.</summary>
        public static readonly string[] AttackStages = { "WindUp", "Attack", "FollowThrough", "End", "Cancel" };

        private static readonly Dictionary<string, Spec> Specs = new Dictionary<string, Spec>
        {
            ["wait-level"] = new Spec(0, 1, false) { NumericFirstArg = true },
            ["wait"] = new Spec(1, 1, false) { NumericFirstArg = true },
            ["mark"] = new Spec(0, 0, false),
            ["expect-log"] = new Spec(0, 0, true),
            ["await-log"] = new Spec(0, 0, true),
            ["expect-no-log"] = new Spec(0, 0, true),
            ["option-exists"] = new Spec(2, 2, false, "category", "display", "type", "save", "interaction", "tooltip"),
            ["set-option"] = new Spec(3, 3, false),
            ["press"] = new Spec(2, 2, false),
            ["expect-option"] = new Spec(3, 3, false),
            ["save-options"] = new Spec(0, 0, false),
            ["screenshot"] = new Spec(1, 1, false, "of", "distance", "height", "angle")
            {
                Validate = (a, n) => OneOf(n, "of", new[] { "npc" })
                    ?? NumberKey(n, "distance") ?? NumberKey(n, "height") ?? NumberKey(n, "angle")
                    ?? (!n.ContainsKey("of") && (n.ContainsKey("distance") || n.ContainsKey("height") || n.ContainsKey("angle"))
                        ? "distance, height and angle need of=npc"
                        : null),
            },
            ["quit"] = new Spec(0, 0, false),
            ["spawn-npc"] = new Spec(1, 1, false, "distance", "angle", "brain", "facing", "container")
            {
                Validate = (a, n) => NumberKey(n, "distance") ?? NumberKey(n, "angle") ?? NumberKey(n, "facing"),
            },
            ["run"] = new Spec(1, 1, false) { Validate = (a, n) => RunRatio(a[0]) },
            ["stop-run"] = new Spec(0, 0, false),
            ["npc-grab"] = new Spec(1, 1, false, "hand") { Validate = (a, n) => OneOf(n, "hand", new[] { "left", "right" }) },
            ["npc-attack"] = new Spec(1, 1, false)
            {
                Validate = (a, n) => AttackStages.Contains(a[0], StringComparer.OrdinalIgnoreCase)
                    ? null
                    : "'" + a[0] + "' is not one of " + string.Join(", ", AttackStages),
            },
            ["player-grab-dropped"] = new Spec(0, 0, false, "hand") { Validate = (a, n) => OneOf(n, "hand", new[] { "left", "right" }) },
            ["turn-player"] = new Spec(1, 1, false)
            {
                Validate = (a, n) => TryParseNumber(a[0], out _) ? null : "'" + a[0] + "' is not a number of degrees",
            },
            ["player-grab"] = new Spec(1, 1, false, "hand") { Validate = (a, n) => OneOf(n, "hand", new[] { "left", "right" }) },
            ["player-parry"] = new Spec(0, 0, false, "offset") { Validate = (a, n) => NumberKey(n, "offset") },
            ["player-strike-weapon"] = new Spec(0, 0, false, "twist", "seconds") { Validate = (a, n) => NumberKey(n, "twist") ?? SecondsKey(n, "seconds") },
            ["player-release"] = new Spec(0, 0, false, "hand") { Validate = (a, n) => OneOf(n, "hand", new[] { "left", "right" }) },
            ["player-push"] = new Spec(2, 2, false, "back")
            {
                Validate = (a, n) => (!TryParseNumber(a[0], out _) ? "'" + a[0] + "' is not a number of metres"
                    : !TryParseSeconds(a[1], out _) ? "'" + a[1] + "' is not a number of seconds >= 0" : null)
                    ?? OneOf(n, "back", new[] { "true", "false" }),
            },
            ["player-invincible"] = new Spec(1, 1, false)
            {
                Validate = (a, n) => a[0] == "true" || a[0] == "false" ? null : "'" + a[0] + "' is not one of true, false",
            },
            ["expect-player-speed"] = new Spec(1, 2, false) { Validate = (a, n) => SpeedRange(a) },
            ["hit-npc"] = new Spec(1, 1, false, "velocity", "damage", "push", "type", "by", "item", "dead", "swing")
            {
                Validate = (a, n) => NumberKey(n, "velocity") ?? NumberKey(n, "damage") ?? NumberKey(n, "swing") ?? WholeKey(n, "push") ?? OneOf(n, "by", HitBy.Names)
                    ?? OneOf(n, "dead", new[] { "true", "false" }),
            },
            ["expect-npc"] = new Spec(2, 2, false, "within")
            {
                Validate = (a, n) => !NpcProperty.IsKnown(a[0])
                    ? "unknown NPC property '" + a[0] + "' (known: " + string.Join(", ", NpcProperty.Names) + ")"
                    : SecondsKey(n, "within"),
            },
        };

        public static IEnumerable<string> Verbs => Specs.Keys;

        /// <summary>await-log's text: "&lt;seconds&gt; &lt;text&gt;" (seconds a positive number).</summary>
        public static bool TrySplitAwait(string rest, out float seconds, out string text)
        {
            seconds = 0f;
            text = null;
            var space = (rest ?? string.Empty).IndexOf(' ');
            if (space <= 0 || !TryParseNumber(rest.Substring(0, space), out seconds) || seconds <= 0f)
            {
                return false;
            }
            text = rest.Substring(space + 1).Trim();
            return text.Length > 0;
        }

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

            var invalid = spec.Validate?.Invoke(args, named);
            if (invalid != null)
            {
                return verb + ": " + invalid;
            }

            step = new Step(lineNumber, verb, args, named, null, raw);
            return null;
        }

        public static bool TryParseNumber(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string NumberKey(IReadOnlyDictionary<string, string> named, string key)
        {
            return named.TryGetValue(key, out var text) && !TryParseNumber(text, out _)
                ? key + ": '" + text + "' is not a number"
                : null;
        }

        private static string WholeKey(IReadOnlyDictionary<string, string> named, string key)
        {
            return named.TryGetValue(key, out var text) && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                ? key + ": '" + text + "' is not a whole number"
                : null;
        }

        private static string SecondsKey(IReadOnlyDictionary<string, string> named, string key)
        {
            return named.TryGetValue(key, out var text) && !TryParseSeconds(text, out _)
                ? key + ": '" + text + "' is not a number of seconds >= 0"
                : null;
        }

        private static string OneOf(IReadOnlyDictionary<string, string> named, string key, IReadOnlyList<string> allowed)
        {
            return named.TryGetValue(key, out var text) && !allowed.Contains(text, StringComparer.OrdinalIgnoreCase)
                ? key + ": '" + text + "' is not one of " + string.Join(", ", allowed)
                : null;
        }

        private static string RunRatio(string text)
        {
            if (!TryParseNumber(text, out var ratio))
            {
                return "'" + text + "' is not a number";
            }
            return ratio < 0f || ratio > 1f ? "run ratio " + text + " must be between 0 and 1" : null;
        }

        private static string SpeedRange(IReadOnlyList<string> args)
        {
            foreach (var text in args)
            {
                if (!TryParseNumber(text, out _))
                {
                    return "'" + text + "' is not a number";
                }
            }
            if (args.Count == 2)
            {
                TryParseNumber(args[0], out var min);
                TryParseNumber(args[1], out var max);
                if (max < min)
                {
                    return "max " + args[1] + " is below min " + args[0];
                }
            }
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
