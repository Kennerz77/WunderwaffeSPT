using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// Client side of the Wunderwaffe DG-2. Pack-a-Punch support is optional (soft dependency): nothing here touches
    /// PaP unless it's installed.
    /// </summary>
    [BepInPlugin(PluginGuid, "Kenner-Wunderwaffe", "1.0.0")] // Forge rule: "Username-ModName"
    [BepInDependency("com.kennerz.paphideoutmodule", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(WunderwaffeDraw.HideoutUncensoredGuid, "1.3.0")] // hard: the wall buy hands you the gun through it
    public class WunderwaffePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.kennerz.wunderwaffe"; // same GUID as the server mod (Forge rule)

        internal static ManualLogSource Log;

        /// <summary>Runs the mod's coroutines (the lightning).</summary>
        internal static MonoBehaviour Runner;

        private void Awake()
        {
            // Release builds: F12 shows players only their own settings
            ReleaseMenu.Install(this, d => d.Section == "1. Normal gameplay" || d.Section == WunderwaffeSettings.VolumeSection); // volumes + normal play only (user, 2026-11-17)
            Log = Logger;
            Runner = this;
            // Teammates' lightning in a zombies co-op match: listened for once every plugin is in (the core loads after)
            StartCoroutine(ListenLater());
            WunderwaffeSettings.Bind(Config);
            WunderwaffeNormalGame.Enable(Config, gameObject);
            WunderwaffeSounds.Enable();
            var harmony = new HarmonyLib.Harmony(PluginGuid);
            Try("Weapon root reset", () =>
            {
                WunderwaffeRootReset.Enable(harmony);
                gameObject.AddComponent<WunderwaffeFitKeeper>();
            });
            Try("Magazine size", () => WunderwaffeMagSize.Enable(harmony));
            Try("Sell price", () => WunderwaffeSellPrice.Enable(harmony));
            Try("Hideout stations", () => WunderwaffeStations.Enable(harmony, gameObject));
            Try("Ammo count", () => WunderwaffeAmmoText.Enable(harmony));
            Try("Shocked bots", () => WunderwaffeShock.Enable(harmony));
            Try("Model and icon", () =>
            {
                WunderwaffeModels.Enable(harmony);
                WunderwaffeIcons.Enable(harmony);
            });
            Try("Item window centring", () => WunderwaffePreview.Enable(harmony));
            Try("Draw and menu pose", () =>
            {
                WunderwaffeBootUp.Enable(harmony);
                WunderwaffeMenuPose.Enable(harmony);
            });
            Try("Lightning shot", () => WunderwaffeShot.Enable(harmony));
            WunderwaffePaP.Setup();
            gameObject.AddComponent<WunderwaffeTracker>();
            Log.LogInfo("Wunderwaffe DG-2 loaded");
        }

        private static void Try(string what, System.Action enable)
        {
            try
            {
                enable();
            }
            catch (System.Exception e)
            {
                Log.LogError($"{what} patches failed: {e}");
            }
        }

        private System.Collections.IEnumerator ListenLater()
        {
            yield return new UnityEngine.WaitForSeconds(5f);
            WunderwaffeLightning.ListenToTeam();
        }
    }
}
