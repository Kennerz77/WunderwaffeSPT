using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace WunderwaffeServer;

/// <summary>
/// Each player's choice: the Wunderwaffe in their normal gameplay (the hideout's chalk wall and ammo box), or only in the
/// Kennerz Zombies mode. Kept per profile in normalgame.json next to the DLL; a player who hasn't picked yet plays as
/// before (on), and every client asks them (the Kennerz mods' shared first-launch question, or F12). A choice counts
/// from the next server start. Switched off, the Wunderwaffes and cartridges that player owns are removed at every server start (their side
/// file into removed/, overwritten each time; the profile file itself never holds them).
/// </summary>
internal static class WunderwaffeNormalGame
{
    private static string ModDir => Path.GetDirectoryName(typeof(WunderwaffeNormalGame).Assembly.Location)!;
    private static string FilePath => Path.Combine(ModDir, "normalgame.json");

    private static readonly object Lock = new();
    private static Dictionary<string, bool>? _saved, _atStart;

    private static Dictionary<string, bool> Saved
    {
        get
        {
            if (_saved != null)
                return _saved;
            try
            {
                _saved = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(FilePath)) ?? new() : new();
            }
            catch
            {
                _saved = new();
            }
            return _saved;
        }
    }

    private static Dictionary<string, bool> AtStart
    {
        get
        {
            lock (Lock)
                return _atStart ??= new Dictionary<string, bool>(Saved);
        }
    }

    /// <summary>On for this player since the server started (not picked yet: on, as before).</summary>
    public static bool Now(string? profileId) => profileId == null || !AtStart.TryGetValue(profileId, out bool on) || on;

    /// <summary>The player's saved pick (from the next start), or null if they haven't picked.</summary>
    public static bool? Next(string profileId)
    {
        lock (Lock)
            return Saved.TryGetValue(profileId, out bool on) ? on : null;
    }

    public static void Set(string profileId, bool on)
    {
        lock (Lock)
        {
            _ = AtStart;
            Saved[profileId] = on;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Saved, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Server start: every player who chose off loses the Wunderwaffes and cartridges kept for them (a copy goes to removed/).</summary>
    public static void RemoveOff(Action<string> log)
    {
        foreach (string id in AtStart.Where(p => !p.Value).Select(p => p.Key).ToList())
        {
            try
            {
                string side = Path.Combine(ModDir, "profiles", id + ".json");
                if (!File.Exists(side))
                    continue;
                Directory.CreateDirectory(Path.Combine(ModDir, "removed"));
                File.Move(side, Path.Combine(ModDir, "removed", id + ".json"), true);
                log($"[Wunderwaffe] Profile {id} chose the Wunderwaffe DG-2 for zombies only: its guns and batterys removed (copy in removed/)");
            }
            catch (Exception e)
            {
                log($"[Wunderwaffe] Removing the Wunderwaffe DG-2 items from profile {id} failed: {e.Message}");
            }
        }
    }
}

public record WunderwaffeNormalGameRequest : IRequestData
{
    /// <summary>null: just ask; true/false: this player's pick (from the next server start).</summary>
    [JsonPropertyName("set")] public bool? Set { get; set; }
}

/// <summary>POST /wunderwaffe/normalgame {set?}: the caller's pick. Replies on now, the saved pick, and whether they've picked.</summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public class WunderwaffeNormalGameRouter(JsonUtil jsonUtil, HttpResponseUtil httpResponseUtil) : StaticRouter(jsonUtil, [
    new RouteAction<WunderwaffeNormalGameRequest>(
        "/wunderwaffe/normalgame",
        (url, info, sessionId, output, cancellationToken) =>
        {
            string id = sessionId.ToString();
            if (info?.Set is bool on)
                WunderwaffeNormalGame.Set(id, on);
            bool? next = WunderwaffeNormalGame.Next(id);
            return ValueTask.FromResult(httpResponseUtil.NoBody(new Dictionary<string, object>
            {
                ["now"] = WunderwaffeNormalGame.Now(id),
                ["next"] = next ?? WunderwaffeNormalGame.Now(id),
                ["chosen"] = next != null,
            }));
        }
    ),
]) { }
