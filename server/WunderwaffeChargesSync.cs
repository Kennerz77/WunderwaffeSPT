using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.DI.Routing;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace WunderwaffeServer;

/// <summary>Body of the client's battery update: the battery and the charges it now holds.</summary>
public record WunderwaffeChargesRequest : BaseInteractionRequestData
{
    [JsonPropertyName("item")]
    public string? Item { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// A battery's charges as the client set them: doubled while it sits in a Pack-a-Punched Wunderwaffe (each bulb two
/// shots), halved back into bulbs once it's out. Out of a raid only (the hideout); a raid's own changes come back with
/// the raid's end. Only ever a Wunderwaffe battery, and never past its room.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class WunderwaffeChargesSync(ISptLogger<WunderwaffeChargesSync> logger)
{
    public const string Action = "WunderwaffeCharges";

    public ItemEventRouterResponse Set(PmcData pmcData, WunderwaffeChargesRequest body, ItemEventRouterResponse output)
    {
        List<Item>? items = pmcData.Inventory?.Items;
        Item? battery = items?.FirstOrDefault(i => i.Id.ToString() == body.Item);
        if (items == null || battery == null || battery.Template != WunderwaffeIds.Clip)
            return output;
        int count = Math.Clamp(body.Count, 0, WunderwaffeIds.UpgradedClipShots);
        string batteryId = battery.Id.ToString();
        List<Item> stacks = items.Where(i => i.ParentId == batteryId && i.SlotId == "cartridges").ToList();
        if (stacks.Count == 0)
            return output;
        Item keep = stacks[0];
        foreach (Item extra in stacks.Skip(1))
            items.Remove(extra);
        if (count == 0)
            items.Remove(keep);
        else
        {
            keep.Upd ??= new Upd();
            keep.Upd.StackObjectsCount = count;
        }
        logger.Info($"[Wunderwaffe] Battery {batteryId}: {count} charges");
        return output;
    }
}

/// <summary>Routes the client's WunderwaffeCharges item event.</summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public class WunderwaffeChargesRouter(WunderwaffeChargesSync sync) : ItemEventRouter([
    new ItemRouteAction<WunderwaffeChargesRequest>(WunderwaffeChargesSync.Action,
        (url, pmcData, body, sessionID, output, cancellationToken) => ValueTask.FromResult(sync.Set(pmcData, body, output))),
]) { }
