using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BasHarness.Core;
using ThunderRoad;
using UnityEngine;
using OptionValue = BasHarness.Core.OptionValue;

namespace BasHarness
{
    /// <summary>Runs a scenario inside the game, one step after the other, and reports each step to the log.</summary>
    internal sealed class HarnessRunner
    {
        private const float ExpectLogTimeout = 10f;
        private const float DefaultLevelTimeout = 180f;
        private const float Poll = 0.25f;

        private readonly string _scenarioPath;
        private readonly string _outputDir;
        private readonly LogCursor _cursor = new LogCursor();
        private bool _calibrationSkipped;

        public HarnessRunner(string scenarioPath, string outputDir)
        {
            _scenarioPath = scenarioPath;
            _outputDir = string.IsNullOrEmpty(outputDir) ? Path.Combine(Application.persistentDataPath, "BasHarness") : outputDir;
        }

        private sealed class Outcome
        {
            public bool Ok = true;
            public string Detail;

            public void Fail(string detail)
            {
                Ok = false;
                Detail = detail;
            }
        }

        /// <summary>
        /// A new character asks for a two-trigger calibration before the level continues
        /// (PlayerSpawner.CalibratePlayer polls calibration.calibrated each second). Nobody is in the
        /// headset, so the harness marks it calibrated. Only ever runs in the harness's own prefix.
        /// </summary>
        public void KeepCalibrated()
        {
            var calibration = Player.characterData?.calibration;
            if (calibration == null || calibration.calibrated)
            {
                return;
            }
            calibration.calibrated = true;
            if (!_calibrationSkipped)
            {
                _calibrationSkipped = true;
                Debug.Log(Report.Prefix + " calibration marked done (nobody in the headset)");
            }
        }

        public IEnumerator Run()
        {
            Debug.Log(Report.Prefix + " scenario " + _scenarioPath);
            string text = null;
            try
            {
                text = File.ReadAllText(_scenarioPath);
            }
            catch (Exception ex)
            {
                Debug.Log(Report.Prefix + " cannot read scenario: " + ex.Message);
            }
            var scenario = ScenarioParser.Parse(text);
            if (scenario.Errors.Count > 0)
            {
                foreach (var error in scenario.Errors)
                {
                    Debug.Log(Report.Prefix + " scenario error " + error);
                }
                Debug.Log(Report.ResultLine(0, scenario.Steps.Count, 1));
                Quit();
                yield break;
            }

            var total = scenario.Steps.Count;
            var passed = 0;
            var failed = 0;
            for (var i = 0; i < total; i++)
            {
                var step = scenario.Steps[i];
                var outcome = new Outcome();
                if (step.Verb == "quit")
                {
                    Debug.Log(Report.StepLine(i + 1, total, true, step.Line, step.Describe(), null));
                    passed++;
                    break;
                }
                yield return Execute(step, outcome);
                Debug.Log(Report.StepLine(i + 1, total, outcome.Ok, step.Line, step.Describe(), outcome.Detail));
                if (!outcome.Ok)
                {
                    failed++;
                    break;
                }
                passed++;
            }
            Debug.Log(Report.ResultLine(passed, total, failed));
            yield return new WaitForSecondsRealtime(1f);
            Quit();
        }

        private static void Quit()
        {
            Debug.Log(Report.Prefix + " quitting the game");
            GameManager.local.QuitGame();
        }

        private IEnumerator Execute(Step step, Outcome outcome)
        {
            switch (step.Verb)
            {
                case "wait-level":
                    return WaitLevel(step, outcome);
                case "wait":
                    ScenarioParser.TryParseSeconds(step.Args[0], out var seconds);
                    return Wait(seconds);
                case "mark":
                    _cursor.Mark(ReadLog().Length);
                    return Nothing();
                case "expect-log":
                    return ExpectLog(step.Rest, outcome);
                case "expect-no-log":
                    if (_cursor.ContainsSinceMark(ReadLog(), step.Rest))
                    {
                        outcome.Fail("found in log since last mark");
                    }
                    return Nothing();
                case "option-exists":
                    OptionExists(step, outcome);
                    return Nothing();
                case "set-option":
                    SetOption(step, outcome);
                    return Nothing();
                case "press":
                    Press(step, outcome);
                    return Nothing();
                case "expect-option":
                    ExpectOption(step, outcome);
                    return Nothing();
                case "save-options":
                    // What closing the player menu does when an option changed (UIPlayerMenu.ManagedOnDisable).
                    return Options.SaveCoroutine();
                case "screenshot":
                    return Screenshot(step.Args[0], outcome);
                default:
                    outcome.Fail("verb not implemented in the game: " + step.Verb);
                    return Nothing();
            }
        }

        private static IEnumerator Nothing()
        {
            yield break;
        }

        private static IEnumerator Wait(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
        }

        private static IEnumerator WaitLevel(Step step, Outcome outcome)
        {
            var timeout = DefaultLevelTimeout;
            if (step.Args.Count > 0)
            {
                ScenarioParser.TryParseSeconds(step.Args[0], out timeout);
            }
            var start = Time.realtimeSinceStartup;
            while (Player.local == null || Player.local.creature == null)
            {
                if (Time.realtimeSinceStartup - start > timeout)
                {
                    outcome.Fail("no player creature after " + timeout.ToString("0", CultureInfo.InvariantCulture) + "s");
                    yield break;
                }
                yield return new WaitForSecondsRealtime(Poll);
            }
            // Let the level's own start-up (spawners, menus) settle.
            yield return new WaitForSecondsRealtime(2f);
            outcome.Detail = "player in level '" + (Level.current?.data?.id ?? "?") + "'";
        }

        private IEnumerator ExpectLog(string text, Outcome outcome)
        {
            var start = Time.realtimeSinceStartup;
            while (!_cursor.ContainsSinceMark(ReadLog(), text))
            {
                if (Time.realtimeSinceStartup - start > ExpectLogTimeout)
                {
                    outcome.Fail("not found since last mark within " + ExpectLogTimeout.ToString("0", CultureInfo.InvariantCulture) + "s");
                    yield break;
                }
                yield return new WaitForSecondsRealtime(Poll);
            }
        }

        private static string ReadLog()
        {
            try
            {
                using (var stream = new FileStream(Application.consoleLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool TryFindOption(Step step, Outcome outcome, out ModOption option)
        {
            option = null;
            var modName = step.Args[0];
            var optionName = step.Args[1];
            var mod = ModManager.loadedMods.FirstOrDefault(m =>
                string.Equals(m.folderName, modName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Name, modName, StringComparison.OrdinalIgnoreCase));
            if (mod == null)
            {
                outcome.Fail("mod '" + modName + "' not loaded (loaded: " + string.Join(", ", ModManager.loadedMods.Select(m => m.folderName)) + ")");
                return false;
            }
            option = mod.modOptions.FirstOrDefault(o => o.name == optionName);
            if (option == null)
            {
                outcome.Fail("option '" + optionName + "' not found in " + mod.folderName + " (has: " + string.Join(", ", mod.modOptions.Select(o => o.name)) + ")");
                return false;
            }
            return true;
        }

        private static List<OptionValue> ValuesOf(ModOption option)
        {
            return (option.parameterValues ?? new ModOptionParameter[0])
                .Select(p => new OptionValue(p.title, Convert.ToString(p.value, CultureInfo.InvariantCulture)))
                .ToList();
        }

        /// <summary>The title the mods page shows (same rule as UIArrowListModOption.InitValues).</summary>
        private static string DisplayName(ModOption option)
        {
            var title = option.name;
            if (LocalizationManager.GetLocalizationIds(option.nameLocalizationId, out var group, out var id))
            {
                var localized = LocalizationManager.Instance.GetLocalizedString(group, id);
                if (localized != null)
                {
                    title = localized;
                }
            }
            return title;
        }

        private static void OptionExists(Step step, Outcome outcome)
        {
            if (!TryFindOption(step, outcome, out var option))
            {
                return;
            }
            var actual = new Dictionary<string, string>
            {
                ["category"] = option.category,
                ["display"] = DisplayName(option),
                ["type"] = option.memberDataType?.Name,
                ["save"] = option.saveValue ? "true" : "false",
                ["interaction"] = option.interactionType.ToString(),
                ["tooltip"] = option.tooltip,
            };
            var mismatches = new List<string>();
            foreach (var pair in step.Named)
            {
                actual.TryGetValue(pair.Key, out var value);
                var ok = pair.Key == "tooltip"
                    ? value != null && value.IndexOf(pair.Value, StringComparison.OrdinalIgnoreCase) >= 0
                    : string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase);
                if (!ok)
                {
                    mismatches.Add(pair.Key + ": expected '" + pair.Value + "', is '" + value + "'");
                }
            }
            if (mismatches.Count > 0)
            {
                outcome.Fail(string.Join("; ", mismatches));
            }
            else
            {
                outcome.Detail = "display '" + actual["display"] + "', category '" + actual["category"] + "'";
            }
        }

        private static void SetOption(Step step, Outcome outcome)
        {
            if (!TryFindOption(step, outcome, out var option))
            {
                return;
            }
            var values = ValuesOf(option);
            var index = ValueMatcher.IndexOf(values, step.Args[2]);
            if (index < 0)
            {
                outcome.Fail("value '" + step.Args[2] + "' not in [" + string.Join(", ", values.Select(v => v.ValueText)) + "]");
                return;
            }
            // Same call the mods page makes on a value change (UIArrowListModOption.OnIndexChanged).
            option.Apply(index);
            outcome.Detail = "applied index " + index;
        }

        private static void Press(Step step, Outcome outcome)
        {
            if (!TryFindOption(step, outcome, out var option))
            {
                return;
            }
            var index = ValueMatcher.NextIndex(option.currentValueIndex, option.parameterValues?.Length ?? 0);
            if (index < 0)
            {
                outcome.Fail("option has no values to press");
                return;
            }
            // A ButtonList press: UIList.NextValue wraps, then OnIndexChanged -> Apply.
            option.Apply(index);
            outcome.Detail = "applied index " + index;
        }

        private static void ExpectOption(Step step, Outcome outcome)
        {
            if (!TryFindOption(step, outcome, out var option))
            {
                return;
            }
            var values = ValuesOf(option);
            var current = option.currentValueIndex;
            var wanted = ValueMatcher.IndexOf(values, step.Args[2]);
            var shown = current >= 0 && current < values.Count ? values[current].ValueText : "?";
            if (wanted < 0 || wanted != current)
            {
                outcome.Fail("menu value is '" + shown + "', expected '" + step.Args[2] + "'");
                return;
            }
            // The member the option drives must hold the same value (live state, not just the menu).
            var live = ReadMember(option);
            if (live != null && ValueMatcher.IndexOf(new[] { values[current] }, live) != 0)
            {
                outcome.Fail("menu value '" + shown + "' but the member holds '" + live + "'");
                return;
            }
            outcome.Detail = "menu '" + shown + "', member '" + (live ?? "(method)") + "'";
        }

        private static string ReadMember(ModOption option)
        {
            switch (option.member)
            {
                case FieldInfo field when field.IsStatic:
                    return Convert.ToString(field.GetValue(null), CultureInfo.InvariantCulture);
                case PropertyInfo property when property.GetGetMethod(true)?.IsStatic == true:
                    return Convert.ToString(property.GetValue(null, null), CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }

        private IEnumerator Screenshot(string name, Outcome outcome)
        {
            Directory.CreateDirectory(_outputDir);
            var path = Path.Combine(_outputDir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            var start = Time.realtimeSinceStartup;
            while (!File.Exists(path))
            {
                if (Time.realtimeSinceStartup - start > 5f)
                {
                    outcome.Fail("no file at " + path);
                    yield break;
                }
                yield return new WaitForSecondsRealtime(Poll);
            }
            outcome.Detail = path;
        }
    }
}
