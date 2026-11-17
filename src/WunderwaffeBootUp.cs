using HarmonyLib;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// The original's first raise (its start-up, with its sound) on every draw in normal play (user, 2026-11-16); in a
    /// Kennerz Zombies match only when the gun is newly in your hands, out of the mystery box or Pack-a-Punch (the core
    /// calls <see cref="Next"/> just before drawing it), every other draw being the plain pullout. The controller picks
    /// the draw by its own bool (OUT TO IDLE ARM = first raise, OUT TO IDLE = pullout); it's set here as EFT starts the
    /// draw (SetActiveParam).
    /// </summary>
    public static class WunderwaffeBootUp
    {
        public const string Param = "WwBootUp";
        private static readonly int ParamHash = Animator.StringToHash(Param);
        private static float _until = -1f;

        /// <summary>The next Wunderwaffe draw (within 10 s) is the first raise.</summary>
        public static void Next() => _until = Time.time + 10f;

        // The zombies core's match flag (KennerZombies.ZombiesMatch.Active), read by name: this mod needs no core
        private static System.Reflection.PropertyInfo _zombiesActive;
        private static bool _zombiesLooked;

        private static bool InZombiesMatch()
        {
            if (!_zombiesLooked)
            {
                _zombiesLooked = true;
                _zombiesActive = AccessTools.TypeByName("KennerZombies.ZombiesMatch")?.GetProperty("Active",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            }
            try
            {
                return _zombiesActive != null && (bool)_zombiesActive.GetValue(null);
            }
            catch
            {
                return false;
            }
        }

        internal static void Enable(Harmony harmony)
        {
            var m = AccessTools.Method(typeof(ObjectInHandsAnimator), nameof(ObjectInHandsAnimator.SetActiveParam));
            if (m != null)
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(WunderwaffeBootUp), nameof(ActivePrefix)));
        }

        private static void ActivePrefix(ObjectInHandsAnimator __instance, bool active)
        {
            if (!active)
                return;
            try
            {
                var a = __instance?.Animator;
                if (a == null || !a.HasParameter(ParamHash))
                    return;
                // Normal play: the first raise on every draw (user, 2026-11-16). A zombies match: only when asked for
                // (out of the mystery box or Pack-a-Punch: the zombies core calls Next)
                bool boot = Time.time < _until || !InZombiesMatch();
                a.SetBool(ParamHash, boot);
                if (boot)
                    _until = -1f;
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe first raise choice failed: {e.Message}");
            }
        }
    }
}
