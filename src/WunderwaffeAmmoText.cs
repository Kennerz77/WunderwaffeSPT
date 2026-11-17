using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;

namespace Wunderwaffe
{
    /// <summary>
    /// The Wunderwaffe's count on its inventory cell is every charge it holds, the chambered one too (user, 2026-11-16:
    /// a fresh gun read 2/3, one charge being in the chamber): 3/3 full, 6/6 Pack-a-Punched. Only the shown number.
    /// </summary>
    internal static class WunderwaffeAmmoText
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(GridItemView), nameof(GridItemView.SetItemValue)),
                new HarmonyMethod(AccessTools.Method(typeof(WunderwaffeAmmoText), nameof(Prefix))));
        }

        private static void Prefix(GridItemView __instance, ref object arg1)
        {
            try
            {
                if (!(__instance.Item is Weapon gun) || !WunderwaffeIds.Is(gun) || !(arg1 is int count))
                    return;
                Magazine mag = gun.GetCurrentMagazine();
                if (mag == null || count != mag.Count)
                    return;
                int chambered = 0;
                foreach (Slot chamber in gun.Chambers)
                    if (chamber.ContainedItem != null)
                        chambered++;
                arg1 = count + chambered;
            }
            catch
            {
            }
        }
    }
}
