using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using EFT;
using EFT.InventoryLogic;
using PaPHideoutModule;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// Everything the Wunderwaffe asks of Pack-a-Punch. PaP is optional: callers go through <see cref="WunderwaffePaP"/>,
    /// which only touches this class (and so only loads PaPHideoutModule.dll) when PaP is installed.
    /// </summary>
    internal static class WunderwaffePaPBridge
    {
        public static bool IsInfused(Item item) => PaPApi.IsInfused(item) && !InMachine(item);

        public static bool IsOrigins(Item item) => IsInfused(item) && PaPApi.IsOriginsGun(item);

        /// <summary>What the machine added to the gun's worth: its fee, which depends on the mode picked in PaP.</summary>
        public static int InfusedFeeRub => PaPApi.BonusesActive ? 500_000 : 5_000;

        // Weapons infused when the set last changed (profile ids), so a change can tell which ones were just stripped
        private static HashSet<string> _infused = new HashSet<string>();

        // Guns the machine has just infused (profile id -> when): the server says yes before the gun goes in, but
        // like PaP's camo, the upgraded look waits until it comes back out (WeaponUpgraded). The timeout only
        // covers a sequence that never finishes (leaving the hideout mid-way); the machine takes about 4 s
        private static readonly Dictionary<string, float> Upgrading = new Dictionary<string, float>();
        private const float MachineTimeout = 15f;

        private static bool InMachine(Item item)
        {
            if (Upgrading.Count == 0)
                return false;
            string id = PaPApi.OriginalId(item.Id);
            if (!Upgrading.TryGetValue(id, out float since))
                return false;
            if (Time.time - since < MachineTimeout)
                return true;
            Upgrading.Remove(id);
            return false;
        }

        public static void Setup()
        {
            PaPApi.RegisterName(WunderwaffeIds.Weapon, WunderwaffeIds.UpgradedName);
            // Its own shot stays as it is (the mod plays the original's shot itself)
            PaPApi.NoShotTail(WunderwaffeIds.Weapon);
            // The bare metal (split off by the original's camo map) and the glowing cells keep their look in the Origins
            // machine too (its look paints cut-out materials unless told not to)
            PaPApi.KeepOriginsPlain("_metal");
            PaPApi.KeepOriginsPlain("_bulb");
            PaPApi.KeepOriginsPlain("_glow");
            PaPApi.KeepOriginsPlain("_glass");
            // The camo drawn smaller (user, 2026-11-15); where it goes is split at build time by the original's own
            // camo map (bare metal keeps its look). PaP 2.1.2+ only: an older PaP just keeps its size
            try
            {
                SmallerCamo();
            }
            catch (System.MissingMethodException)
            {
            }
            try
            {
                OwnCamo();
            }
            catch (System.MissingMethodException)
            {
                // An older PaP: the chosen default look instead
            }
            PaPApi.WeaponUpgraded += OnUpgraded;
            PaPApi.InfusedChanged += OnInfusedChanged;
            _infused = new HashSet<string>(PaPApi.InfusedIds);
        }

        public const float CamoTiling = 2.5f;

        /// <summary>
        /// The DG-3 JZ's own look (user, 2026-11-16, from the original's Shi No Numa footage): the original's engraved
        /// "etching" camo, bronze-gold with the 935 scrollwork, made from its colour, gold specular and depth maps
        /// (camo\ww_pap_etching_c / _s beside the plugin). In place of PaP's chosen default look; the Origins machine
        /// still gives the Origins look.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OwnCamo()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(WunderwaffePaPBridge).Assembly.Location), "camo");
            Texture2D albedo = LoadCamo(System.IO.Path.Combine(dir, "ww_pap_etching_c.png"), false);
            Texture2D spec = LoadCamo(System.IO.Path.Combine(dir, "ww_pap_etching_s.png"), true);
            // Metal: the gold engraving shines and the scrollwork has relief (user, 2026-11-16: "metallic in texture")
            Texture2D specGloss = LoadCamo(System.IO.Path.Combine(dir, "ww_pap_etching_sg.png"), false);
            Texture2D normal = LoadCamo(System.IO.Path.Combine(dir, "ww_pap_etching_n.png"), true);
            if (albedo == null)
            {
                WunderwaffePlugin.Log.LogWarning("Wunderwaffe: no camo/ww_pap_etching_c.png; PaP's default look on the DG-3 JZ");
                return;
            }
            // Only on the body plates the original lets camo cover (ww_body_body: the importer split the body by the
            // original's own camo mask); the wood, the brass cage and pipes and the battery keep their own look, as in
            // the original's footage (user, 2026-11-16)
            PaPApi.SetDefaultCamo(WunderwaffeIds.Weapon, albedo, spec, 1.2f, 0.04f, new[] { "ww_body_body" }, specGloss, normal);
        }

        private static Texture2D LoadCamo(string path, bool linear)
        {
            if (!System.IO.File.Exists(path))
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = System.IO.Path.GetFileNameWithoutExtension(path), wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            tex.LoadImage(System.IO.File.ReadAllBytes(path), true);
            return tex;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SmallerCamo()
        {
            try
            {
                OwnLooks();
            }
            catch (System.MissingMethodException)
            {
                // PaP before 2.2.1: one size for every look
                PaPApi.SetCamoTiling("ww_body", CamoTiling);
            }
        }

        /// <summary>
        /// The default camo small; the Origins crystal's dark splotches big and bold (bigger pattern, less glow over
        /// it: user, 2026-11-15). PaP 2.2.1+.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void OwnLooks()
        {
            PaPApi.SetCamoTiling("ww_body", CamoTiling, OriginsTiling);
            PaPApi.SetOriginsGlow("ww_body", OriginsGlow);
        }

        public const float OriginsTiling = 0.55f;
        public const float OriginsGlow = 0.4f;

        // Out of the machine: the red power cells and the camo show
        private static void OnUpgraded(Item item)
        {
            if (item != null && Upgrading.Remove(item.Id))
                RefreshAll();
        }

        /// <summary>
        /// The infused set changed (an upgrade, a strip, a profile sync): every live Wunderwaffe model redoes its power
        /// cells and camo at once, rather than when the gun is next drawn.
        /// </summary>
        private static void OnInfusedChanged()
        {
            var now = new HashSet<string>(PaPApi.InfusedIds);
            // One gun newly infused while in the hideout is the machine at work (a profile sync happens at the menu,
            // with no player, and can bring several at once)
            List<string> added = now.Where(id => !_infused.Contains(id)).ToList();
            if (added.Count == 1 && LocalPlayer.Get() is HideoutPlayer)
                Upgrading[added[0]] = Time.time;
            foreach (string id in _infused.Where(id => !now.Contains(id)))
                Upgrading.Remove(id);
            _infused = now;
            RefreshAll();
        }

        private static void RefreshAll()
        {
            foreach (WunderwaffeView view in WunderwaffeView.Live.ToList())
                view?.Refresh();
        }

        public static string OriginalId(string itemId) => PaPApi.OriginalId(itemId);

        public static void ApplyCamo(GameObject model, Item item) => PaPApi.ApplyCamo(model, item);

        public static void KeepOwnShot() => PaPApi.KeepOwnShotSound(WunderwaffeIds.Weapon);
    }

    /// <summary>Pack-a-Punch hooks that are safe to call with or without PaP installed.</summary>
    internal static class WunderwaffePaP
    {
        // PaP 2.0.0 or newer: older ones lack the API these features call (an older one is treated as not installed)
        public static readonly bool Installed =
            Chainloader.PluginInfos.TryGetValue("com.kennerz.paphideoutmodule", out BepInEx.PluginInfo pap) && pap.Metadata.Version >= new System.Version(2, 0, 0);
        private static bool _shotRegistered;

        public static void Setup()
        {
            if (Installed)
                WunderwaffePaPBridge.Setup();
        }

        public static bool IsInfused(Item item) => Installed && item != null && WunderwaffePaPBridge.IsInfused(item);

        /// <summary>Upgraded in PaP's Origins machine.</summary>
        public static bool IsOrigins(Item item) => Installed && item != null && WunderwaffePaPBridge.IsOrigins(item);

        public static int InfusedFeeRub => Installed ? WunderwaffePaPBridge.InfusedFeeRub : 0;

        /// <summary>The profile id behind a hideout weapons-mode copy (the id itself without PaP).</summary>
        public static string OriginalId(string itemId) => Installed ? WunderwaffePaPBridge.OriginalId(itemId) : itemId;

        /// <summary>PaP's camo on (or off) a model, by the gun's current state. Nothing without PaP.</summary>
        public static void ApplyCamo(GameObject model, Item item)
        {
            if (Installed && model != null && item != null)
                WunderwaffePaPBridge.ApplyCamo(model, item);
        }

        /// <summary>The original's Pack-a-Punched shot (in the weapon bundle) becomes the upgraded Wunderwaffe's sound.</summary>
        /// <summary>The gun plays its own Pack-a-Punched fire (the view swaps it in): Pack-a-Punch adds nothing on top.</summary>
        public static void KeepOwnShot()
        {
            if (!Installed || _shotRegistered)
                return;
            WunderwaffePaPBridge.KeepOwnShot();
            _shotRegistered = true;
        }
    }

    internal static class WunderwaffeIds
    {
        public const string Weapon = "6a7f4d6f1b11550000000001";
        public const string Clip = "6a7f4d6f1b11550000000002";
        public const string Charge = "6a7f4d6f1b11550000000003";
        public const string Name = "Wunderwaffe DG-2";
        public const string UpgradedName = "The Wunderwaffe DG-3 JZ";

        public static bool Is(Item item) => item != null && item.TemplateId.ToString() == Weapon;

        public static bool IsClip(Item item) => item != null && item.TemplateId.ToString() == Clip;

        public static bool IsCharge(Item item) => item != null && item.TemplateId.ToString() == Charge;
    }
}
