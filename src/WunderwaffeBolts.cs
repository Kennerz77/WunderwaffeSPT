using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// The Wunderwaffe's effects bundle (ww_fx.bundle, beside the plugin): the lightning bolt, the barrel arcs and the
    /// strike (white-blue, red from a Pack-a-Punched gun).
    /// </summary>
    internal static class WunderwaffeBolts
    {
        private const string BundleFile = "ww_fx.bundle";

        private static AssetBundle _bundle;
        private static bool _bundleTried;
        private static readonly Dictionary<string, GameObject> Prefabs = new Dictionary<string, GameObject>();

        private static string Dir => Path.GetDirectoryName(typeof(WunderwaffeBolts).Assembly.Location);

        internal static GameObject Prefab(string name)
        {
            if (Prefabs.TryGetValue(name, out GameObject cached) && cached != null)
                return cached;
            if (_bundle == null && !_bundleTried)
            {
                _bundleTried = true;
                string path = Path.Combine(Dir, BundleFile);
                _bundle = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
                if (_bundle == null)
                    WunderwaffePlugin.Log.LogWarning($"Wunderwaffe effects bundle missing or unreadable: {path}");
            }
            GameObject prefab = _bundle != null ? _bundle.LoadAsset<GameObject>(name) : null;
            Prefabs[name] = prefab;
            return prefab;
        }
    }

    /// <summary>
    /// Your Wunderwaffe has fired: the original's shot (its own last-charge shot as the battery empties) and the
    /// lightning (WunderwaffeLightning). The charge has no projectile, so the game itself fires nothing.
    /// </summary>
    internal static class WunderwaffeShot
    {
        public static void Enable(Harmony harmony)
        {
            EnableGap(harmony);
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot)),
                new HarmonyMethod(AccessTools.Method(typeof(WunderwaffeShot), nameof(ShotPrefix))),
                new HarmonyMethod(AccessTools.Method(typeof(WunderwaffeShot), nameof(ShotPostfix))));
        }

        /// <summary>A short pause after each shot before the next trigger pull fires (user, 2026-11-16).</summary>
        public const float ShotGap = 1.2f;
        private static float _lastShot = -10f;

        public static void EnableGap(Harmony harmony)
        {
            _harmony = harmony;
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.SetTriggerPressed)),
                new HarmonyMethod(AccessTools.Method(typeof(WunderwaffeShot), nameof(TriggerPrefix))));
        }

        private static Harmony _harmony;
        private static bool _overridesPatched;

        /// <summary>
        /// A co-op client's own hands controller overrides the trigger without calling the base one (the gap never held:
        /// user, 2026-11-16): every override patched too. Run once the first gun model is made, every mod loaded by then.
        /// </summary>
        public static void PatchOverrides()
        {
            if (_overridesPatched || _harmony == null)
                return;
            _overridesPatched = true;
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(WunderwaffeShot), nameof(TriggerPrefix)));
            Harmony harmony = _harmony;
            foreach (System.Reflection.Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }
                foreach (System.Type t in types)
                {
                    if (t == typeof(Player.FirearmController) || !typeof(Player.FirearmController).IsAssignableFrom(t))
                        continue;
                    System.Reflection.MethodInfo m = t.GetMethod(nameof(Player.FirearmController.SetTriggerPressed),
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                    if (m == null)
                        continue;
                    try
                    {
                        harmony.Patch(m, prefix);
                        WunderwaffePlugin.Log.LogInfo($"Shot gap also on {t.FullName}");
                    }
                    catch (System.Exception e)
                    {
                        WunderwaffePlugin.Log.LogWarning($"Shot gap not on {t.FullName}: {e.Message}");
                    }
                }
            }
        }

        private static readonly System.Reflection.FieldInfo HandsPlayer = AccessTools.Field(typeof(Player.FirearmController), "_player");

        private static bool TriggerPrefix(Player.FirearmController __instance, bool pressed)
        {
            // A bot being shocked by the lightning can't fire (WunderwaffeShock)
            if (pressed && HandsPlayer?.GetValue(__instance) is Player holder && WunderwaffeShock.IsHeld(holder))
                return false;
            if (!pressed || __instance.IsTriggerPressed || !WunderwaffeIds.Is(__instance.Item))
                return true;
            Player me = LocalPlayer.Get();
            if (me == null || !ReferenceEquals(me.HandsController, __instance))
                return true;
            return Time.time - _lastShot >= ShotGap;
        }

        // Charges in the gun as the shot starts (read before the game takes the fired one out)
        private static int _before;

        private static int Charges(Weapon gun) =>
            (gun.GetCurrentMagazine()?.Count ?? 0) + (gun.Chambers?.Count(c => c.ContainedItem != null) ?? 0);

        private static void ShotPrefix(Player.FirearmController __instance)
        {
            _before = __instance?.Item is Weapon gun && WunderwaffeIds.Is(gun) ? Charges(gun) : 99;
        }

        private static void ShotPostfix(Player.FirearmController __instance)
        {
            try
            {
                Player me = LocalPlayer.Get();
                if (me == null || !ReferenceEquals(me.HandsController, __instance) || !(__instance.Item is Weapon gun) || !WunderwaffeIds.Is(gun))
                    return;
                _lastShot = Time.time;
                bool upgraded = WunderwaffePaP.IsInfused(gun);
                Vector3 at = __instance.CurrentFireport?.position ?? me.Position;
                WunderwaffeShotSound.Play(at, upgraded, _before <= 1);
                WunderwaffeLightning.Fire(me, __instance, gun);
                __instance._weaponPrefab?.GetComponentInChildren<WunderwaffeView>(true)?.ShotCrawl();
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe shot skipped: {e.Message}");
            }
        }
    }
}
