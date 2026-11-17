using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// A Wunderwaffe holds exactly a battery's worth: 3 charges, 6 Pack-a-Punched. EFT keeps a round in the chamber on
    /// top of the magazine, so a reload with a charge still chambered would come out one over. While the gun is held
    /// and not mid-reload, a chambered charge plus a full battery is trimmed back by one charge from the battery.
    /// </summary>
    internal static class WunderwaffeRounds
    {
        public const int Clip = 3, UpgradedClip = 6;

        public static void Check(Player.FirearmController fc)
        {
            try
            {
                if (!(fc?.Item is Weapon gun) || !WunderwaffeIds.Is(gun) || Reloading(fc))
                    return;
                Magazine mag = gun.GetCurrentMagazine();
                if (mag == null)
                    return;
                int chambered = gun.Chambers?.Count(c => c.ContainedItem != null) ?? 0;
                int most = WunderwaffePaP.IsInfused(gun) ? UpgradedClip : Clip;
                int extra = mag.Count + chambered - most;
                if (extra <= 0)
                    return;
                Ammo stack = mag.Cartridges.Items.OfType<Ammo>().LastOrDefault();
                if (stack == null)
                    return;
                if (stack.StackObjectsCount > extra)
                    stack.StackObjectsCount -= extra;
                else
                    mag.Cartridges.Remove(stack, false);
                WunderwaffePlugin.Log.LogInfo($"Wunderwaffe: {mag.Count + chambered + extra} charges after a reload, trimmed to {most}");
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe round count check skipped: {e.Message}");
            }
        }

        public static bool Reloading(Player.FirearmController fc)
        {
            string op = fc.CurrentOperation?.GetType().Name ?? "";
            return op.IndexOf("Reload", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
