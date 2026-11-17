using System;
using System.Linq;
using EFT.InventoryLogic;
using EFT.Trading;
using HarmonyLib;

namespace Wunderwaffe
{
    /// <summary>
    /// What traders pay for the Wunderwaffe DG-2's items (the server pays the price the client offers). The gun always
    /// sells for what the wall charged, plus the Pack-a-Punch fee when infused; a battery for what the ammo box
    /// charged, less for the shots spent, and nothing once empty.
    /// </summary>
    internal static class WunderwaffeSellPrice
    {
        public const int GunRub = 950_000;
        public const int CartridgeRub = 20_000;

        public static void Enable(Harmony harmony)
        {
            // Last, so PaP's own postfix (which adds its fee to the vanilla price) has already run and is replaced
            harmony.Patch(AccessTools.Method(typeof(Trader), nameof(Trader.GetUserItemPrice)),
                postfix: new HarmonyMethod(typeof(WunderwaffeSellPrice).GetMethod(nameof(PricePostfix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)) { priority = Priority.Last });
        }

        private static void PricePostfix(Trader __instance, Item item, ref Trader.ItemPrice? __result)
        {
            try
            {
                // No price means this trader doesn't buy it; that stays so
                if (__result == null || item == null)
                    return;
                long rub;
                if (WunderwaffeIds.Is(item))
                    rub = GunRub + (WunderwaffePaP.IsInfused(item) ? WunderwaffePaP.InfusedFeeRub : 0);
                else if (WunderwaffeIds.IsClip(item))
                    rub = CartridgeValue(item);
                else
                    return;
                Trader.ItemPrice price = __result.Value;
                double course = price.CurrencyId.HasValue && __instance.CurrencyCourses != null
                                && __instance.CurrencyCourses.TryGetValue(price.CurrencyId.Value.ToString(), out double c) && c > 0 ? c : 1.0;
                __result = new Trader.ItemPrice(price.CurrencyId, (int)Math.Min((long)Math.Floor(rub / course), int.MaxValue));
            }
            catch (Exception e)
            {
                WunderwaffePlugin.Log.LogError($"Sell price failed: {e.Message}");
            }
        }

        /// <summary>A battery: what the ammo box charged for a full one, by the shots it still holds.</summary>
        private static long CartridgeValue(Item cartridge)
        {
            int charges = (int)cartridge.GetAllItems().Where(i => i.TemplateId.ToString() == WunderwaffeIds.Charge).Sum(i => i.StackObjectsCount);
            if (charges <= 0)
                return 0;
            return (long)CartridgeRub * Math.Min(charges, WunderwaffeRounds.Clip) / WunderwaffeRounds.Clip;
        }
    }
}
