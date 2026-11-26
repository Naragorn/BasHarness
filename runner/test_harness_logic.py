"""Unit tests for the runner's pure logic: python3 -m unittest discover -s runner"""

import unittest

import harness_logic as logic


class WindowsPathTests(unittest.TestCase):
    def test_posix_path_maps_to_wine_z_drive(self):
        self.assertEqual(logic.windows_path("/home/a b/x.txt"), "Z:\\home\\a b\\x.txt")

    def test_relative_path_is_refused(self):
        with self.assertRaises(ValueError):
            logic.windows_path("relative/x.txt")


class MirrorPlanTests(unittest.TestCase):
    def test_root_copies_the_executables_and_links_the_rest(self):
        plan = logic.mirror_plan(
            root=["BladeAndSorcery.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe", "MonoBleedingEdge",
                  "actions.json", "BladeAndSorcery_Data"],
            data=["Managed", "data.unity3d", "StreamingAssets"],
            streaming=["aa", "Default", "Mods", "Logs", "SteamVR"])
        self.assertEqual(plan["BladeAndSorcery.exe"], "copy")
        self.assertEqual(plan["UnityPlayer.dll"], "copy")
        self.assertEqual(plan["UnityCrashHandler64.exe"], "copy")
        self.assertEqual(plan["MonoBleedingEdge"], "link")
        self.assertEqual(plan["actions.json"], "link")
        self.assertEqual(plan["BladeAndSorcery_Data"], "dir")
        self.assertEqual(plan["BladeAndSorcery_Data/Managed"], "link")
        self.assertEqual(plan["BladeAndSorcery_Data/StreamingAssets"], "dir")
        self.assertEqual(plan["BladeAndSorcery_Data/StreamingAssets/Default"], "link")
        # The two folders the harness owns: never the player's mods or logs.
        self.assertEqual(plan["BladeAndSorcery_Data/StreamingAssets/Mods"], "dir")
        self.assertEqual(plan["BladeAndSorcery_Data/StreamingAssets/Logs"], "dir")

    def test_own_folders_exist_even_when_missing_in_the_game(self):
        plan = logic.mirror_plan(root=["BladeAndSorcery_Data"], data=["StreamingAssets"], streaming=[])
        self.assertEqual(plan["BladeAndSorcery_Data/StreamingAssets/Mods"], "dir")
        self.assertEqual(plan["BladeAndSorcery_Data/StreamingAssets/Logs"], "dir")


class CopyDecisionTests(unittest.TestCase):
    def test_missing_target_needs_copy(self):
        self.assertTrue(logic.needs_copy((10, 1.0), None))

    def test_same_size_and_mtime_is_kept(self):
        self.assertFalse(logic.needs_copy((10, 1.0), (10, 1.0)))

    def test_game_update_changes_size_or_mtime(self):
        self.assertTrue(logic.needs_copy((11, 1.0), (10, 1.0)))
        self.assertTrue(logic.needs_copy((10, 2.0), (10, 1.0)))


class LaunchTests(unittest.TestCase):
    def test_game_args_boot_straight_into_the_level_with_the_mock_headset(self):
        self.assertEqual(
            logic.game_args("Arena", "Sandbox"),
            ["-vrmode", "mockhmd", "-skipintro", "-skipmodswarning", "-level", "Arena", "-levelmode", "Sandbox"])

    def test_game_args_without_mode(self):
        self.assertNotIn("-levelmode", logic.game_args("Arena", None))

    def test_headless_wraps_in_gamescope(self):
        cmd = logic.launch_command("/slr", "/proton", "/g/BladeAndSorcery.exe", ["-a"], (1280, 720), headless=True)
        self.assertEqual(cmd[:3], ["gamescope", "--backend", "headless"])
        self.assertIn("-W", cmd)
        self.assertEqual(cmd[cmd.index("/slr"):], ["/slr", "--verb=waitforexitandrun", "--", "/proton", "waitforexitandrun",
                                                   "/g/BladeAndSorcery.exe", "-a"])

    def test_visible_run_has_no_gamescope(self):
        cmd = logic.launch_command("/slr", "/proton", "/g/x.exe", [], (1, 1), headless=False)
        self.assertEqual(cmd[0], "/slr")

    def test_env_points_proton_at_the_harness_prefix_and_passes_the_scenario(self):
        env = logic.launch_env({"PATH": "/bin", "PROTON_LOG": "1"}, "/steam", "/h/compatdata",
                               "Z:\\s.txt", "Z:\\out")
        self.assertEqual(env["STEAM_COMPAT_DATA_PATH"], "/h/compatdata")
        self.assertEqual(env["STEAM_COMPAT_CLIENT_INSTALL_PATH"], "/steam")
        self.assertEqual(env["SteamAppId"], "629730")
        self.assertEqual(env["BAS_HARNESS_SCENARIO"], "Z:\\s.txt")
        self.assertEqual(env["BAS_HARNESS_OUTPUT"], "Z:\\out")
        self.assertNotIn("PROTON_LOG", env)
        self.assertEqual(env["PATH"], "/bin")


LOG = """\
noise
2026 INFO X : [BasHarness] scenario Z:\\s.txt
2026 INFO X : [BasHarness] STEP 1/3 PASS line 2: wait-level -- player in level 'Arena'
2026 INFO X : [BasHarness] STEP 2/3 FAIL line 4: expect-log [DMCS] x -- not found since last mark within 10s
2026 INFO X : [BasHarness] RESULT FAIL 1/3
"""


class ReportParsingTests(unittest.TestCase):
    def test_steps_and_result_are_read_from_the_player_log(self):
        report = logic.parse_report(LOG)
        self.assertEqual(len(report.steps), 2)
        first, second = report.steps
        self.assertEqual((first.index, first.total, first.ok, first.line), (1, 3, True, 2))
        self.assertEqual(first.text, "wait-level")
        self.assertEqual(first.detail, "player in level 'Arena'")
        self.assertFalse(second.ok)
        self.assertEqual(second.detail, "not found since last mark within 10s")
        self.assertEqual(report.result, ("FAIL", 1, 3))

    def test_scenario_errors_and_inert_are_seen(self):
        report = logic.parse_report("[BasHarness] scenario error line 3: unknown verb 'x'\n[BasHarness] inert: X not set\n")
        self.assertEqual(report.errors, ["line 3: unknown verb 'x'"])
        self.assertTrue(report.inert)
        self.assertIsNone(report.result)

    def test_step_without_detail(self):
        report = logic.parse_report("[BasHarness] STEP 1/1 PASS line 1: quit\n[BasHarness] RESULT PASS 1/1\n")
        self.assertIsNone(report.steps[0].detail)
        self.assertEqual(report.result, ("PASS", 1, 1))


class VerdictTests(unittest.TestCase):
    def test_pass_needs_a_pass_result(self):
        report = logic.parse_report("[BasHarness] RESULT PASS 2/2\n")
        self.assertEqual(logic.verdict(report, timed_out=False), ("PASS", []))

    def test_fail_result_is_fail(self):
        verdict, reasons = logic.verdict(logic.parse_report(LOG), timed_out=False)
        self.assertEqual(verdict, "FAIL")
        self.assertIn("step 2 (line 4) failed: expect-log [DMCS] x -- not found since last mark within 10s", reasons)

    def test_timeout_without_result(self):
        verdict, reasons = logic.verdict(logic.parse_report("noise\n"), timed_out=True)
        self.assertEqual(verdict, "FAIL")
        self.assertIn("timed out before the harness reported a result", reasons)

    def test_game_ended_without_result(self):
        verdict, reasons = logic.verdict(logic.parse_report("noise\n"), timed_out=False)
        self.assertEqual(verdict, "FAIL")
        self.assertIn("the game ended without a harness result (crash, or the harness mod did not load)", reasons)

    def test_inert_harness_is_named(self):
        _, reasons = logic.verdict(logic.parse_report("[BasHarness] inert: X not set\n"), timed_out=False)
        self.assertIn("the harness mod ran inert: the scenario variable did not reach the game", reasons)

    def test_scenario_errors_are_listed(self):
        _, reasons = logic.verdict(logic.parse_report("[BasHarness] scenario error line 1: bad\n"
                                                      "[BasHarness] RESULT FAIL 0/0\n"), timed_out=False)
        self.assertIn("scenario error line 1: bad", reasons)

    def test_pass_result_but_timed_out_is_still_pass(self):
        # The result was reported; only the shutdown afterwards hung.
        report = logic.parse_report("[BasHarness] RESULT PASS 1/1\n")
        self.assertEqual(logic.verdict(report, timed_out=True)[0], "PASS")


class ModFolderTests(unittest.TestCase):
    def test_folder_needs_a_manifest(self):
        self.assertEqual(logic.mod_folder_problem("/m/X", ["X.dll"]), "/m/X: no manifest.json")

    def test_folder_with_manifest_is_fine(self):
        self.assertIsNone(logic.mod_folder_problem("/m/X", ["manifest.json", "X.dll"]))

    def test_missing_folder(self):
        self.assertEqual(logic.mod_folder_problem("/m/X", None), "/m/X: not a folder")

    def test_install_name_comes_from_folder_name_or_assembly(self):
        self.assertEqual(logic.install_name("/r/src/DMCS/bin/Release/net472", ["DMCS.dll", "manifest.json"]), "DMCS")
        self.assertEqual(logic.install_name("/r/dist/AchillesTendon", ["AchillesTendon.dll", "manifest.json"]),
                         "AchillesTendon")
        self.assertEqual(logic.install_name("/r/dist/Json", ["manifest.json"]), "Json")


class ProcessTests(unittest.TestCase):
    PROCS = [(1, "steam", "/steam/steam"), (2, "wine", "Z:\\games\\Blade & Sorcery\\BladeAndSorcery.exe -x"),
             (3, "zsh", "zsh -c grep BladeAndSorcery.exe")]

    def test_running_game_is_found_by_its_exe_but_not_by_a_shell_mentioning_it(self):
        self.assertEqual(logic.game_pids(self.PROCS), [2])

    def test_steam_running(self):
        self.assertTrue(logic.steam_running(self.PROCS))
        self.assertFalse(logic.steam_running([(1, "bash", "bash")]))


class SummaryTests(unittest.TestCase):
    def test_summary_has_verdict_steps_and_reasons(self):
        text = logic.summary_markdown("step1-menu", "FAIL", logic.parse_report(LOG), ["boom"], "/a/run", 42.0)
        self.assertIn("# step1-menu: FAIL", text)
        self.assertIn("| 1 | PASS | 2 | wait-level | player in level 'Arena' |", text)
        self.assertIn("- boom", text)
        self.assertIn("42 s", text)


if __name__ == "__main__":
    unittest.main()


class FilmCommandsTests(unittest.TestCase):
    FILM = {"name": "twist", "width": 640, "height": 360, "fps": 60, "views": ["side", "first"], "frames": 120, "repeated": 0}

    def test_two_views_give_one_video_each_then_side_by_side_slow_and_gifs(self):
        calls = logic.film_commands(self.FILM, "/run/x/")
        outs = [c[-1] for c in calls]
        self.assertEqual(outs, ["/run/x/twist-side.mp4", "/run/x/twist-first.mp4", "/run/x/twist.mp4",
                                "/run/x/twist-slow.mp4", "/run/x/twist.gif", "/run/x/twist-slow.gif"])
        first = calls[0]
        self.assertIn("rawvideo", first)
        self.assertEqual(first[first.index("-s") + 1], "640x360")
        self.assertEqual(first[first.index("-r") + 1], "60")
        self.assertEqual(first[first.index("-i") + 1], "/run/x/twist-side.rgba")
        self.assertIn("vflip", first)
        self.assertIn("hstack=inputs=2", calls[2])
        self.assertIn("setpts=4*PTS", calls[3])

    def test_one_view_has_no_side_by_side(self):
        film = dict(self.FILM, views=["first"])
        outs = [c[-1] for c in logic.film_commands(film, "/d")]
        self.assertEqual(outs, ["/d/twist-first.mp4", "/d/twist-slow.mp4", "/d/twist.gif", "/d/twist-slow.gif"])
        self.assertEqual(logic.film_commands(film, "/d")[1][logic.film_commands(film, "/d")[1].index("-i") + 1], "/d/twist-first.mp4")

    def test_a_long_film_gets_no_slow_gif(self):
        outs = [c[-1] for c in logic.film_commands(dict(self.FILM, frames=60 * 7), "/d")]
        self.assertIn("/d/twist.gif", outs)
        self.assertNotIn("/d/twist-slow.gif", outs)
        self.assertIn("/d/twist-slow.mp4", outs)

    def test_gifs_are_capped_at_640_wide_and_12_fps(self):
        gif = logic.film_commands(self.FILM, "/d")[4]
        vf = gif[gif.index("-vf") + 1]
        self.assertIn("fps=12", vf)
        self.assertIn("min(640,iw)", vf)

    def test_a_film_with_sound_gets_a_wav_and_the_sound_in_the_real_pace_videos(self):
        film = dict(self.FILM, audio={"rate": 48000, "channels": 2})
        calls = logic.film_commands(film, "/d")
        outs = [c[-1] for c in calls]
        self.assertEqual(outs, ["/d/twist.wav", "/d/twist-side.mp4", "/d/twist-first.mp4", "/d/twist.mp4",
                                "/d/twist-slow.mp4", "/d/twist.gif", "/d/twist-slow.gif"])
        wav = calls[0]
        self.assertEqual(wav[wav.index("-f") + 1], "f32le")
        self.assertEqual(wav[wav.index("-ar") + 1], "48000")
        self.assertEqual(wav[wav.index("-ac") + 1], "2")
        self.assertEqual(wav[wav.index("-i") + 1], "/d/twist.f32")
        self.assertIn("pcm_s16le", wav)
        for view in calls[1:3]:
            inputs = [view[i + 1] for i, a in enumerate(view) if a == "-i"]
            self.assertEqual(inputs[1], "/d/twist.wav")
            self.assertIn("aac", view)
        both = calls[3]
        self.assertIn("[0:v][1:v]hstack=inputs=2[v]", both)
        self.assertIn("0:a", both)
        # The slow film has no sound: four times slower sound would not match anything heard in the game.
        self.assertIn("-an", calls[4])

    def test_a_film_without_sound_has_no_sound_in_its_videos(self):
        for call in logic.film_commands(self.FILM, "/d"):
            self.assertNotIn("aac", call)
            self.assertNotIn("-an", call)

    def test_sound_without_rate_or_channels_counts_as_none(self):
        for audio in ({"rate": 0, "channels": 2}, {"rate": 48000, "channels": 0}, {}):
            outs = [c[-1] for c in logic.film_commands(dict(self.FILM, audio=audio), "/d")]
            self.assertNotIn("/d/twist.wav", outs)

    def test_an_empty_film_makes_nothing(self):
        self.assertEqual(logic.film_commands(dict(self.FILM, frames=0), "/d"), [])
        self.assertEqual(logic.film_commands(dict(self.FILM, views=[]), "/d"), [])
