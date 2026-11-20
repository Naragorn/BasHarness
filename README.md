# BasHarness

Runs Blade & Sorcery **headless in the background** with only the mods under test, plays a
scenario inside the game and reports PASS/FAIL — nobody in the headset, nothing on the desktop,
the player's own install, mods, saves and options untouched. Used by DMCS and Heel of Achilles;
any ThunderScript mod can use it.

Linux + Proton (CachyOS). Modelled on the OBVR hand-script harness.

## Run

```sh
python3 runner/bas_harness.py --mod /path/to/ModFolder [--mod ...] scenario.txt [more.txt ...]
```

- `--mod` — a mod folder with `manifest.json` (a build output like `bin/Release/net472` works; it is
  installed under its DLL's name, which is the folder name the game saves mod options under).
- Several scenarios run **in order in the same prefix**: that is how persistence across a restart
  is tested (part 1 sets and saves, part 2 starts a new game and checks).
  **Option changes carry over to the next scenario even without `save-options`:** `Options.SetModOption`
  marks the options dirty (`Options.isDirty = true`), and when the game quits the player menu's
  `UIPlayerMenu.ManagedOnDisable` saves dirty options. So a scenario must not assume defaults unless it
  sets them itself (or it is the first one after `--fresh-prefix`). Found by the Heel of Achilles scenarios.
- Results: `artifacts/harness/<time>/<scenario>/` — `summary.md`, the run's `Player.log`,
  screenshots, `launch.txt`, `proton.log`. Exit code 0 = all PASS, 1 = FAIL, 2 = could not run.
- `--window` shows the game in a normal window (debugging), `--fresh-prefix` starts with a new
  character, `--level`/`--level-mode` pick the level (default `Arena`/`Sandbox`).

Needs: Steam running (the game's entitlement check), `gamescope`, the Steam Linux Runtime 4 and
`proton-cachyos-slr` (override with `--slr`/`--proton`), .NET SDK 8 (`~/.dotnet` is found).
The player's game must not be running.

## How it works (each point read in the game's code, ThunderRoad 1.3.2)

| What | How | Evidence |
|---|---|---|
| No headset | `-vrmode mockhmd` — Unity's MockHMD loader ships in the game | `PlayerControl.LoadXR`; log `XR Active Loader : Mock HMD Loader` |
| No main menu | `-level Arena -levelmode Sandbox -skipintro -skipmodswarning` | `GameManager.ProcessCommandLineArgs`; log `Startup level MainMenu overriden with Arena` |
| Only the mods under test | a **mirror** of the install in `~/.local/share/bas-harness/game`: symlinks, copied executables, own `Mods` and `Logs` | The game's own `-mod` filter does not work: `ModManager.IsEnabledFolder` returns *enabled* for folders **not** in the list (and so even loads `_`-disabled mods) — seen in a run with all 33 of the player's mods loaded |
| Player's saves/options untouched | own Proton prefix `~/.local/share/bas-harness/compatdata` | Options are saved per prefix; `Options.CleanModOptions` deletes saved options of mods that are not loaded, so a run in the player's prefix would wipe their mod settings |
| Nothing on the desktop | `gamescope --backend headless` | as in OBVR |
| New character blocks on "Calibration" | the harness mod sets `calibration.calibrated = true` | `PlayerSpawner.CalibratePlayer` waits for both triggers, polling that flag |

The harness mod (`src/BasHarness.Mod`) is inert unless the runner sets `BAS_HARNESS_SCENARIO`, and it is
only ever installed into the mirror.

## Scenario language

One step per line, `#` comments, `"quotes"` for spaces. The game runs the steps in order and stops at
the first FAIL; the game quits by itself at the end.

| Step | Does |
|---|---|
| `wait-level [seconds]` | wait for the player's creature in the level (default 180 s) |
| `wait <seconds>` | wait |
| `mark` | later `expect-log`/`expect-no-log` only look at log lines after this point |
| `expect-log <text>` | text appears in Player.log since the mark (within 10 s) |
| `await-log <seconds> <text>` | as `expect-log`, waiting up to that many seconds (for events a real NPC brings at its own pace) |
| `expect-no-log <text>` | text did not appear since the mark |
| `option-exists <mod> <name> [category= display= type= save= interaction= tooltip=]` | a ModOption with these properties; `display` is the title the mods page shows (localised); `tooltip` matches a part |
| `set-option <mod> <name> <value>` | apply a value (by value or menu title), the same call the mods page makes |
| `press <mod> <name>` | press a button option (next value, wrapping, like the mods page) |
| `expect-option <mod> <name> <value>` | the menu value **and** the member it drives hold `<value>` |
| `save-options` | save options the way closing the player menu does |
| `screenshot <name> [of=npc] [distance=2.5] [height=1.2] [angle=0]` | PNG of the game view into the run folder. With `of=npc`: a picture of the spawned NPC from a temporary camera `distance` m from its root part, `height` m above it, `angle` degrees clockwise around it from the player's side, looking at it (the game view follows the player's head and often misses an NPC lying at its feet) |
| `quit` | end here |
| `spawn-npc <creatureId> [distance=2] [angle=0] [facing=0] [brain=<id>] [container=<id>]` | spawn an NPC the way the book's spawner does (`container` replaces its loadout, e.g. `CivilianMeleeSword` = only a sword; the default `CivilianMelee` is random and may add a shield, mace or axe), `distance` m from the player, `angle` degrees clockwise from where the player faces, turned `facing` degrees clockwise away from looking at the player (180 = back to the player); waits until loaded; fails if a requested brain did not load. `brain=HumanDummy` stands still and still staggers (has `BrainModuleHitReaction`) |
| `run <0..1>` | keep the player moving forward through the game's own locomotion (`Locomotion.MoveWeighted`, the gamepad's call); the number is the run share, 0 = walk (2.8 m/s in the Arena), 1 = full sprint (6.0 m/s) |
| `stop-run` | stop moving |
| `player-grab <ItemId> [hand=right]` | spawn an item and put it into the player's hand (`RagdollHand.Grab`, teleported) |
| `player-parry [offset=0] [swing=0]` | raise the game's block event (`EventManager.InvokeCreatureAttackParried`, as `BrainModuleMelee` does when an NPC swing hits a held weapon) for the NPC's attacking weapon (the hand of its running attack, else the right) against the player's right-hand item; contact at the middle between the NPC's grip and blade tip, `offset` m along the blade; no physics. `swing` (m/s): first the right controller sweeps sideways, to the player's right, at that speed for 0.12 s, the event comes at the end of the sweep, then the controller glides back (for a mod that reads how fast the hand moved into the parry) |
| `player-push <metres> <seconds> [back=true]` | the player's right controller (the `PlayerHand` pose the hand follows, `SetTracking(false)`) moves `metres` towards the spawned NPC and stays for `seconds`, then goes back and tracks again (`back=false`: stays); reports how far the controller led the hand towards the NPC, min / mean / max: `PlayerHand.grip` vs the held handle (a physics body), or `RagdollHand.grip` when the hand is empty. Read from a coroutine, `RagdollHand.grip` sat up to 0.6 m off the held sword (2026-11-18), so the handle is used |
| `player-release [hand=right]` | the player's hand lets go of what it holds (`RagdollHand.TryRelease`) |
| `player-strike-weapon [twist=0] [seconds=0.2] [approach=0.6] [settle=0.7] [depth=0.15] [phase=all]` | the player's right controller brings the held blade's middle beside the spawned NPC's held blade (0.35 m out, away from its body; in `approach` s, then `settle` s for the hand to catch up), then strikes through it (to `depth` m past it, on the body's side) in `seconds`, rolling around the blade's long axis at `twist` °/s; then back over 0.5 s. `phase=ready` stops beside the blade and waits; a later `phase=strike` strikes from there, so a stagger can come in between. Real physics: the game's own collision events fire if the blades meet |
| `record-start <name> [fps=60] [width=640] [height=360] [views=side,first] [distance=2.5] [rise=1.2] [angle=0]` | films until `record-stop`, at a fixed frame rate in real time (a game frame that comes too late repeats the picture before it; the count is reported). Views: `side` (3 m beside the line between player and NPC, 1.2 m up, both in view; drawn with the URP renderer that also draws the player's head, which the game hides from the headset), `first` (from the player's eyes, looking at the right hand; the mock headset itself only looks straight ahead), `npc` (around the NPC's root part as `screenshot of=npc`: `distance`, `rise` m up, `angle` from the player's side), `hand` (close-up of the player's right hand and blade from above the side, for wrist gestures). The runner turns each film into `<name>-<view>.mp4`, `<name>.mp4` (views side by side), `<name>-slow.mp4` (4× slower) and both as GIF, then deletes the raw frames (640×360 is 0.9 MB per frame and view). GIFs: 12 fps, at most 640 px wide; the slow GIF only for films up to 6 s |
| `record-stop` | ends the film started by `record-start` |
| `frame-rate <fps>` | caps the game's frame rate for the rest of the run with the harness's own frame limiter (it waits at the end of each frame; `Application.targetFrameRate` did nothing, as with XR on the XR runtime paces the frames), then measures the frame rate over 1 s and fails when it is more than 15 % above the cap. Headless the game runs at 330–500 fps, and some game code is frame-rate dependent (measured by the Achilles session: an NPC turn of 172° → 22° takes 0.08 s uncapped and 0.17 s at 90 fps); `frame-rate 90` gives headset pace |
| `player-invincible <true\|false>` | the game's own `Player.invincibility` (console command `invincibility`): a long fight against a real NPC does not kill the player |
| `turn-player <degrees>` | turn the player on the spot (`Player.Teleport`), e.g. 180 to run back instead of into a wall |
| `expect-player-speed <min> [max]` | `Locomotion.horizontalSpeed` (m/s) is in range within 5 s |
| `hit-npc <RagdollPart.Type> [velocity=5] [damage=1] [push=-1] [type=Blunt] [by=hand] [item=DaggerCommon] [dead=false] [swing=3]` | `swing` (by=hand only): the right controller's pose (`PlayerHand`, tracking off via `SetTracking(false)`) moves forward at that m/s for 0.12 s before the hit and back after it, so a mod reading the real hand's speed sees a punch; `swing=0` = a hand that was only run into.  a hit on that part of the spawned NPC through `Creature.Damage` (raises `onCreatureHit` OnStart/OnEnd); no physics contact. `by`: `hand` = the player's right hand (punch), `held` = a spawned `item` grabbed by that hand (`RagdollHand.Grab`), `thrown` = a spawned `item` nobody holds with `Item.lastHandler` = the player's hand, `spell` = `CollisionInstance.casterHand` = the hand's caster, `foot` = the player's right foot ragdoll part with its own damager (a kick); `dead=true` also hits an NPC that is already killed (e.g. still in its dying animation); spawned items are despawned after the hit. `type` = `DamageType` (Unknown, Pierce, Slash, Blunt, Energy, Fire, Lightning, UnBlockable). `push` = `DamageStruct.pushLevel`: the default -1 makes the game's own hit reaction skip its push, so a stagger afterwards came from the mod under test |
| `npc-grab <ItemId> [hand=right]` | spawn an item and put it into the NPC's hand (`RagdollHand.Grab`, teleported to the hand). Passes if the grab happened, even when a mod releases it again by the next frame (the detail says so); check the result with `expect-npc holding` |
| `npc-attack <WindUp\|Attack\|FollowThrough\|End\|Cancel>` | raise the game's attack event (`EventManager.InvokeCreatureAttack`, melee) from the spawned NPC at the player, as `BrainModuleMelee` does at each stage. Only the event: the NPC does not swing, nothing hits |
| `player-grab-dropped [hand=right]` | the player's hand grabs the item the last `npc-grab` gave the NPC (`RagdollHand.Grab`, teleported to the hand); fails while the NPC still holds it |
| `expect-npc <stagger\|state\|staggered\|holding\|brain> <value> [within=2]` | the NPC's `Brain.currentStagger` (None/LightAndMedium/Full), `Creature.state` (Alive/Destabilized/Dead), staggered (True/False: stagger not None or destabilized), holding (True/False: a hand holds an item) or brain (loaded brain id) reaches the value within the time |

`<mod>` is the mod's folder name or manifest name.

## What it does not cover (yet)

- **Looking at the menu itself**: the checks read the same data the mods page renders from, but nobody
  opens the book; whether it *looks* right is still a headset check.
- **Controller input / physical combat**: no hands are moved. The combat steps drive the game directly
  (locomotion call, `Creature.Damage`); a real arm-swing sprint, a real weapon contact and how it feels stay
  headset checks.

## Development

```sh
dotnet test                                   # scenario parser, value matching, report format
python3 -m unittest discover -s runner        # runner logic
dotnet format --verify-no-changes
```
