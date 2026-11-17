using PaPHideoutServer;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace WunderwaffeServer;

/// <summary>
/// The Pack-a-Punched Wunderwaffe (The Wunderwaffe DG-3 JZ) holds 6 charges per battery (two a bulb). Registered with PaP's
/// server API (PaPServerApi), which runs it inside PaP's own upgrade and strip and sends the changed gun back to the
/// client: upgrading tops the loaded battery up to 6, however many it had left; stripping trims it back to a
/// plain gun's 3. Without PaP installed none of this loads.
/// </summary>
internal static class WunderwaffePaPServer
{
    private const string Chamber = "patron_in_weapon";

    internal static ISptLogger<WunderwaffePaPHook>? Logger;

    public static PaPWeaponHandler Handler => new()
    {
        Name = "Wunderwaffe",
        Upgraded = Recharge,
        Stripped = Trim,
    };

    /// <summary>Tops the loaded battery up to its upgraded charges where it sits: the chamber first, then the battery's stack.</summary>
    private static void Recharge(PaPWeaponContext ctx)
    {
        Item? clip = ctx.InSlot("mod_magazine");
        if (clip == null)
            return;
        Item? stack = ctx.InSlot("cartridges", clip);
        bool chambered = ctx.InSlot(Chamber) != null;
        int have = (int)(stack?.Upd?.StackObjectsCount ?? (stack == null ? 0 : 1)) + (chambered ? 1 : 0);
        int need = WunderwaffeIds.UpgradedClipShots - have;
        if (need <= 0)
            return;

        if (!chambered)
        {
            ctx.Attach(WunderwaffeIds.Charge, Chamber, upd: new Upd { StackObjectsCount = 1 });
            need--;
        }
        if (need > 0 && stack != null)
            ctx.SetStack(stack, (stack.Upd?.StackObjectsCount ?? 1) + need);
        else if (need > 0)
            ctx.Attach(WunderwaffeIds.Charge, "cartridges", clip, new Upd { StackObjectsCount = need }).Location = 0;
        Logger?.Info($"[Wunderwaffe] The Wunderwaffe DG-3 JZ {ctx.Weapon.Id}: battery {clip.Id} topped up to {WunderwaffeIds.UpgradedClipShots} shots");
    }

    /// <summary>The loaded battery keeps at most a plain gun's 3 charges (the chambered one counts first).</summary>
    private static void Trim(PaPWeaponContext ctx)
    {
        Item? clip = ctx.InSlot("mod_magazine");
        Item? stack = clip == null ? null : ctx.InSlot("cartridges", clip);
        if (stack == null)
            return;
        bool chambered = ctx.InSlot(Chamber) != null;
        int keep = Math.Max(0, WunderwaffeIds.ClipShots - (chambered ? 1 : 0));
        if ((stack.Upd?.StackObjectsCount ?? 1) <= keep)
            return;
        if (keep == 0)
            ctx.Remove(stack);
        else
            ctx.SetStack(stack, keep);
        Logger?.Info($"[Wunderwaffe] Wunderwaffe {ctx.Weapon.Id}: 115 powers stripped, clip back to {WunderwaffeIds.ClipShots} shots");
    }
}

/// <summary>Registers the Wunderwaffe with PaP when the PaP Hideout Module's server mod is loaded.</summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class WunderwaffePaPHook(ISptLogger<WunderwaffePaPHook> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        // Our assembly references PaPHideoutServer, but only this class touches its types (at JIT time), so check first
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "PaPHideoutServer"))
        {
            logger.Info("[Wunderwaffe] PaP Hideout Module not installed; no Pack-a-Punched Wunderwaffe");
            return Task.CompletedTask;
        }
        Enable(logger);
        return Task.CompletedTask;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Enable(ISptLogger<WunderwaffePaPHook> logger)
    {
        WunderwaffePaPServer.Logger = logger;
        try
        {
            PaPServerApi.Register(WunderwaffeIds.Weapon.ToString(), WunderwaffePaPServer.Handler);
            logger.Info($"[Wunderwaffe] Wunderwaffe registered with Pack-a-Punch: upgrades top the loaded battery up to {WunderwaffeIds.UpgradedClipShots} charges");
        }
        catch (Exception e)
        {
            // An older PaP without the server API
            logger.Error($"[Wunderwaffe] Wunderwaffe registration failed (PaP 1.1.0 or newer needed): {e.GetBaseException().Message}");
        }
    }
}
