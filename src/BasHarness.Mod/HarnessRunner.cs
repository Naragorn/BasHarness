using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BasHarness.Core;
using ThunderRoad;
using ThunderRoad.Skill.Spell;
using UnityEngine;
using OptionValue = BasHarness.Core.OptionValue;

namespace BasHarness
{
    /// <summary>Runs a scenario inside the game, one step after the other, and reports each step to the log.</summary>
    internal sealed partial class HarnessRunner
    {
        private const float ExpectLogTimeout = 10f;
        private const float DefaultLevelTimeout = 180f;
        private const float Poll = 0.25f;

        private readonly string _scenarioPath;
        private readonly string _outputDir;
        private readonly LogCursor _cursor = new LogCursor();
        private bool _calibrationSkipped;
        private Creature _npc;

        /// <summary>The item the last npc-grab gave the NPC (player-grab-dropped takes it).</summary>
        private Item _npcItem;
        private float _runRatio = -1f;

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
                case "await-log":
                    if (!ScenarioParser.TrySplitAwait(step.Rest, out var awaitSeconds, out var awaitText))
                    {
                        outcome.Fail("await-log needs <seconds> <text>");
                        return Nothing();
                    }
                    return ExpectLog(awaitText, outcome, awaitSeconds);
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
                    return step.Named.ContainsKey("of") ? NpcScreenshot(step, outcome) : Screenshot(step.Args[0], outcome);
                case "spawn-npc":
                    return SpawnNpc(step, outcome);
                case "run":
                    return Run(step, outcome);
                case "stop-run":
                    StopRun(outcome);
                    return Nothing();
                case "player-invincible":
                    Player.invincibility = step.Args[0] == "true";
                    outcome.Detail = "Player.invincibility = " + Player.invincibility;
                    return Nothing();
                case "npc-tk-throw":
                    NpcTkThrow(step, outcome);
                    return Nothing();
                case "player-cast":
                    return PlayerCast(step, outcome);
                case "player-skill":
                    PlayerSkill(step, outcome);
                    return Nothing();
                case "turn-player":
                    TurnPlayer(step, outcome);
                    return Nothing();
                case "expect-player-speed":
                    return ExpectPlayerSpeed(step, outcome);
                case "hit-npc":
                    return HitNpc(step, outcome);
                case "npc-grab":
                    return NpcGrab(step, outcome);
                case "npc-attack":
                    NpcAttack(step, outcome);
                    return Nothing();
                case "player-grab":
                    return PlayerGrab(step, outcome);
                case "player-parry":
                    return PlayerParry(step, outcome);
                case "player-push":
                    return PlayerPush(step, outcome);
                case "player-strike-weapon":
                    return PlayerStrikeWeapon(step, outcome);
                case "player-release":
                    PlayerRelease(step, outcome);
                    return Nothing();
                case "player-grab-npc":
                    return PlayerGrabNpc(step, outcome);
                case "player-slam":
                    return PlayerSlam(step, outcome);
                case "player-stab":
                    return PlayerStab(step, outcome);
                case "player-eyes":
                    return PlayerEyes(outcome);
                case "place-block":
                    PlaceBlock(step, outcome);
                    return Nothing();
                case "record-start":
                    RecordStart(step, outcome);
                    return Nothing();
                case "frame-rate":
                    return FrameRate(step, outcome);
                case "record-stop":
                    RecordStop(outcome);
                    return Nothing();
                case "player-grab-dropped":
                    return PlayerGrabDropped(step, outcome);
                case "expect-npc":
                    return ExpectNpc(step, outcome);
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

        private IEnumerator ExpectLog(string text, Outcome outcome, float timeout = ExpectLogTimeout)
        {
            var start = Time.realtimeSinceStartup;
            while (!_cursor.ContainsSinceMark(ReadLog(), text))
            {
                if (Time.realtimeSinceStartup - start > timeout)
                {
                    outcome.Fail("not found since last mark within " + timeout.ToString("0", CultureInfo.InvariantCulture) + "s");
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

        private const float SpawnTimeout = 30f;
        private const float DefaultNpcWithin = 2f;
        private const float SpeedTimeout = 5f;

        /// <summary>
        /// Spawns an NPC the way the book's spawner does (UICustomSpawner.Spawn: CreatureData clone,
        /// optional brain id, SpawnCoroutine) at a point around the player, and waits until it is loaded.
        /// </summary>
        private IEnumerator SpawnNpc(Step step, Outcome outcome)
        {
            var player = Player.local?.creature;
            if (player == null)
            {
                outcome.Fail("no player creature (wait-level first)");
                yield break;
            }
            if (!(Catalog.GetData<CreatureData>(step.Args[0])?.Clone() is CreatureData data))
            {
                outcome.Fail("no CreatureData '" + step.Args[0] + "' in the catalog");
                yield break;
            }
            if (step.Named.TryGetValue("brain", out var brainId))
            {
                data.brainId = brainId;
            }
            if (step.Named.TryGetValue("container", out var containerId))
            {
                if (Catalog.GetData<ContainerData>(containerId, false) == null)
                {
                    outcome.Fail("no ContainerData '" + containerId + "' in the catalog");
                    yield break;
                }
                data.containerID = containerId;
            }
            var distance = Number(step, "distance", 2f);
            var angle = Number(step, "angle", 0f);
            var forward = player.transform.forward;
            var (x, z) = CombatMath.OffsetXZ(forward.x, forward.z, angle, distance);
            var position = player.transform.position + new Vector3(x, 0f, z);
            var facing = Number(step, "facing", 0f);
            var yaw = CombatMath.FacingYaw(Quaternion.LookRotation(new Vector3(-x, 0f, -z)).eulerAngles.y, facing);

            Creature spawned = null;
            GameManager.local.StartCoroutine(data.SpawnCoroutine(position, yaw, null, c => spawned = c));
            var start = Time.realtimeSinceStartup;
            while (spawned == null || !spawned.loaded)
            {
                if (Time.realtimeSinceStartup - start > SpawnTimeout)
                {
                    outcome.Fail("NPC '" + step.Args[0] + "' not loaded after " + SpawnTimeout.ToString("0", CultureInfo.InvariantCulture) + "s");
                    yield break;
                }
                yield return new WaitForSecondsRealtime(Poll);
            }
            _npc = spawned;
            var loadedBrain = spawned.brain?.instance?.id;
            if (brainId != null && !string.Equals(loadedBrain, brainId, StringComparison.OrdinalIgnoreCase))
            {
                outcome.Fail("brain '" + brainId + "' did not load (has '" + (loadedBrain ?? "none") + "')");
                yield break;
            }
            var hitReaction = spawned.brain?.instance?.GetModule<BrainModuleHitReaction>(autoAddModuleIfMissing: false) != null;
            outcome.Detail = "'" + spawned.data.id + "' brain '" + (spawned.brain?.instance?.id ?? "none") + "'"
                + (containerId != null ? " container '" + containerId + "'" : "")
                + (hitReaction ? " with" : " WITHOUT") + " BrainModuleHitReaction, "
                + Vector3.Distance(player.transform.position, spawned.transform.position).ToString("0.0", CultureInfo.InvariantCulture) + " m away, "
                + "turned " + Vector3.SignedAngle(new Vector3(-x, 0f, -z), spawned.transform.forward, Vector3.up).ToString("0", CultureInfo.InvariantCulture) + " deg from facing the player";
        }

        /// <summary>
        /// Moves the player forward through the game's own locomotion: the call the gamepad input makes
        /// (InputGamepad: locomotion.MoveWeighted(dir, body, 1, 1, sprinting ? 1 : 0)), repeated each frame
        /// until stop-run. The ratio is the run share (0 walk, 1 full sprint).
        /// </summary>
        private IEnumerator Run(Step step, Outcome outcome)
        {
            if (Player.local?.locomotion == null || Player.local.creature == null)
            {
                outcome.Fail("no player locomotion (wait-level first)");
                yield break;
            }
            ScenarioParser.TryParseNumber(step.Args[0], out var ratio);
            var alreadyRunning = _runRatio >= 0f;
            _runRatio = ratio;
            if (!alreadyRunning)
            {
                GameManager.local.StartCoroutine(Drive());
            }
            outcome.Detail = "moving forward, run ratio " + ratio.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private IEnumerator Drive()
        {
            while (_runRatio >= 0f && Player.local?.locomotion != null && Player.local.creature != null)
            {
                var body = Player.local.creature.transform;
                Player.local.locomotion.MoveWeighted(body.forward, body, 1f, 1f, _runRatio);
                yield return null;
            }
        }

        private void StopRun(Outcome outcome)
        {
            _runRatio = -1f;
            Player.local?.locomotion?.MoveStop();
            outcome.Detail = "stopped";
        }

        /// <summary>
        /// Stands in for a telekinesis throw of the spawned NPC by the player's right hand: the NPC is destabilized, every
        /// part gets speed m/s (default 8) away from the player and slightly up, then the game's own static
        /// SpellTelekinesis.onTelekinesisUngrabEvent is raised (through reflection: a C# event cannot be raised from outside
        /// its class) with that hand's caster and telekinesis, a HandleRagdoll of the NPC and thrown = true. Tests a mod's
        /// listening and the flight; a real telekinesis grab and swing need the headset.
        /// </summary>
        private void NpcTkThrow(Step step, Outcome outcome)
        {
            var player = Player.currentCreature;
            if (player == null || _npc == null || _npc.ragdoll == null)
            {
                outcome.Fail(player == null ? "no player (wait-level first)" : "no NPC (spawn-npc first)");
                return;
            }
            var caster = player.handRight?.caster;
            var tk = caster?.telekinesis;
            var handle = _npc.GetComponentsInChildren<HandleRagdoll>().FirstOrDefault();
            if (tk == null || handle == null)
            {
                outcome.Fail(tk == null ? "the player's right hand has no telekinesis" : "the NPC has no ragdoll handle");
                return;
            }
            var away = _npc.transform.position - player.transform.position;
            away.y = 0f;
            var dir = ((away.sqrMagnitude > 1e-4f ? away.normalized : player.transform.forward) + (Vector3.up * 0.3f)).normalized;
            var speed = Number(step, "speed", 8f);
            _npc.ragdoll.SetState(Ragdoll.State.Destabilized);
            foreach (var part in _npc.ragdoll.parts)
            {
                part.physicBody?.AddForce(dir * speed, ForceMode.VelocityChange);
            }
            var field = typeof(ThunderRoad.Skill.SpellPower.SpellTelekinesis).GetField("onTelekinesisUngrabEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var raise = field?.GetValue(null) as Delegate;
            raise?.DynamicInvoke(caster, tk, Side.Right, handle, true);
            outcome.Detail = _npc.name + " flung at " + speed.ToString("0.#", CultureInfo.InvariantCulture) + " m/s, release event "
                + (field == null ? "NOT FOUND" : raise == null ? "has no listener" : "raised to " + raise.GetInvocationList().Length + " listener(s)");
        }

        /// <summary>
        /// Casts a spell from the player's hand (right by default) at the spawned NPC through the game's own spell code:
        /// the spell is loaded on the hand's SpellCaster (SpellCaster.LoadSpell, so Mana raises OnSpellLoadEvent as for a
        /// real cast), then Fire throws a fireball (SpellCastProjectile.ThrowProjectile, unguided, speed m/s, default 15),
        /// Lightning strikes the NPC's chest with one bolt (SpellCastLightning.Hit plus PlayBolt for the look; a real spray
        /// needs an aimed controller), Gravity pushes towards it at full charge (SpellCastGravity.Throw, speed default 8).
        /// </summary>
        private IEnumerator PlayerCast(Step step, Outcome outcome)
        {
            var player = Player.currentCreature;
            if (player == null || _npc == null || _npc.ragdoll == null)
            {
                outcome.Fail(player == null ? "no player (wait-level first)" : "no NPC (spawn-npc first)");
                yield break;
            }
            var hand = step.Named.TryGetValue("hand", out var side) && side == "left" ? player.handLeft : player.handRight;
            var caster = hand?.caster;
            if (caster == null)
            {
                outcome.Fail("the player's hand has no spell caster");
                yield break;
            }
            if (!Catalog.TryGetData<SpellCastData>(step.Args[0], out var data, false) || data == null)
            {
                outcome.Fail("spell '" + step.Args[0] + "' is not in the catalog");
                yield break;
            }
            caster.LoadSpell(data);
            // Let the spell settle on the hand for a few frames: right after LoadSpell the caster's Orb (where the gravity
            // push starts) can still sit metres away (seen: 5.84 m from the NPC with the NPC 2 m from the player).
            for (var f = 0; f < 5; f++)
            {
                yield return null;
            }
            var target = _npc.ragdoll.targetPart.transform.position;
            // From the hand, not the caster's Orb: right after LoadSpell the Orb can still sit metres away (seen: 5.84 m).
            var from = hand.transform.position;
            var dir = (target - from).normalized;
            switch (caster.spellInstance)
            {
                case SpellCastProjectile fire:
                    var speed = Number(step, "speed", 15f);
                    // Launched 1.5 m in front of the NPC's chest, on the line from the hand: a projectile spawned inside or
                    // right at the NPC's colliders starts overlapped and never reports the hit (seen at 0.24 m).
                    var start = target - (dir * Mathf.Min(1.5f, Vector3.Distance(from, target)));
                    fire.ThrowProjectile(start, Quaternion.LookRotation(dir), dir * speed);
                    outcome.Detail = "fireball thrown at " + speed.ToString("0.#", CultureInfo.InvariantCulture) + " m/s from "
                        + Vector3.Distance(start, target).ToString("0.00", CultureInfo.InvariantCulture) + " m before " + _npc.name + "'s chest";
                    yield break;
                case SpellCastLightning bolt:
                    bolt.Hit(_npc.ragdoll.targetPart.colliderGroup, target, -dir, dir * 10f, 1f, false);
                    bolt.PlayBolt(from, target);
                    outcome.Detail = "a bolt hit " + _npc.name + "'s " + _npc.ragdoll.targetPart.type;
                    yield break;
                case SpellCastGravity gravity:
                    var push = Number(step, "speed", 8f);
                    gravity.currentCharge = 1f;
                    // The push's overlap sphere starts at the caster's Orb, which the mock headset leaves far from the hand
                    // (seen: 14.53 m from the NPC). For the push only, the Orb goes 1.2 m in front of the NPC's chest on
                    // the line from the hand, then back.
                    var orb = caster.Orb;
                    var wasAt = orb != null ? orb.position : Vector3.zero;
                    var orbAway = orb != null ? Vector3.Distance(wasAt, target) : -1f;
                    if (orb != null)
                    {
                        orb.position = target - (dir * 1.2f);
                    }
                    gravity.Throw(dir * push);
                    if (orb != null)
                    {
                        orb.position = wasAt;
                    }
                    outcome.Detail = "gravity push towards " + _npc.name + " at full charge from 1.2 m before its chest (the orb was "
                        + orbAway.ToString("0.00", CultureInfo.InvariantCulture) + " m away)";
                    yield break;
                default:
                    outcome.Fail("spell '" + step.Args[0] + "' loaded as " + (caster.spellInstance?.GetType().Name ?? "nothing") + ", which player-cast cannot cast");
                    yield break;
            }
        }

        /// <summary>
        /// Gives the player a skill or takes it away, the way buying and refunding at the skill tree does: through the
        /// player creature's container (Creature.TryAddSkill / TryRemoveSkill), whose change the game's Mana answers
        /// with Creature.ForceLoadSkill / ForceUnloadSkill (SkillData.OnSkillLoadedEvent / OnSkillUnloadedEvent).
        /// </summary>
        private static void PlayerSkill(Step step, Outcome outcome)
        {
            var id = step.Args[0];
            var want = step.Args[1] == "true";
            var creature = Player.currentCreature;
            if (creature == null)
            {
                outcome.Fail("no player creature");
                return;
            }
            if (!Catalog.TryGetData<SkillData>(id, out var data, false) || data == null)
            {
                outcome.Fail("skill '" + id + "' is not in the catalog");
                return;
            }
            var ok = want ? creature.TryAddSkill(data) : creature.TryRemoveSkill(data);
            var has = creature.HasSkill(id);
            if (!ok || has != want)
            {
                outcome.Fail("player-skill " + id + " " + step.Args[1] + ": the player " + (has ? "still has" : "does not have") + " it");
                return;
            }
            outcome.Detail = "the player " + (want ? "has" : "no longer has") + " the skill " + id + " (" + data.primarySkillTreeId + " tier " + (data.tier + 1) + ")";
        }

        /// <summary>
        /// Turns the player on the spot (clockwise seen from above) with the game's own Player.Teleport,
        /// so runs can go back and forth inside the level instead of into a wall.
        /// </summary>
        private static void TurnPlayer(Step step, Outcome outcome)
        {
            var player = Player.local;
            if (player == null || player.creature == null)
            {
                outcome.Fail("no player (wait-level first)");
                return;
            }
            ScenarioParser.TryParseNumber(step.Args[0], out var degrees);
            var before = player.creature.transform.eulerAngles.y;
            player.Teleport(player.transform.position, player.transform.rotation * Quaternion.Euler(0f, degrees, 0f), keepVelocity: false, useHeadPosition: false);
            outcome.Detail = "body yaw " + before.ToString("0", CultureInfo.InvariantCulture) + " -> rig turned " + degrees.ToString("0", CultureInfo.InvariantCulture) + " deg";
        }

        /// <summary>Locomotion.horizontalSpeed (m/s, measured by the game each frame) reaches the range within 5 s.</summary>
        private static IEnumerator ExpectPlayerSpeed(Step step, Outcome outcome)
        {
            ScenarioParser.TryParseNumber(step.Args[0], out var min);
            float? max = null;
            if (step.Args.Count > 1)
            {
                ScenarioParser.TryParseNumber(step.Args[1], out var m);
                max = m;
            }
            var start = Time.realtimeSinceStartup;
            var peak = 0f;
            while (true)
            {
                var speed = Player.local?.locomotion?.horizontalSpeed ?? 0f;
                peak = Mathf.Max(peak, speed);
                if (CombatMath.InRange(speed, min, max))
                {
                    outcome.Detail = "horizontal speed " + speed.ToString("0.00", CultureInfo.InvariantCulture) + " m/s";
                    yield break;
                }
                if (Time.realtimeSinceStartup - start > SpeedTimeout)
                {
                    outcome.Fail("horizontal speed " + speed.ToString("0.00", CultureInfo.InvariantCulture) + " m/s (peak "
                        + peak.ToString("0.00", CultureInfo.InvariantCulture) + ") not in range within " + SpeedTimeout.ToString("0", CultureInfo.InvariantCulture) + "s");
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>
        /// A hit by the player's right hand on a part of the spawned NPC, through Creature.Damage — the
        /// method a real collision ends in (it raises EventManager.onCreatureHit OnStart and OnEnd).
        /// No physics contact happens: the hit is attributed to the player through the damager and
        /// source collider group of the player's hand, as a punch is.
        /// push = DamageStruct.pushLevel; the default -1 makes the game's own hit reaction skip its push
        /// (BrainModuleHitReaction.TryPush returns on pushLevel &lt; 0), so a stagger seen afterwards came
        /// from the mod under test.
        /// </summary>
        private const string DefaultHitItem = "DaggerCommon";

        /// <summary>m/s and seconds of the controller swing before a punch (hit-npc by=hand).</summary>
        private const float DefaultSwing = 3f;
        private const float SwingSeconds = 0.12f;

        /// <summary>Where a hit-npc lands: the part's position, moved by at= metres along the NPC's own right, up and forward.</summary>
        private Vector3 ContactOn(RagdollPart part, Step step)
        {
            var contact = part.transform.position;
            if (step.Named.TryGetValue("at", out var atText) && ScenarioParser.TryParseVector(atText, out var atRight, out var atUp, out var atForward))
            {
                var body = _npc.transform;
                contact += (body.right * atRight) + (body.up * atUp) + (body.forward * atForward);
            }
            return contact;
        }

        private IEnumerator HitNpc(Step step, Outcome outcome)
        {
            var allowDead = step.Named.TryGetValue("dead", out var deadText) && deadText.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (_npc == null || (_npc.isKilled && !allowDead))
            {
                outcome.Fail(_npc == null ? "no NPC (spawn-npc first)" : "the NPC is dead");
                yield break;
            }
            if (!Enum.TryParse(step.Args[0], true, out RagdollPart.Type type))
            {
                outcome.Fail("'" + step.Args[0] + "' is not a body part (" + string.Join(", ", Enum.GetNames(typeof(RagdollPart.Type))) + ")");
                yield break;
            }
            var section = RagdollPart.Section.Full;
            if (step.Named.TryGetValue("section", out var sectionText))
            {
                Enum.TryParse(sectionText, true, out section);
            }
            var part = _npc.ragdoll.GetPart(type, section);
            if (part == null)
            {
                outcome.Fail("the NPC has no " + type + (section != RagdollPart.Section.Full ? " (" + section + ")" : string.Empty));
                yield break;
            }
            var contact = ContactOn(part, step);
            var hand = Player.local?.creature?.handRight;
            if (hand == null)
            {
                outcome.Fail("no player hand");
                yield break;
            }
            var damageType = DamageType.Blunt;
            if (step.Named.TryGetValue("type", out var typeText) && !Enum.TryParse(typeText, true, out damageType))
            {
                outcome.Fail("'" + typeText + "' is not a DamageType (" + string.Join(", ", Enum.GetNames(typeof(DamageType))) + ")");
                yield break;
            }
            var by = step.Named.TryGetValue("by", out var byText) ? byText.ToLowerInvariant() : "hand";

            // What deals the hit: the hand itself, or a spawned item held in it / last held by it.
            Item item = null;
            if (by == "held" || by == "thrown")
            {
                var itemId = step.Named.TryGetValue("item", out var itemText) ? itemText : DefaultHitItem;
                var data = Catalog.GetData<ItemData>(itemId);
                if (data == null)
                {
                    outcome.Fail("no ItemData '" + itemId + "' in the catalog");
                    yield break;
                }
                data.SpawnAsync(i => item = i, hand.transform.position, hand.transform.rotation, null, false, null);
                var start = Time.realtimeSinceStartup;
                while (item == null)
                {
                    if (Time.realtimeSinceStartup - start > SpawnTimeout)
                    {
                        outcome.Fail("item '" + itemId + "' not spawned");
                        yield break;
                    }
                    yield return null;
                }
                if (by == "held")
                {
                    hand.Grab(item.GetMainHandle(Side.Right), true);
                    yield return null;
                    if (!item.handlers.Contains(hand))
                    {
                        outcome.Fail("the player's hand did not grab '" + itemId + "'");
                        item.Despawn();
                        yield break;
                    }
                }
                else
                {
                    // A thrown item: nobody holds it, the player was the last to (what Item.lastHandler records).
                    item.lastHandler = hand;
                }
            }

            var velocity = Number(step, "velocity", 5f);
            var damage = Number(step, "damage", 1f);
            var push = step.Named.TryGetValue("push", out var pushText) ? int.Parse(pushText, CultureInfo.InvariantCulture) : -1;
            // A kick: the player's right foot part deals it (its own damager, e.g. the game's Kick).
            var foot = by == "foot" ? Player.local.creature.ragdoll.GetPart(RagdollPart.Type.RightFoot) : null;
            if (by == "foot" && foot == null)
            {
                outcome.Fail("the player has no right foot part");
                yield break;
            }
            var from = foot != null ? foot.transform.position : hand.transform.position;
            var direction = (contact - from).normalized;
            var damager = item != null
                ? item.mainCollisionHandler?.damagers?.FirstOrDefault()
                : foot != null ? foot.collisionHandler?.damagers?.FirstOrDefault() : (by == "spell" ? null : hand.bodyDamager);
            var sourceGroup = item != null
                ? (damager?.colliderGroup ?? item.colliderGroups.FirstOrDefault())
                : foot != null ? foot.colliderGroup : (by == "spell" ? null : hand.colliderGroup);
            var hit = new CollisionInstance(new DamageStruct(damageType, damage)
            {
                damager = damager,
                hitRagdollPart = part,
                pushLevel = push,
            })
            {
                sourceColliderGroup = sourceGroup,
                targetColliderGroup = part.colliderGroup,
                impactVelocity = direction * velocity,
                contactPoint = contact,
                contactNormal = -direction,
                casterHand = by == "spell" ? hand.caster : null,
            };
            // A punch: the player's real hand (its controller pose) swings first, so a mod that asks how fast the hand
            // moved sees a punch; swing=0 is a hand that was only run into.
            var swing = by == "hand" ? Number(step, "swing", DefaultSwing) : 0f;
            var playerHand = Player.local?.handRight;
            var restPosition = playerHand != null ? playerHand.transform.localPosition : Vector3.zero;
            if (swing > 0f && playerHand != null)
            {
                playerHand.SetTracking(false);
                var swingStart = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - swingStart < SwingSeconds)
                {
                    playerHand.transform.localPosition = restPosition + (Vector3.forward * (swing * (Time.realtimeSinceStartup - swingStart)));
                    yield return null;
                }
            }
            if (swing > 0f)
            {
                // The NPC may have moved during the swing (getting up, walking): aim at where the part is now.
                contact = ContactOn(part, step);
                direction = (contact - from).normalized;
                hit.contactPoint = contact;
                hit.contactNormal = -direction;
                hit.impactVelocity = direction * velocity;
            }
            var before = _npc.currentHealth;
            _npc.Damage(hit);
            if (swing > 0f && playerHand != null)
            {
                playerHand.transform.localPosition = restPosition;
                playerHand.SetTracking(true);
            }
            outcome.Detail = type + " " + damageType + " hit by " + by + (item != null ? " '" + item.data.id + "'" : string.Empty)
                + " at " + velocity.ToString("0.#", CultureInfo.InvariantCulture) + " m/s, health "
                + before.ToString("0.#", CultureInfo.InvariantCulture) + " -> " + _npc.currentHealth.ToString("0.#", CultureInfo.InvariantCulture)
                + (by == "hand" ? ", hand swing " + swing.ToString("0.#", CultureInfo.InvariantCulture) + " m/s" : string.Empty)
                + ", player speed " + (Player.local?.locomotion?.horizontalSpeed ?? 0f).ToString("0.00", CultureInfo.InvariantCulture) + " m/s";
            if (item != null)
            {
                if (by == "held")
                {
                    hand.UnGrab(false);
                }
                item.Despawn();
            }
        }

        private IEnumerator ExpectNpc(Step step, Outcome outcome)
        {
            if (_npc == null)
            {
                outcome.Fail("no NPC (spawn-npc first)");
                yield break;
            }
            var within = DefaultNpcWithin;
            if (step.Named.TryGetValue("within", out var withinText))
            {
                ScenarioParser.TryParseSeconds(withinText, out within);
            }
            var start = Time.realtimeSinceStartup;
            while (true)
            {
                var actual = NpcValue(_npc, step.Args[0]);
                if (NpcProperty.Matches(step.Args[1], actual))
                {
                    outcome.Detail = step.Args[0] + " = " + actual + " (" + NpcSummary(_npc) + ")";
                    yield break;
                }
                if (Time.realtimeSinceStartup - start > within)
                {
                    outcome.Fail(step.Args[0] + " is '" + actual + "' (" + NpcSummary(_npc) + ")");
                    yield break;
                }
                yield return null;
            }
        }

        private static string NpcValue(Creature npc, string property)
        {
            switch (property.ToLowerInvariant())
            {
                case "stagger":
                    return (npc.brain != null ? npc.brain.currentStagger : Brain.Stagger.None).ToString();
                case "state":
                    return npc.state.ToString();
                case "holding":
                    return (Holds(npc.handLeft) || Holds(npc.handRight)) ? "True" : "False";
                case "brain":
                    return npc.brain?.instance?.id ?? "none";
                case "tree":
                    return npc.brain?.instance != null && npc.brain.instance.updateTree ? "True" : "False";
                default:
                    var staggered = (npc.brain != null && npc.brain.currentStagger != Brain.Stagger.None) || npc.state == Creature.State.Destabilized;
                    return staggered ? "True" : "False";
            }
        }

        private static string NpcSummary(Creature npc)
        {
            return "stagger " + (npc.brain != null ? npc.brain.currentStagger : Brain.Stagger.None) + ", state " + npc.state
                + ", holding L '" + HeldId(npc.handLeft) + "' R '" + HeldId(npc.handRight) + "', brain " + (npc.brain?.instance?.id ?? "none");
        }

        private static bool Holds(RagdollHand hand)
        {
            return hand != null && hand.grabbedHandle != null && hand.grabbedHandle.item != null;
        }

        private static string HeldId(RagdollHand hand)
        {
            return Holds(hand) ? hand.grabbedHandle.item.data?.id ?? "?" : "-";
        }

        /// <summary>Puts a spawned item into the NPC's hand (RagdollHand.Grab, teleported to the hand).</summary>
        private IEnumerator NpcGrab(Step step, Outcome outcome)
        {
            if (_npc == null || _npc.isKilled)
            {
                outcome.Fail(_npc == null ? "no NPC (spawn-npc first)" : "the NPC is dead");
                yield break;
            }
            var side = step.Named.TryGetValue("hand", out var handText) && handText.Equals("left", StringComparison.OrdinalIgnoreCase) ? Side.Left : Side.Right;
            var hand = _npc.GetHand(side);
            var data = Catalog.GetData<ItemData>(step.Args[0]);
            if (data == null || hand == null)
            {
                outcome.Fail(data == null ? "no ItemData '" + step.Args[0] + "' in the catalog" : "the NPC has no " + side + " hand");
                yield break;
            }
            if (Holds(hand))
            {
                hand.TryRelease();
            }
            Item item = null;
            data.SpawnAsync(i => item = i, hand.transform.position, hand.transform.rotation, null, false, null);
            var start = Time.realtimeSinceStartup;
            while (item == null)
            {
                if (Time.realtimeSinceStartup - start > SpawnTimeout)
                {
                    outcome.Fail("item '" + step.Args[0] + "' not spawned");
                    yield break;
                }
                yield return null;
            }
            _npcItem = item;
            hand.Grab(item.GetMainHandle(side), true);
            // The step checks the grab itself, right after Grab and again one frame later: a mod under test may
            // release it again on its next update (e.g. a disarmed NPC); whether the hand still holds it is
            // for expect-npc holding to judge.
            var grabbedAtOnce = item.handlers.Contains(hand);
            yield return null;
            var holdsNow = item.handlers.Contains(hand);
            if (!grabbedAtOnce && !holdsNow)
            {
                outcome.Fail("the NPC's " + side + " hand did not grab '" + step.Args[0] + "'");
                yield break;
            }
            outcome.Detail = side + " hand " + (holdsNow ? "holds '" : "grabbed and released again by the next frame '") + item.data.id + "'";
        }

        /// <summary>
        /// Raises the game's own attack event (EventManager.InvokeCreatureAttack, as BrainModuleMelee does) for the
        /// spawned NPC against the player, melee. Only the event: the NPC does not swing.
        /// </summary>
        private void NpcAttack(Step step, Outcome outcome)
        {
            var player = Player.local != null ? Player.local.creature : null;
            if (_npc == null || player == null)
            {
                outcome.Fail(_npc == null ? "no NPC (spawn-npc first)" : "no player creature");
                return;
            }
            var stage = (BrainModuleAttack.AttackStage)Enum.Parse(typeof(BrainModuleAttack.AttackStage), step.Args[0], true);
            EventManager.InvokeCreatureAttack(_npc, player, player.transform, BrainModuleAttack.AttackType.Melee, stage);
            outcome.Detail = "raised " + stage + " (melee) from the NPC at the player";
        }

        /// <summary>The player's hand grabs the item the last npc-grab gave the NPC (RagdollHand.Grab, teleported to the hand).</summary>
        private IEnumerator PlayerGrabDropped(Step step, Outcome outcome)
        {
            var player = Player.local != null ? Player.local.creature : null;
            if (_npcItem == null || player == null)
            {
                outcome.Fail(_npcItem == null ? "no item (npc-grab first)" : "no player creature");
                yield break;
            }
            if (_npcItem.handlers.Any(h => h != null && h.creature == _npc))
            {
                outcome.Fail("the NPC still holds '" + _npcItem.data.id + "'");
                yield break;
            }
            var side = step.Named.TryGetValue("hand", out var handText) && handText.Equals("left", StringComparison.OrdinalIgnoreCase) ? Side.Left : Side.Right;
            var hand = player.GetHand(side);
            if (Holds(hand))
            {
                hand.TryRelease();
            }
            hand.Grab(_npcItem.GetMainHandle(side), true);
            yield return null;
            if (!_npcItem.handlers.Contains(hand))
            {
                outcome.Fail("the player's " + side + " hand did not grab '" + _npcItem.data.id + "'");
                yield break;
            }
            outcome.Detail = "player's " + side + " hand holds '" + _npcItem.data.id + "'";
        }

        /// <summary>The player's hand grabs a newly spawned item (RagdollHand.Grab, teleported to the hand).</summary>
        /// <summary>
        /// player-strike-weapon [twist=°/s] [seconds=0.2] [approach=0.6] [settle=0.7] [depth=0.15] [phase=all|ready|strike]: the player's
        /// right controller brings
        /// the held blade's middle beside the spawned NPC's held blade (0.35 m out, away from its body, in the approach
        /// seconds, then the settle seconds to let the hand catch up), then strikes
        /// through it (to 0.15 m on the body's side) in the seconds, rolling around the held blade's long axis at the twist
        /// rate; then back over 0.5 s, tracking again. phase=ready stops beside the blade (tracking off); a later
        /// phase=strike strikes from there (a stagger can come in between). For a mod that reacts
        /// to blade-on-blade contact (the game's own collision events) and to a wrist twist.
        /// </summary>
        private sealed class StrikeSetup
        {
            public PlayerHand Hand;
            public Item Mine;
            public Item Theirs;
            public RagdollHand NpcHand;
            public Vector3 MyMiddle;
            public Vector3 Axis;
            public Vector3 Outward;
            public Vector3 RestPosition;
            public Quaternion RestRotation;
        }

        /// <summary>A player-strike-weapon phase=ready waiting for its phase=strike.</summary>
        private StrikeSetup _strikeReady;

        private static Vector3 BladeMiddle(Item item, Vector3 grip)
        {
            var tip = item.collisionHandlers.SelectMany(h => h.damagers).Where(d => d != null)
                .Select(d => d.transform.position).OrderByDescending(p => (p - grip).sqrMagnitude).DefaultIfEmpty(item.transform.position).First();
            return (grip + tip) / 2f;
        }

        /// <summary>Where the controller must be for the player's blade middle to sit at a point (0.35 m out = beside, -0.15 m = through).</summary>
        private static Vector3 ControllerFor(StrikeSetup s, float outwards)
        {
            var theirMiddle = BladeMiddle(s.Theirs, s.NpcHand.grabbedHandle.transform.position);
            return s.RestPosition + (theirMiddle + (s.Outward * outwards) - s.MyMiddle);
        }

        private IEnumerator PlayerStrikeWeapon(Step step, Outcome outcome)
        {
            var phase = step.Named.TryGetValue("phase", out var ph) ? ph : "all";
            var s = phase == "strike" ? _strikeReady : null;
            _strikeReady = null;
            if (s == null)
            {
                var player = Player.local;
                var hand = player != null ? player.handRight : null;
                var ragdollHand = player != null && player.creature != null ? player.creature.handRight : null;
                var mine = ragdollHand != null ? ragdollHand.grabbedHandle?.item : null;
                var npcHand = _npc == null ? null : (Holds(_npc.handRight) ? _npc.handRight : Holds(_npc.handLeft) ? _npc.handLeft : null);
                var theirs = npcHand != null ? npcHand.grabbedHandle.item : null;
                if (hand == null || mine == null || theirs == null)
                {
                    outcome.Fail(mine == null ? "the player holds nothing in the right hand" : "the NPC holds nothing (npc-grab first)");
                    yield break;
                }
                var myGrip = ragdollHand.grabbedHandle.transform.position;
                var myMiddle = BladeMiddle(mine, myGrip);
                // Come at the NPC's blade from outside its body: first bring the blade beside it (away from the torso),
                // then strike through it towards the body.
                var body = _npc.ragdoll != null && _npc.ragdoll.rootPart != null ? _npc.ragdoll.rootPart.transform.position : _npc.transform.position;
                var outward = BladeMiddle(theirs, npcHand.grabbedHandle.transform.position) - body;
                outward.y = 0f;
                s = new StrikeSetup
                {
                    Hand = hand,
                    Mine = mine,
                    Theirs = theirs,
                    NpcHand = npcHand,
                    MyMiddle = myMiddle,
                    Axis = (myMiddle - myGrip).normalized,
                    Outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : -_npc.transform.right,
                    RestPosition = hand.transform.position,
                    RestRotation = hand.transform.rotation,
                };
                var ready = ControllerFor(s, 0.35f);
                s.Hand.SetTracking(false);
                var begin = Time.realtimeSinceStartup;
                var approach = Mathf.Max(0.05f, Number(step, "approach", 0.6f));
                while (Time.realtimeSinceStartup - begin < approach)
                {
                    s.Hand.transform.position = Vector3.Lerp(s.RestPosition, ready, (Time.realtimeSinceStartup - begin) / approach);
                    yield return null;
                }
                // Let the heavy hand catch up.
                yield return new WaitForSecondsRealtime(Number(step, "settle", 0.7f));
                if (phase == "ready")
                {
                    // Stays there, tracking off, until phase=strike (for a stagger or anything else in between).
                    _strikeReady = s;
                    outcome.Detail = "blade ready beside the NPC's '" + theirs.data.id + "', waiting for phase=strike";
                    yield break;
                }
            }
            if (s.NpcHand.grabbedHandle == null || s.Mine == null)
            {
                s.Hand.SetTracking(true);
                outcome.Fail("the NPC's or the player's weapon is gone before the strike");
                yield break;
            }
            var twist = Number(step, "twist", 0f);
            var seconds = Number(step, "seconds", 0.2f);
            var from = s.Hand.transform.position;
            var start = Time.realtimeSinceStartup;
            var through = from;
            while (Time.realtimeSinceStartup - start < seconds)
            {
                var t = (Time.realtimeSinceStartup - start) / seconds;
                // Follow the NPC's blade while striking: a staggered NPC sways, and a target fixed at the start missed.
                if (s.NpcHand.grabbedHandle != null)
                {
                    through = ControllerFor(s, -Number(step, "depth", 0.15f));
                }
                s.Hand.transform.position = Vector3.Lerp(from, through, t);
                s.Hand.transform.rotation = Quaternion.AngleAxis(twist * (Time.realtimeSinceStartup - start), s.Axis) * s.RestRotation;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.2f);
            // Back slowly (0.5 s): an instant snap back hit things at up to 29 m/s and rolled the hand at ~6000°/s
            // (seen 2026-11-20), which a mod measuring the wrist would take for a real twist.
            var endPosition = s.Hand.transform.position;
            var endRotation = s.Hand.transform.rotation;
            start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 0.5f)
            {
                var t = (Time.realtimeSinceStartup - start) / 0.5f;
                s.Hand.transform.position = Vector3.Lerp(endPosition, s.RestPosition, t);
                s.Hand.transform.rotation = Quaternion.Slerp(endRotation, s.RestRotation, t);
                yield return null;
            }
            s.Hand.transform.position = s.RestPosition;
            s.Hand.transform.rotation = s.RestRotation;
            s.Hand.SetTracking(true);
            outcome.Detail = "swept '" + s.Mine.data.id + "' " + (through - from).magnitude.ToString("0.00", CultureInfo.InvariantCulture) + " m through the NPC's '"
                + s.Theirs.data.id + "' in " + seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s, twisting "
                + twist.ToString("0", CultureInfo.InvariantCulture) + "°/s around the blade";
        }

        /// <summary>
        /// player-stab &lt;part&gt; [section=Full] [speed=2] [depth=0.15] [from=0.3] [step=true]: the player's right controller
        /// turns the held blade so its tip points at that part of the spawned NPC from the player's chest, brings the tip
        /// `from` m short of the part (0.6 s, then 0.6 s for the hand to catch up), then thrusts along the blade through the
        /// part to `depth` m past it at `speed` m/s; holds 0.3 s, then back over 0.8 s. step=true first moves the player
        /// (Player.Teleport) to 0.45 m beside the part, facing it, as a player walks up to an enemy on the ground. Real
        /// physics: the game's own collision, penetration and hit events decide what the thrust does.
        /// </summary>
        private IEnumerator PlayerStab(Step step, Outcome outcome)
        {
            var player = Player.local;
            var hand = player != null ? player.handRight : null;
            var ragdollHand = player != null && player.creature != null ? player.creature.handRight : null;
            var mine = ragdollHand != null ? ragdollHand.grabbedHandle?.item : null;
            if (_npc == null || hand == null || mine == null)
            {
                outcome.Fail(_npc == null ? "no NPC (spawn-npc first)" : "the player holds nothing in the right hand");
                yield break;
            }
            Enum.TryParse(step.Args[0], true, out RagdollPart.Type type);
            var section = RagdollPart.Section.Full;
            if (step.Named.TryGetValue("section", out var sectionText))
            {
                Enum.TryParse(sectionText, true, out section);
            }
            var part = _npc.ragdoll.GetPart(type, section);
            if (part == null)
            {
                outcome.Fail("the NPC has no " + step.Args[0]);
                yield break;
            }
            var speed = Number(step, "speed", 2f);
            var depth = Number(step, "depth", 0.15f);
            var from = Number(step, "from", 0.3f);
            if (!step.Named.TryGetValue("step", out var stepText) || stepText != "false")
            {
                var toPart = part.transform.position - player.creature.transform.position;
                toPart.y = 0f;
                var facing = toPart.sqrMagnitude > 1e-4f ? toPart.normalized : player.creature.transform.forward;
                var stand = part.transform.position - facing * 0.45f;
                var shift = stand - player.creature.transform.position;
                shift.y = 0f;
                player.Teleport(player.transform.position + shift, Quaternion.LookRotation(facing, Vector3.up), keepVelocity: false, useHeadPosition: false);
                yield return new WaitForSecondsRealtime(0.5f);
            }
            var restPosition = hand.transform.position;
            var restRotation = hand.transform.rotation;
            var grip = ragdollHand.grabbedHandle.transform.position;
            var tip = BladeTip(mine, grip);
            var target = part.transform.position;
            var chest = player.creature.ragdoll.headPart.transform.position - Vector3.up * 0.35f;
            var thrust = (target - chest).normalized;
            var turn = Quaternion.FromToRotation((tip - grip).normalized, thrust);
            var ready = target - thrust * from - turn * (tip - restPosition);
            var readyRotation = turn * restRotation;
            var startDistance = (tip - target).magnitude;
            hand.SetTracking(false);
            var begin = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - begin < 0.6f)
            {
                var t = (Time.realtimeSinceStartup - begin) / 0.6f;
                hand.transform.position = Vector3.Lerp(restPosition, ready, t);
                hand.transform.rotation = Quaternion.Slerp(restRotation, readyRotation, t);
                yield return null;
            }
            hand.transform.position = ready;
            hand.transform.rotation = readyRotation;
            yield return new WaitForSecondsRealtime(0.6f);
            var readyTip = mine != null && ragdollHand.grabbedHandle != null ? BladeTip(mine, ragdollHand.grabbedHandle.transform.position) : tip;
            var length = from + depth;
            var through = ready + thrust * length;
            var closest = float.MaxValue;
            begin = Time.realtimeSinceStartup;
            var seconds = length / speed;
            while (Time.realtimeSinceStartup - begin < seconds)
            {
                hand.transform.position = Vector3.Lerp(ready, through, (Time.realtimeSinceStartup - begin) / seconds);
                if (ragdollHand.grabbedHandle != null)
                {
                    closest = Mathf.Min(closest, (BladeTip(mine, ragdollHand.grabbedHandle.transform.position) - part.transform.position).magnitude);
                }
                yield return null;
            }
            hand.transform.position = through;
            yield return new WaitForSecondsRealtime(0.3f);
            begin = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - begin < 0.8f)
            {
                var t = (Time.realtimeSinceStartup - begin) / 0.8f;
                hand.transform.position = Vector3.Lerp(through, restPosition, t);
                hand.transform.rotation = Quaternion.Slerp(readyRotation, restRotation, t);
                yield return null;
            }
            hand.transform.position = restPosition;
            hand.transform.rotation = restRotation;
            hand.SetTracking(true);
            outcome.Detail = "thrust '" + mine.data.id + "' " + length.ToString("0.00", CultureInfo.InvariantCulture) + " m at "
                + speed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s towards the NPC's " + part.type + ", "
                + (Vector3.Angle(thrust, Vector3.down)).ToString("0", CultureInfo.InvariantCulture) + " deg from straight down; tip "
                + startDistance.ToString("0.00", CultureInfo.InvariantCulture) + " m from the part before, "
                + (readyTip - target).magnitude.ToString("0.00", CultureInfo.InvariantCulture) + " m when ready (aim "
                + from.ToString("0.00", CultureInfo.InvariantCulture) + "), closest " + closest.ToString("0.00", CultureInfo.InvariantCulture) + " m";
        }

        private static Vector3 BladeTip(Item item, Vector3 grip)
        {
            return item.collisionHandlers.SelectMany(h => h.damagers).Where(d => d != null)
                .Select(d => d.transform.position).OrderByDescending(p => (p - grip).sqrMagnitude).DefaultIfEmpty(item.transform.position).First();
        }

        private static float _frameCap;
        private static bool _limiterRunning;

        /// <summary>
        /// frame-rate &lt;fps&gt;: caps the frame rate for the rest of the run with a frame limiter of its own (at the end of
        /// each frame it waits until 1/fps s have passed since the last). Application.targetFrameRate alone did nothing:
        /// with XR on, the XR runtime paces frames and the game kept running at 330-1000 fps (Achilles run 2026-11-20).
        /// Measures the frame rate over the next second and reports it.
        /// </summary>
        private IEnumerator FrameRate(Step step, Outcome outcome)
        {
            _frameCap = int.Parse(step.Args[0], CultureInfo.InvariantCulture);
            if (!_limiterRunning)
            {
                _limiterRunning = true;
                GameManager.local.StartCoroutine(Limit());
            }
            yield return new WaitForSecondsRealtime(0.2f);
            var frames = Time.frameCount;
            var start = Time.realtimeSinceStartup;
            yield return new WaitForSecondsRealtime(1f);
            var measured = (Time.frameCount - frames) / (Time.realtimeSinceStartup - start);
            outcome.Detail = "frame rate capped at " + _frameCap.ToString("0", CultureInfo.InvariantCulture) + " fps; measured "
                + measured.ToString("0", CultureInfo.InvariantCulture) + " fps over 1 s";
            if (measured > _frameCap * 1.15f)
            {
                outcome.Fail(outcome.Detail + " (the cap does not hold)");
            }
        }

        private static IEnumerator Limit()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var last = watch.Elapsed.TotalSeconds;
            var end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                if (_frameCap <= 0f)
                {
                    last = watch.Elapsed.TotalSeconds;
                    continue;
                }
                var next = last + (1.0 / _frameCap);
                while (watch.Elapsed.TotalSeconds < next - 0.0015)
                {
                    System.Threading.Thread.Sleep(1);
                }
                while (watch.Elapsed.TotalSeconds < next)
                {
                }
                // Catch up without bursting when a frame took longer than its slot.
                last = Math.Max(next, watch.Elapsed.TotalSeconds - (1.0 / _frameCap));
            }
        }

        private Recorder _recorder;

        /// <summary>
        /// record-start &lt;name&gt; [fps=60] [width=640] [height=360] [views=side,first] [distance=2.5] [rise=1.2] [angle=0]:
        /// films until record-stop. distance/rise/angle place the "npc" view as screenshot of=npc places its camera.
        /// </summary>
        private void RecordStart(Step step, Outcome outcome)
        {
            if (_recorder != null)
            {
                outcome.Fail("already filming; record-stop first");
                return;
            }
            var views = step.Named.TryGetValue("views", out var v) ? v.Split(',') : new[] { "side", "first" };
            _recorder = new Recorder(step.Args[0], _outputDir, (int)Number(step, "fps", 60f), (int)Number(step, "width", 640f),
                (int)Number(step, "height", 360f), views, () => _npc,
                Number(step, "distance", 2.5f), Number(step, "rise", 1.2f), Number(step, "angle", 0f));
            GameManager.local.StartCoroutine(_recorder.Run());
            outcome.Detail = "filming " + _recorder.Describe;
        }

        private void RecordStop(Outcome outcome)
        {
            if (_recorder == null)
            {
                outcome.Fail("not filming; record-start first");
                return;
            }
            outcome.Detail = _recorder.Stop();
            _recorder = null;
        }

        /// <summary>player-release [hand=right|left]: the player's hand lets go of what it holds (RagdollHand.TryRelease).</summary>
        private static void PlayerRelease(Step step, Outcome outcome)
        {
            var player = Player.local != null ? Player.local.creature : null;
            var side = step.Named.TryGetValue("hand", out var handText) && handText.Equals("left", StringComparison.OrdinalIgnoreCase) ? Side.Left : Side.Right;
            var hand = player != null ? player.GetHand(side) : null;
            if (hand == null || hand.grabbedHandle == null)
            {
                outcome.Fail("the player's " + side + " hand holds nothing");
                return;
            }
            var item = hand.grabbedHandle.item;
            var npcPart = hand.grabbedHandle is HandleRagdoll ragdollHandle ? ragdollHandle.ragdollPart : null;
            hand.TryRelease();
            if (npcPart != null)
            {
                // player-grab-npc parked the controller on the NPC; it follows the mock headset again.
                (side == Side.Left ? Player.local.handLeft : Player.local.handRight).SetTracking(true);
            }
            outcome.Detail = "player's " + side + " hand let go of " + (npcPart != null ? "the NPC's " + npcPart.type : "'" + (item != null && item.data != null ? item.data.id : "?") + "'");
        }

        private IEnumerator PlayerGrab(Step step, Outcome outcome)
        {
            var player = Player.local != null ? Player.local.creature : null;
            var data = Catalog.GetData<ItemData>(step.Args[0]);
            if (player == null || data == null)
            {
                outcome.Fail(player == null ? "no player creature" : "no ItemData '" + step.Args[0] + "' in the catalog");
                yield break;
            }
            var side = step.Named.TryGetValue("hand", out var handText) && handText.Equals("left", StringComparison.OrdinalIgnoreCase) ? Side.Left : Side.Right;
            var hand = player.GetHand(side);
            Item item = null;
            data.SpawnAsync(i => item = i, hand.transform.position, hand.transform.rotation, null, false, null);
            var start = Time.realtimeSinceStartup;
            while (item == null)
            {
                if (Time.realtimeSinceStartup - start > SpawnTimeout)
                {
                    outcome.Fail("item '" + step.Args[0] + "' not spawned");
                    yield break;
                }
                yield return null;
            }
            if (Holds(hand))
            {
                hand.TryRelease();
            }
            hand.Grab(item.GetMainHandle(side), true);
            yield return null;
            if (!item.handlers.Contains(hand))
            {
                outcome.Fail("the player's " + side + " hand did not grab '" + step.Args[0] + "'");
                yield break;
            }
            outcome.Detail = "player's " + side + " hand holds '" + step.Args[0] + "'";
        }

        /// <summary>
        /// Raises the game's own block event (EventManager.InvokeCreatureAttackParried, as BrainModuleMelee does when an NPC's
        /// swing hits a held weapon) for the spawned NPC's attacking weapon (the hand of its running attack, else the right) against the player's right-hand item, with the
        /// contact at the middle between the NPC's grip and the damager furthest from it, moved `offset` m along the blade. No physics contact.
        /// swing=&lt;m/s&gt;: first the player's right controller (the PlayerHand pose the hand follows) sweeps sideways, to the
        /// player's right (away from the NPC, so the sword touches nothing), at that speed for <see cref="ParrySweepSeconds"/>;
        /// the event comes at the end of the sweep, then the controller glides back and tracks the mock headset again. For a
        /// mod that reads how fast the player's hand moved into the parry.
        /// </summary>
        private IEnumerator PlayerParry(Step step, Outcome outcome)
        {
            var player = Player.local != null ? Player.local.creature : null;
            // The weapon of the NPC's running attack (BrainModuleMelee.attack.attackSide), else its right hand's.
            var attack = _npc?.brain?.instance?.GetModule<BrainModuleMelee>(false)?.attack;
            var npcHand = _npc == null ? null : attack != null && attack.attackSide == Interactable.HandSide.Left ? _npc.handLeft : _npc.handRight;
            var npcItem = npcHand != null ? npcHand.grabbedHandle?.item : null;
            var playerItem = player != null ? player.handRight.grabbedHandle?.item : null;
            if (npcItem == null || playerItem == null)
            {
                outcome.Fail(npcItem == null ? "the NPC holds nothing in its attacking hand" : "the player holds nothing in the right hand");
                yield break;
            }
            var grip = npcHand.grabbedHandle.transform.position;
            // The blade tip: the damager furthest from the grip (no physics module reference needed for its transform).
            var tip = npcItem.collisionHandlers.SelectMany(h => h.damagers).Where(d => d != null)
                .Select(d => d.transform.position).OrderByDescending(p => (p - grip).sqrMagnitude).DefaultIfEmpty(npcItem.transform.position).First();
            var axis = (tip - grip).normalized;
            var point = ((grip + tip) / 2f) + (axis * Number(step, "offset", 0f));
            var swing = Number(step, "swing", 0f);
            var hand = Player.local.handRight;
            var rest = hand.transform.position;
            var swept = 0f;
            if (swing > 0f)
            {
                var side = player.transform.right;
                side.y = 0f;
                side = side.sqrMagnitude > 1e-4f ? side.normalized : Vector3.right;
                hand.SetTracking(false);
                var start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < ParrySweepSeconds)
                {
                    swept = Time.realtimeSinceStartup - start;
                    hand.transform.position = rest + (side * (swing * swept));
                    yield return null;
                }
                swept = Time.realtimeSinceStartup - start;
                hand.transform.position = rest + (side * (swing * swept));
                yield return null;
            }
            var hit = new CollisionInstance(new DamageStruct(DamageType.Blunt, 0f))
            {
                sourceColliderGroup = npcItem.colliderGroups.FirstOrDefault(),
                targetColliderGroup = playerItem.colliderGroups.FirstOrDefault(),
                impactVelocity = (player.transform.position - _npc.transform.position).normalized * 3f,
                contactPoint = point,
            };
            EventManager.InvokeCreatureAttackParried(_npc, npcItem, player, playerItem, hit);
            outcome.Detail = "'" + npcItem.data.id + "' blocked by '" + playerItem.data.id + "' at " + point.ToString("F2") + " (" + Number(step, "offset", 0f).ToString("0.##", CultureInfo.InvariantCulture) + " m along the blade from its middle)"
                + (swing > 0f ? "; the right controller swept " + (swing * swept).ToString("0.00", CultureInfo.InvariantCulture) + " m in " + swept.ToString("0.00", CultureInfo.InvariantCulture) + " s before it" : "");
            if (swing > 0f)
            {
                // Glide back (an instant snap would be a speed spike of its own), then track the headset again.
                var from = hand.transform.position;
                var start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < ParryReturnSeconds)
                {
                    hand.transform.position = Vector3.Lerp(from, rest, (Time.realtimeSinceStartup - start) / ParryReturnSeconds);
                    yield return null;
                }
                hand.transform.position = rest;
                hand.SetTracking(true);
            }
        }

        /// <summary>player-parry swing=: how long the controller sweeps before the block event (s).</summary>
        private const float ParrySweepSeconds = 0.12f;

        /// <summary>player-parry swing=: how long the controller glides back afterwards (s).</summary>
        private const float ParryReturnSeconds = 0.8f;

        /// <summary>
        /// player-push &lt;metres&gt; &lt;seconds&gt; [back=true|false]: the player's right controller (the PlayerHand pose the
        /// hand follows) moves the given metres towards the spawned NPC and stays there for the seconds, then (back=true,
        /// default) returns to where it was and tracks the mock headset again. Reports how far the controller led the
        /// hand (PlayerHand.grip vs RagdollHand.grip) towards the NPC: min / mean / max, for a mod that measures pushing.
        /// </summary>
        private IEnumerator PlayerPush(Step step, Outcome outcome)
        {
            var player = Player.local;
            var hand = player != null ? player.handRight : null;
            var ragdollHand = player != null && player.creature != null ? player.creature.handRight : null;
            if (hand == null || ragdollHand == null || _npc == null || step.Args.Count < 2
                || !ScenarioParser.TryParseNumber(step.Args[0], out var metres) || !ScenarioParser.TryParseSeconds(step.Args[1], out var seconds))
            {
                outcome.Fail(_npc == null ? "no spawned NPC to push towards (spawn-npc first)" : "usage: player-push <metres> <seconds> [back=true|false]");
                yield break;
            }
            var back = !step.Named.TryGetValue("back", out var b) || b != "false";
            var toNpc = _npc.transform.position - player.creature.transform.position;
            toNpc.y = 0f;
            toNpc = toNpc.sqrMagnitude > 1e-4f ? toNpc.normalized : player.creature.transform.forward;
            var rest = hand.transform.position;
            hand.SetTracking(false);
            var start = Time.realtimeSinceStartup;
            float min = float.PositiveInfinity, max = float.NegativeInfinity, sum = 0f;
            var samples = 0;
            while (Time.realtimeSinceStartup - start < seconds)
            {
                hand.transform.position = rest + (toNpc * metres);
                yield return null;
                // The hand's side: the held handle (a physics body) when it holds one. RagdollHand.grip read here, in the
                // coroutine, sat up to 0.6 m off the held sword while DMCS read it 3 cm off in the same frames (probe
                // 2026-11-18), so the coroutine sees the hand bone in another pose than the physics one.
                var handSide = ragdollHand.grabbedHandle != null ? ragdollHand.grabbedHandle.transform : ragdollHand.grip;
                if (hand.grip != null && handSide != null)
                {
                    var lead = Vector3.Dot(hand.grip.position - handSide.position, toNpc);
                    min = Mathf.Min(min, lead);
                    max = Mathf.Max(max, lead);
                    sum += lead;
                    samples++;
                }
            }
            if (back)
            {
                hand.transform.position = rest;
                hand.SetTracking(true);
            }
            outcome.Detail = "right controller " + metres.ToString("0.00", CultureInfo.InvariantCulture) + " m towards the NPC for "
                + seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s" + (back ? ", then back" : ", left there")
                + (samples > 0 ? "; lead of the controller over the hand min " + min.ToString("0.000", CultureInfo.InvariantCulture)
                    + " / mean " + (sum / samples).ToString("0.000", CultureInfo.InvariantCulture) + " / max " + max.ToString("0.000", CultureInfo.InvariantCulture) + " m" : "; no grip to measure");
        }

        private static float Number(Step step, string key, float fallback)
        {
            return step.Named.TryGetValue(key, out var text) && ScenarioParser.TryParseNumber(text, out var value) ? value : fallback;
        }

        /// <summary>
        /// A picture of the spawned NPC from a temporary camera next to it, rendered into a
        /// texture (the game view follows the player's head and often misses an NPC lying at
        /// its feet). The camera stands `distance` m from the NPC's root part, `height` m above
        /// it, `angle` degrees clockwise around it from the player's side, and looks at it.
        /// </summary>
        private IEnumerator NpcScreenshot(Step step, Outcome outcome)
        {
            if (_npc == null || _npc.ragdoll == null || _npc.ragdoll.rootPart == null)
            {
                outcome.Fail("no spawned NPC to photograph (spawn-npc first)");
                yield break;
            }
            const int Width = 1280;
            const int Height = 720;
            var target = _npc.ragdoll.rootPart.transform.position;
            var side = Player.local != null && Player.local.creature != null
                ? Player.local.creature.transform.position - target
                : Vector3.forward;
            side.y = 0f;
            side = side.sqrMagnitude > 1e-4f ? side.normalized : Vector3.forward;
            side = Quaternion.Euler(0f, Number(step, "angle", 0f), 0f) * side;

            var cameraObject = new GameObject("BasHarnessNpcCamera");
            var renderTexture = new RenderTexture(Width, Height, 24);
            Texture2D picture = null;
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = target + side * Number(step, "distance", 2.5f) + Vector3.up * Number(step, "height", 1.2f);
                camera.transform.LookAt(target);
                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.05f;
                camera.targetTexture = renderTexture;
                // Let the render pipeline draw the camera once, then read it back.
                yield return null;
                yield return new WaitForEndOfFrame();
                var previous = RenderTexture.active;
                RenderTexture.active = renderTexture;
                picture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                picture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                picture.Apply();
                RenderTexture.active = previous;
                Directory.CreateDirectory(_outputDir);
                var path = Path.Combine(_outputDir, step.Args[0] + ".png");
                File.WriteAllBytes(path, picture.EncodeToPNG());
                outcome.Detail = path + " (camera " + camera.transform.position.ToString("F2") + " looking at NPC root " + target.ToString("F2") + ")";
            }
            finally
            {
                UnityEngine.Object.Destroy(cameraObject);
                renderTexture.Release();
                UnityEngine.Object.Destroy(renderTexture);
                if (picture != null)
                {
                    UnityEngine.Object.Destroy(picture);
                }
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
