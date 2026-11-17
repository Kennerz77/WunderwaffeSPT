using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Modding.Custom;
using Path = System.IO.Path;

namespace WunderwaffeServer;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.kennerz.wunderwaffe"; // same GUID as the client plugin (Forge rule)
    public string Name { get; init; } = "Kenner-Wunderwaffe";
    public string Author { get; init; } = "Kenner";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Settings read from config.json next to the DLL. Never shipped, so an update can't overwrite it.</summary>
public record WunderwaffeConfig
{
    /// <summary>The hideout chalk wall's price for a Wunderwaffe DG-2, in roubles.</summary>
    public int WallBuyPrice { get; init; } = 950_000;

    /// <summary>The hideout ammo box's price for one full battery, in roubles.</summary>
    public int CartridgePrice { get; init; } = 20_000;

    internal static WunderwaffeConfig Load(Action<string> logError)
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(WunderwaffeConfig).Assembly.Location)!, "config.json");
        try
        {
            WunderwaffeConfig config = File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<WunderwaffeConfig>(File.ReadAllText(path)) ?? new() : new();
            // Written back so settings added in a newer version show up
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return config;
        }
        catch (Exception e)
        {
            logError($"[Wunderwaffe] Could not read {path}, using defaults: {e.Message}");
        }
        return new();
    }
}

/// <summary>Fixed template ids. They share a prefix so the profile store can spot them in any item list.</summary>
internal static class WunderwaffeIds
{
    public const string Prefix = "6a7f4d6f1b115500000000";

    public static readonly MongoId Weapon = new(Prefix + "01");
    public static readonly MongoId Clip = new(Prefix + "02");   // the battery clip (the magazine)
    public static readonly MongoId Charge = new(Prefix + "03"); // one shot

    // Vanilla templates the Wunderwaffe items are cloned from
    public static readonly MongoId Saiga = new("576165642459773c7a400233");
    public static readonly MongoId SaigaMagazine = new("57616a9e2459773c7a400234");
    public static readonly MongoId CopperSlug = new("5d6e68b3a4b9361bca7e50b5");
    public static readonly MongoId DefaultInventory = new("55d7217a4bdc2d86028b456d");

    public const string WeaponParent = "5447b6094bdc2dc3278b4567";   // Shotgun (the Saiga's own class)
    public const string MagazineParent = "5448bc234bdc2d3c308b4569"; // Magazine
    public const string AmmoParent = "5485a8684bdc2da71d8b4567";     // Ammo
    public const string HandbookShotguns = "5b5f794b86f77409407a7f92";
    public const string HandbookMagazines = "5b5f754a86f774094242f19b";
    public const string HandbookRounds = "5b47574386f77428ca22b33b";

    // Bundle keys: paths under the mod's bundles folder (bundles.json), built by build_bundle.ps1
    public const string BundleFolder = "Wunderwaffe";
    public const string WeaponBundle = "weapon_dg2_container.bundle";
    public const string ClipBundle = "mag_dg2_clip.bundle";

    /// <summary>The original's battery: 3 shots, 6 Pack-a-Punched.</summary>
    public const int ClipShots = 3;
    public const int UpgradedClipShots = 6;

    public static bool IsOurs(string? templateId) => templateId != null && templateId.StartsWith(Prefix);
}

/// <summary>
/// Adds the Wunderwaffe DG-2, its battery clip and the charge it holds. Items must exist before profiles load, hence
/// Preload. Until the Wunderwaffe bundle exists the clones keep the vanilla Saiga-12K's model.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class WunderwaffeItemLoader(
    ISptLogger<WunderwaffeItemLoader> logger,
    CustomItemService customItemService,
    TemplateTable templateTable,
    LocaleTable localeTable,
    ItemConfig itemConfig,
    ItemFilterService itemFilterService) : IOnLoad
{
    /// <summary>Out of random loot, bot gear, flea and daily rewards.</summary>
    private void KeepOutOfTheWorld(IEnumerable<MongoId> ids)
    {
        List<MongoId> all = ids.ToList();
        foreach (MongoId id in all)
        {
            itemConfig.Blacklist.Add(id);
            itemConfig.LootableItemBlacklist.Add(id);
            itemConfig.RewardItemBlacklist.Add(id);
        }
        itemFilterService.AddItemToBlacklistCache(all);
        itemFilterService.AddItemToLootableBlacklistCache(all);
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = templateTable.Items;

        TemplateItem? charge = Clone(WunderwaffeIds.CopperSlug, WunderwaffeIds.AmmoParent, WunderwaffeIds.Charge, "patron_wunderwaffe_charge",
            WunderwaffeIds.HandbookRounds, 500, "Wunderwaffe DG-2 charge", "DG-2",
            "One charge of a Wunderwaffe battery. Only exists inside a battery.");
        TemplateItem? clip = Clone(WunderwaffeIds.SaigaMagazine, WunderwaffeIds.MagazineParent, WunderwaffeIds.Clip, "mag_wunderwaffe_battery",
            WunderwaffeIds.HandbookMagazines, 20_000, "Wunderwaffe battery", "DG-2",
            "A Group 935 battery for the Wunderwaffe DG-2: 3 charges, 6 in a Pack-a-Punched gun.");
        TemplateItem? weapon = Clone(WunderwaffeIds.Saiga, WunderwaffeIds.WeaponParent, WunderwaffeIds.Weapon, "weapon_group935_wunderwaffe_dg2",
            WunderwaffeIds.HandbookShotguns, 500_000, "Wunderwaffe DG-2", "DG-2",
            "Group 935's lightning gun. Each shot arcs from one target to the next, killing everything it chains through.");
        if (charge?.Properties == null || clip?.Properties == null || weapon?.Properties == null)
            return Task.CompletedTask;

        clip.Properties.Width = 1;
        clip.Properties.Height = 1;
        clip.Properties.ExtraSizeLeft = 0;
        clip.Properties.ExtraSizeRight = 0;
        clip.Properties.ExtraSizeUp = 0;
        clip.Properties.ExtraSizeDown = 0;
        clip.Properties.ExtraSizeForceAdd = false;
        clip.Properties.Weight = 0.3;
        // Room for the Pack-a-Punched 42: a plain gun's clips are filled to 21 (the debug give and the zombies mode)
        HashSet<MongoId> chargeOnly = [WunderwaffeIds.Charge];
        SetCartridges(clip.Properties, WunderwaffeIds.UpgradedClipShots, chargeOnly);

        // No projectile at all (the game only fires one for ammo with a speed): the shot is the gun's animation and
        // sound, and the client mod's lightning, which picks its targets and kills them (WunderwaffeLightning)
        TemplateItemProperties bolt = charge.Properties;
        bolt.StackMaxSize = WunderwaffeIds.UpgradedClipShots;
        bolt.Caliber = Caliber;
        bolt.InitialSpeed = 0;
        bolt.ProjectileCount = 1;
        bolt.Tracer = false;
        bolt.DurabilityBurnModificator = 0;
        bolt.HeatFactor = 0;
        bolt.MisfireChance = 0;
        bolt.MalfMisfireChance = 0;
        bolt.MalfFeedChance = 0;
        bolt.Weight = 0;

        TemplateItemProperties gun = weapon.Properties;
        gun.AmmoCaliber = Caliber;
        gun.Weight = 4.5;
        gun.DurabilityBurnRatio = 0;
        // Nothing can go wrong with it: no jams, misfeeds, misfires or overheating
        gun.AllowJam = false;
        gun.AllowFeed = false;
        gun.AllowMisfire = false;
        gun.AllowSlide = false;
        gun.AllowOverheat = false;
        gun.BaseMalfunctionChance = 0;
        gun.Width = 4;
        gun.Height = 2;
        gun.Foldable = false;
        // Only the clip slot: the Saiga's required handguard and grip slots would leave the gun unusable bare
        Slot? magazineSlot = gun.Slots?.FirstOrDefault(s => s.Name == "mod_magazine");
        gun.Slots = magazineSlot == null ? [] : [WithFilter(magazineSlot, [WunderwaffeIds.Clip])];
        gun.Chambers = gun.Chambers?.Select(c => WithFilter(c, chargeOnly)).ToList();
        gun.DefMagType = WunderwaffeIds.Clip;
        gun.DefAmmo = WunderwaffeIds.Charge;

        // One shot at a time, a little over half a second apart (the original's pace)
        gun.WeapFireType = ["single"];
        // One shot every 1.2 s at most (user, 2026-11-16: it needs a delay per shot)
        gun.BFirerate = 50;
        gun.SingleFireRate = 50;
        // The kick is the fire animation's: no camera recoil on top
        gun.RecoilForceUp = 0;
        gun.RecoilForceBack = 0;
        gun.RecoilCamera = 0;
        if (gun.WeaponRecoilSettings != null)
            gun.WeaponRecoilSettings.Enable = false;

        foreach (TemplateItem item in new[] { charge, clip, weapon })
        {
            item.Properties!.CanSellOnRagfair = false;
            items[item.Id] = item;
        }
        AllowInWeaponSlots(items);
        UseOwnModels(weapon.Properties, clip.Properties);
        AddCaliberName();
        KeepOutOfTheWorld([WunderwaffeIds.Weapon, WunderwaffeIds.Clip, WunderwaffeIds.Charge]);
        logger.Success("[Wunderwaffe] Items added: Wunderwaffe DG-2, Wunderwaffe battery, Wunderwaffe charge");
        return Task.CompletedTask;
    }

    private const string Caliber = "CaliberWunderwaffe";

    /// <summary>The bolt's explosion name (the client mod draws it): matches WunderwaffeBolts.ExplosionName.</summary>
    private const string ExplosionName = "wunderwaffe_bolt";

    /// <summary>The calibre's name in the stats, every language: the game looks it up both with and without "Caliber".</summary>
    private void AddCaliberName()
    {
        foreach (var locale in localeTable.Global.Values)
        {
            locale.AddTransformer(dictionary =>
            {
                if (dictionary != null)
                {
                    dictionary[Caliber] = "Group 935 battery";
                    dictionary[Caliber.Replace("Caliber", "")] = "Group 935 battery";
                }
                return dictionary;
            });
        }
    }

    private TemplateItem? Clone(MongoId from, string parent, MongoId id, string name, string handbookParent, int price,
        string locName, string shortName, string description)
    {
        CreateItemResult result = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = from,
            ParentId = parent,
            NewId = id,
            NewItemName = name,
            AddToHandbook = true,
            HandbookParentId = handbookParent,
            HandbookPriceRoubles = price,
            AddToFleaPriceDb = false,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new() { Name = locName, ShortName = shortName, Description = description },
            },
        });
        if (result.Success != true || !templateTable.Items.TryGetValue(id, out TemplateItem? item))
        {
            logger.Error($"[Wunderwaffe] Could not add {name}: {string.Join("; ", result.Errors ?? [])}");
            return null;
        }
        return item;
    }

    /// <summary>
    /// Points the items at the Wunderwaffe's own bundles (bundles/Wunderwaffe/, served through bundles.json). Without them
    /// the clones keep the Saiga's models, so the items still work while the bundles are being built.
    /// </summary>
    private void UseOwnModels(TemplateItemProperties gun, TemplateItemProperties clip)
    {
        string dir = Path.Combine(Path.GetDirectoryName(typeof(WunderwaffeItemLoader).Assembly.Location)!, "bundles", WunderwaffeIds.BundleFolder);
        bool Has(string bundle) => File.Exists(Path.Combine(dir, bundle));
        if (!Has(WunderwaffeIds.WeaponBundle) || !Has(WunderwaffeIds.ClipBundle))
        {
            logger.Warning($"[Wunderwaffe] Bundles missing in {dir}; using the Saiga-12K's models");
            return;
        }
        gun.Prefab = new Prefab { Path = $"{WunderwaffeIds.BundleFolder}/{WunderwaffeIds.WeaponBundle}", Rcid = "" };
        clip.Prefab = new Prefab { Path = $"{WunderwaffeIds.BundleFolder}/{WunderwaffeIds.ClipBundle}", Rcid = "" };
    }

    /// <summary>
    /// The player's weapon slots list their guns one template at a time, so a clone is "incompatible" until it's
    /// added by id. Both primaries and the holster, like the Ray Gun.
    /// </summary>
    private void AllowInWeaponSlots(Dictionary<MongoId, TemplateItem> items)
    {
        if (!items.TryGetValue(WunderwaffeIds.DefaultInventory, out TemplateItem? inventory) || inventory.Properties?.Slots == null)
        {
            logger.Error("[Wunderwaffe] Default inventory template not found; the Wunderwaffe can't be equipped");
            return;
        }
        foreach (Slot slot in inventory.Properties.Slots.Where(s => s.Name is "FirstPrimaryWeapon" or "SecondPrimaryWeapon"))
        {
            foreach (SlotFilter filter in slot.Properties?.Filters ?? [])
                filter.Filter?.Add(WunderwaffeIds.Weapon);
        }
    }

    /// <summary>The clone shares nothing with the vanilla magazine: a fresh cartridges slot that takes only charges.</summary>
    private static void SetCartridges(TemplateItemProperties props, int count, HashSet<MongoId> filter)
    {
        Slot? cartridges = props.Cartridges?.FirstOrDefault();
        if (cartridges == null)
            return;
        // A battery runs down as it's used and can't be refilled in normal play: charges are never sold, dropped or
        // looted (blacklisted), they only exist inside batteries. (A filter that let no charge in made the game drop
        // the charges already inside: every battery read 0, user 2026-11-16)
        props.Cartridges = [WithFilter(cartridges, filter) with { MaxCount = count }];
    }

    // New records all the way down, so the vanilla template's filter lists are never touched
    private static Slot WithFilter(Slot slot, HashSet<MongoId> filter) => slot with
    {
        Properties = new SlotProperties { Filters = [new SlotFilter { Filter = [.. filter], Shift = 0 }] },
    };
}
