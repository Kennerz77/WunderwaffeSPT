using System.Collections;
using System.Reflection;
using EFT;
using EFT.Communications;
using HarmonyLib;

namespace Wunderwaffe
{
    /// <summary>
    /// F12 dev: hands you the gun in a Kennerz Zombies match the way the mystery box does (the core's own give: the gun,
    /// a full mag of its default ammo, drawn), to test it there. Reached by name, so this mod needs no core.
    /// </summary>
    internal static class WunderwaffeZombiesGive
    {
        public static void Give()
        {
            try
            {
                PropertyInfo active = AccessTools.TypeByName("KennerZombies.ZombiesMatch")?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
                if (active == null || !(bool)active.GetValue(null))
                {
                    Notify("Wunderwaffe DG-2: the zombies give works in a zombies match (the hideout one is the other button).");
                    return;
                }
                Player me = LocalPlayer.Get();
                System.Type gunType = AccessTools.TypeByName("KennerZombies.BoxGuns+Gun");
                MethodInfo give = AccessTools.Method(AccessTools.TypeByName("KennerZombies.BoxGive"), "Give");
                if (me == null || gunType == null || give == null)
                {
                    Notify("Wunderwaffe DG-2: the zombies core's give wasn't found (core too old?).");
                    return;
                }
                object gun = System.Activator.CreateInstance(gunType);
                AccessTools.Field(gunType, "Id").SetValue(gun, "debug_" + WunderwaffeIds.Weapon);
                AccessTools.Field(gunType, "Name").SetValue(gun, WunderwaffeIds.Name);
                AccessTools.Field(gunType, "Role").SetValue(gun, "box_wonder");
                // Built as a box gun is, gun and its mag apart (user, 2026-11-16: made from the template's preset it came
                // with no mag, one round in the chamber and nothing to reload with): the core fills the mag, racks it in
                // and keeps its reserve. The core's builds are JSON (Newtonsoft), reached by name too
                string build = "{\"tpl\":\"" + WunderwaffeIds.Weapon + "\",\"parts\":[{\"slot\":\"mod_magazine\",\"tpl\":\"" + WunderwaffeIds.Clip + "\",\"parts\":[]}]}";
                MethodInfo parse = AccessTools.Method(AccessTools.TypeByName("Newtonsoft.Json.Linq.JObject"), "Parse", new[] { typeof(string) });
                AccessTools.Field(gunType, "Base").SetValue(gun, parse.Invoke(null, new object[] { build }));
                AccessTools.Field(gunType, "Pap").SetValue(gun, parse.Invoke(null, new object[] { build }));
                var routine = (IEnumerator)give.Invoke(null, new[] { gun, (object)me, false });
                WunderwaffePlugin.Runner.StartCoroutine(routine);
                WunderwaffePlugin.Log.LogInfo("Zombies debug give: Wunderwaffe DG-2");
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Zombies debug give failed: {e.GetBaseException().Message}");
                Notify("Wunderwaffe DG-2: the zombies give failed (see the log).");
            }
        }

        private static void Notify(string text)
        {
            try
            {
                NotificationManager.DisplayMessageNotification(text);
            }
            catch
            {
            }
        }
    }
}
