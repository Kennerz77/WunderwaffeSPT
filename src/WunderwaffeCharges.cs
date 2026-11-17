using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using SPT.Reflection.Utils;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// A battery holds three bulbs; in a Pack-a-Punched Wunderwaffe each bulb is worth two shots (user, 2026-11-16).
    /// So a battery seated in a Pack-a-Punched gun has its charges doubled (3/3 becomes 6/6, 2/3 becomes 4/6), and
    /// once it's out of that gun again they're halved back into bulbs, a half-used bulb counting whole (6/6 shot twice
    /// comes out 2/3, shot once still 3/3). Which batteries are doubled is kept beside the plugin, so one left in the
    /// gun between sessions comes out right; any battery holding more than three outside a Pack-a-Punched gun is
    /// halved whatever the list says. Pack-a-Punch's own top-up (to 6) counts as doubled. In the hideout the server is
    /// told each change (for the profile's own batteries); a raid's changes come back with the raid.
    /// </summary>
    internal static class WunderwaffeCharges
    {
        private const int Bulbs = 3, PerBulb = 2;
        private const string Action = "WunderwaffeCharges";
        private static readonly string FilePath = Path.Combine(Path.GetDirectoryName(typeof(WunderwaffeCharges).Assembly.Location)!, "doubled_batteries.txt");
        private static HashSet<string> _doubled;
        private static float _nextScan;

        private static HashSet<string> Doubled
        {
            get
            {
                if (_doubled != null)
                    return _doubled;
                try
                {
                    _doubled = File.Exists(FilePath) ? new HashSet<string>(File.ReadAllLines(FilePath).Where(l => l.Length > 0)) : new HashSet<string>();
                }
                catch
                {
                    _doubled = new HashSet<string>();
                }
                return _doubled;
            }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, Doubled);
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Couldn't save the doubled batteries list: {e.Message}");
            }
        }

        /// <summary>A few times a second, for the local player: batteries into and out of Pack-a-Punched Wunderwaffes.</summary>
        public static void Tick(Player player)
        {
            if (player == null || Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + 0.2f;
            try
            {
                foreach (Magazine battery in Batteries(player))
                {
                    bool inUpgradedGun = battery.CurrentAddress?.Container?.ParentItem is Weapon gun && WunderwaffeIds.Is(gun) && WunderwaffePaP.IsInfused(gun);
                    bool doubled = Doubled.Contains(battery.Id);
                    if (inUpgradedGun && !doubled)
                    {
                        // Pack-a-Punch's top-up already filled it past a battery's three: that's a doubled one
                        if (battery.Count <= Bulbs)
                            SetCharges(player, battery, Mathf.Min(Bulbs * PerBulb, battery.Count * PerBulb));
                        Doubled.Add(battery.Id);
                        Save();
                        WunderwaffePlugin.Log.LogInfo($"Wunderwaffe battery {battery.Id} in a Pack-a-Punched gun: {battery.Count} charges");
                    }
                    else if (!inUpgradedGun && (doubled || battery.Count > Bulbs))
                    {
                        // Back to bulbs: a half-used bulb counts whole
                        SetCharges(player, battery, Mathf.Min(Bulbs, (battery.Count + PerBulb - 1) / PerBulb));
                        Doubled.Remove(battery.Id);
                        Save();
                        WunderwaffePlugin.Log.LogInfo($"Wunderwaffe battery {battery.Id} out of the Pack-a-Punched gun: {battery.Count}/3");
                    }
                }
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe battery check skipped: {e.Message}");
            }
        }

        /// <summary>Every Wunderwaffe battery the player has: worn, in the stash, and in the gun in their hands.</summary>
        private static IEnumerable<Magazine> Batteries(Player player)
        {
            var seen = new HashSet<string>();
            IEnumerable<Item> items = Enumerable.Empty<Item>();
            // The profile's own items, and (in the hideout) the copies the weapons mode puts in your hands
            foreach (Inventory inventory in new[] { player.Profile?.Inventory, player.Inventory })
            {
                if (inventory == null)
                    continue;
                if (inventory.Equipment != null)
                    items = items.Concat(inventory.Equipment.GetAllItems());
                if (inventory.Stash != null)
                    items = items.Concat(inventory.Stash.GetAllItems());
            }
            if (player.HandsController?.Item is Item held)
                items = items.Concat(held.GetAllItems());
            foreach (Item item in items)
                if (item is Magazine mag && WunderwaffeIds.IsClip(mag) && seen.Add(mag.Id))
                    yield return mag;
        }

        /// <summary>The battery's charges set to this many (its one stack grown or cut).</summary>
        private static void SetCharges(Player player, Magazine battery, int want)
        {
            Ammo stack = battery.Cartridges.Items.OfType<Ammo>().LastOrDefault();
            int have = battery.Count;
            if (stack == null || want == have)
                return;
            int count = stack.StackObjectsCount + (want - have);
            if (count <= 0)
                battery.Cartridges.Remove(stack, false);
            else
                stack.StackObjectsCount = count;
            if (player is HideoutPlayer && InProfile(player, battery))
                Tell(battery.Id, battery.Count);
        }

        private static bool InProfile(Player player, Item item)
        {
            Inventory inventory = player.Profile?.Inventory;
            if (inventory == null)
                return false;
            return (inventory.Equipment?.GetAllItems().Any(i => i.Id == item.Id) ?? false)
                   || (inventory.Stash?.GetAllItems().Any(i => i.Id == item.Id) ?? false);
        }

        private static void Tell(string id, int count)
        {
            try
            {
                ClientBackendSession session = ClientAppUtils.GetMainApp()?.GetClientBackEndSession() as ClientBackendSession;
                session?.SendOperationRightNow(new ChargesOperation { Action = Action, item = id, count = count }, result =>
                {
                    if (result == null || result.Failed)
                        WunderwaffePlugin.Log.LogWarning($"Wunderwaffe battery {id}: the server didn't take the new count ({result?.Error})");
                });
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe battery {id}: couldn't tell the server ({e.Message})");
            }
        }

        /// <summary>Item event body; field names match the server's WunderwaffeChargesRequest.</summary>
        private class ChargesOperation
        {
            public string Action;
            public string item;
            public int count;
        }
    }
}
