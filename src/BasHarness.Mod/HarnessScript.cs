using System;
using ThunderRoad;
using UnityEngine;

namespace BasHarness
{
    /// <summary>
    /// Entry point. Does nothing unless the runner set BAS_HARNESS_SCENARIO (path of the scenario file).
    /// </summary>
    public sealed class HarnessScript : ThunderScript
    {
        public const string ScenarioEnv = "BAS_HARNESS_SCENARIO";
        public const string OutputEnv = "BAS_HARNESS_OUTPUT";

        private HarnessRunner _runner;

        public override void ScriptLoaded(ModManager.ModData modData)
        {
            base.ScriptLoaded(modData);
            var scenario = Environment.GetEnvironmentVariable(ScenarioEnv);
            if (string.IsNullOrEmpty(scenario))
            {
                Debug.Log("[BasHarness] inert: " + ScenarioEnv + " not set");
                return;
            }
            _runner = new HarnessRunner(scenario, Environment.GetEnvironmentVariable(OutputEnv));
            GameManager.local.StartCoroutine(_runner.Run());
        }

        public override void ScriptUpdate()
        {
            _runner?.KeepCalibrated();
        }
    }
}
