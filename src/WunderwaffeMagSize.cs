using EFT.InventoryLogic;
using HarmonyLib;

namespace Wunderwaffe
{
    /// <summary>
    /// The Wunderwaffe battery's size as it reads: 3/3 (a plain gun's), 6/6 only while it sits in a Pack-a-Punched gun (user,
    /// 2026-11-16). The item itself has room for the upgraded count (its template's), so a Pack-a-Punch top-up fits; a
    /// magazine taken out still holding more than a plain gun's reads at the upgraded size until it runs down.
    /// </summary>
    internal static class WunderwaffeMagSize
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(Magazine), nameof(Magazine.MaxCount)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(WunderwaffeMagSize), nameof(MaxCountPostfix))));
        }

        private static void MaxCountPostfix(Magazine __instance, ref int __result)
        {
            if (!WunderwaffeIds.IsClip(__instance))
                return;
            Item holder = __instance.CurrentAddress?.Container?.ParentItem;
            bool upgraded = holder is Weapon gun && WunderwaffeIds.Is(gun) ? WunderwaffePaP.IsInfused(gun) : __instance.Count > WunderwaffeRounds.Clip;
            __result = upgraded ? WunderwaffeRounds.UpgradedClip : WunderwaffeRounds.Clip;
        }
    }
}
