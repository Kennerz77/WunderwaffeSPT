using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// The Wunderwaffe's shot: a bolt of lightning. The charge has no projectile (no speed), so the game fires
    /// nothing; this picks the targets. The first is the one under your aim (or the nearest within a narrow cone, up
    /// to 60 m, in clear sight); the bolt then jumps to the nearest enemy within 9 m of the last, in clear sight, up to
    /// 10 kills (24 Pack-a-Punched), as in the original. Every one is killed through the game's own damage, credited to
    /// you and the gun like any kill. Each jump is drawn as a jagged bolt that crackles for a moment, with a zap and
    /// the original's impact sound where it lands. Your own group is never touched (nor, in a zombies match, any player).
    /// </summary>
    internal static class WunderwaffeLightning
    {
        // The first strike's reach (60 m was far too long: user, 2026-11-16); the chain's jumps keep the original's 9 m
        private const float Cone = 6f;
        // Shorter in a zombies match (user, 2026-11-16: still powerful with its few shots, just not across the map)
        private const float NormalRange = 25f, NormalChainRange = 9f, ZombiesRange = 15f, ZombiesChainRange = 6.5f;
        private static float Range => InZombiesMatch() ? ZombiesRange : NormalRange;
        private static float ChainRange => InZombiesMatch() ? ZombiesChainRange : NormalChainRange;
        private const int Chain = 10, UpgradedChain = 24;
        // Between one jump and the next (the original's lightning runs from zombie to zombie, not all at once)
        // Long enough between jumps to see the lightning go from one to the next (user, 2026-11-16)
        private const float JumpDelay = 0.18f;
        private const float BoltLife = 0.35f, Rejag = 0.03f;

        private static int _bulletLayers;
        private static readonly RaycastHit[] Hits = new RaycastHit[32];

        /// <summary>Your Wunderwaffe has just fired.</summary>
        public static void Fire(Player shooter, Player.FirearmController hands, Weapon gun)
        {
            try
            {
                bool upgraded = WunderwaffePaP.IsInfused(gun);
                Camera cam = Camera.main;
                if (shooter == null || cam == null)
                    return;
                Vector3 eye = cam.transform.position, look = cam.transform.forward;
                Vector3 muzzle = hands?.CurrentFireport?.position ?? eye + look * 0.6f;
                List<Player> enemies = Enemies(shooter).ToList();

                Player first = UnderAim(eye, look, enemies) ?? InCone(eye, look, enemies);
                var path = new List<(Vector3 point, Player target)>();
                if (first != null)
                {
                    var hit = new HashSet<Player> { first };
                    path.Add((Chest(first), first));
                    int most = upgraded ? UpgradedChain : Chain;
                    Player last = first;
                    while (path.Count < most)
                    {
                        Vector3 from = Chest(last);
                        Player next = enemies.Where(e => !hit.Contains(e))
                            .Select(e => (e, d: Vector3.Distance(from, Chest(e))))
                            .Where(x => x.d <= ChainRange && !Blocked(from, Chest(x.e)))
                            .OrderBy(x => x.d).Select(x => x.e).FirstOrDefault();
                        if (next == null)
                            break;
                        hit.Add(next);
                        path.Add((Chest(next), next));
                        last = next;
                    }
                }
                else
                {
                    // Nothing to strike: the bolt hits whatever is in the way (or fades out at range)
                    Vector3 end = Physics.Raycast(eye, look, out RaycastHit wall, Range, BulletLayers(), QueryTriggerInteraction.Ignore)
                        ? wall.point : eye + look * Range;
                    path.Add((end, null));
                }
                WunderwaffePlugin.Runner.StartCoroutine(Strike(shooter, gun, muzzle, look, path, upgraded));
                SendToTeam(upgraded, muzzle, look, path);
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe lightning skipped: {e.Message}");
            }
        }

        // ---------- Co-op (a Kennerz Zombies match over Fika) ----------
        // The lightning was only ever drawn on the shooter's game, and a client's hit just killed the zombie through the
        // host (no tesla death) (user, 2026-11-17). Now the shooter sends the chain on; every other game draws it, and the
        // game that runs a zombie (the host) shocks and kills it, credited to the shooter. Through the zombies core's own
        // team messages (KennerZombies.ZombiesApi, read by name: this mod needs no core).
        private const string Channel = "wunderwaffe_bolt";
        private static PropertyInfo _teamOnline;
        private static MethodInfo _teamSend;
        private static bool _teamLooked;

        private static bool TeamOnline()
        {
            if (!_teamLooked)
            {
                _teamLooked = true;
                System.Type api = HarmonyLib.AccessTools.TypeByName("KennerZombies.ZombiesApi");
                _teamOnline = api?.GetProperty("TeamOnline", BindingFlags.Public | BindingFlags.Static);
                _teamSend = api?.GetMethod("SendToTeam", BindingFlags.Public | BindingFlags.Static);
            }
            try
            {
                return _teamOnline != null && _teamSend != null && (bool)_teamOnline.GetValue(null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Listens for teammates' bolts (once the zombies core is loaded; nothing happens without it).</summary>
        public static void ListenToTeam()
        {
            try
            {
                MethodInfo on = HarmonyLib.AccessTools.TypeByName("KennerZombies.ZombiesApi")?.GetMethod("OnTeamMessage", BindingFlags.Public | BindingFlags.Static);
                if (on == null)
                    return;
                on.Invoke(null, new object[] { Channel, new System.Action<string, byte[]>(FromTeam) });
                WunderwaffePlugin.Log.LogInfo("Wunderwaffe: listening for teammates' lightning (zombies co-op)");
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe: no co-op lightning ({e.GetBaseException().Message})");
            }
        }

        private static void SendToTeam(bool upgraded, Vector3 muzzle, Vector3 look, List<(Vector3 point, Player target)> path)
        {
            if (!TeamOnline())
                return;
            try
            {
                using (var ms = new System.IO.MemoryStream())
                using (var w = new System.IO.BinaryWriter(ms))
                {
                    w.Write(upgraded);
                    Write(w, muzzle);
                    Write(w, look);
                    w.Write(path.Count);
                    foreach (var (point, target) in path)
                    {
                        Write(w, point);
                        w.Write(target?.ProfileId ?? "");
                    }
                    w.Flush();
                    _teamSend.Invoke(null, new object[] { Channel, ms.ToArray() });
                }
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe: the lightning wasn't sent to the team ({e.GetBaseException().Message})");
            }
        }

        private static void Write(System.IO.BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static Vector3 ReadVector(System.IO.BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        /// <summary>A teammate's Wunderwaffe fired: its chain drawn here, and the zombies this game runs shocked and killed.</summary>
        private static void FromTeam(string from, byte[] data)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            Player shooter = world?.AllPlayersEverExisted.FirstOrDefault(p => p != null && p.ProfileId == from);
            if (shooter == null || shooter.IsYourPlayer)
                return;
            using (var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data)))
            {
                bool upgraded = r.ReadBoolean();
                Vector3 muzzle = ReadVector(r), look = ReadVector(r);
                int n = r.ReadInt32();
                var path = new List<(Vector3 point, Player target)>();
                for (int i = 0; i < n; i++)
                {
                    Vector3 point = ReadVector(r);
                    string id = r.ReadString();
                    Player target = id.Length == 0 ? null : world.AllPlayersEverExisted.FirstOrDefault(p => p != null && p.ProfileId == id);
                    path.Add((point, target));
                }
                // The gun to credit the kills to: the one in their hands here, else any Wunderwaffe they carry
                Weapon gun = shooter.HandsController?.Item as Weapon;
                if (gun == null || !WunderwaffeIds.Is(gun))
                    gun = shooter.InventoryController?.Inventory?.Equipment?.GetAllItems().OfType<Weapon>().FirstOrDefault(WunderwaffeIds.Is) ?? gun;
                WunderwaffePlugin.Runner.StartCoroutine(Strike(shooter, gun, muzzle, look, path, upgraded));
            }
        }

        /// <summary>Another game runs this body (a co-op copy): only drawn here, that game shocks and kills it.</summary>
        private static bool IsCopy(Player p) => p != null && p.GetType().Name.Contains("Observed");

        private static IEnumerator Strike(Player shooter, Weapon gun, Vector3 muzzle, Vector3 look, List<(Vector3 point, Player target)> path, bool upgraded)
        {
            WunderwaffePlugin.Runner.StartCoroutine(Glide(muzzle, path.Select(p => p.point).ToList(), upgraded));
            Vector3 from = muzzle;
            foreach (var (point, target) in path)
            {
                Bolt(from, point, upgraded);
                Zap(point, upgraded);
                if (target != null && target.HealthController?.IsAlive == true && IsCopy(target) && TeamOnline())
                {
                    // Run by another game (the host): it shocks and kills it from the same chain; here only the fry
                    ShockedUntil[target] = Time.time + 4f;
                    WunderwaffePlugin.Runner.StartCoroutine(Fry(target, upgraded, 3.3f + FryTime));
                }
                else if (target != null && target.HealthController?.IsAlive == true)
                {
                    try
                    {
                        // A zombie in a Kennerz Zombies match stays on its feet, jerking, a moment before it drops (user,
                        // 2026-11-16, as in the original); anywhere else (or on a co-op copy, run by another game) it
                        // drops at once
                        Vector3 dir = (point - from).normalized;
                        // A zombie: the core's tesla death. A bot in normal play: a pain yell and a moment stood
                        // jerking (user, 2026-11-16). Anyone else (a player, a co-op copy): down at once
                        float down = 0f;
                        if (target.IsAI)
                        {
                            if (InZombiesMatch())
                                down = Shock(target, ShockTime);
                            else
                            {
                                down = WunderwaffeShock.Seconds;
                                WunderwaffeShock.Begin(target, down);
                            }
                        }
                        if (down > 0f)
                            ShockedUntil[target] = Time.time + down + 0.5f;
                        if (down > 0f)
                            WunderwaffePlugin.Runner.StartCoroutine(KillAfter(shooter, target, gun, from, dir, down));
                        else
                            Kill(shooter, target, gun, from, dir, point);
                        WunderwaffePlugin.Runner.StartCoroutine(Fry(target, upgraded, down + FryTime));
                    }
                    catch (System.Exception e)
                    {
                        WunderwaffePlugin.Log.LogWarning($"Wunderwaffe kill skipped: {e.Message}");
                    }
                }
                from = point;
                yield return new WaitForSeconds(JumpDelay);
            }
            if (path.Count > 1)
                WunderwaffePlugin.Log.LogInfo($"Wunderwaffe: lightning chained through {path.Count(p => p.target != null)} target(s)");
        }

        // ---------- Targets ----------

        private static IEnumerable<Player> Enemies(Player shooter)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world?.AllAlivePlayersList == null)
                yield break;
            foreach (Player p in world.AllAlivePlayersList)
                if (p != null && p != shooter && p.HealthController?.IsAlive == true && !Spared(shooter, p) && !Shocked(p))
                    yield return p;
        }

        /// <summary>The enemy whose body the aim line hits first, with nothing solid before it.</summary>
        private static Player UnderAim(Vector3 eye, Vector3 look, List<Player> enemies)
        {
            int count = Physics.RaycastNonAlloc(eye, look, Hits, Range, ~0, QueryTriggerInteraction.Ignore);
            foreach (RaycastHit h in Hits.Take(count).OrderBy(h => h.distance))
            {
                if (h.distance < 1.2f || h.collider == null)
                    continue;
                Player p = h.collider.GetComponentInParent<Player>();
                if (p != null && enemies.Contains(p))
                    return p;
                if (p == null && (BulletLayers() & (1 << h.collider.gameObject.layer)) != 0)
                    return null; // a wall first
            }
            return null;
        }

        /// <summary>The nearest enemy inside a narrow cone round the aim, in clear sight.</summary>
        private static Player InCone(Vector3 eye, Vector3 look, List<Player> enemies) => enemies
            .Select(e => (e, to: Chest(e) - eye))
            .Where(x => x.to.magnitude <= Range && Vector3.Angle(look, x.to) <= Cone && !Blocked(eye, Chest(x.e)))
            .OrderBy(x => x.to.magnitude).Select(x => x.e).FirstOrDefault();

        private static int BulletLayers()
        {
            if (_bulletLayers == 0)
                _bulletLayers = LayerMask.GetMask("HighPolyCollider", "TransparentCollider", "Terrain");
            return _bulletLayers;
        }

        /// <summary>Something solid between the two points (what a bullet would hit).</summary>
        private static bool Blocked(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            float distance = dir.magnitude;
            if (distance < 0.01f)
                return false;
            return Physics.Raycast(from, dir / distance, distance - 0.3f, BulletLayers(), QueryTriggerInteraction.Ignore);
        }

        // Fika (co-op): on another game, a bot run by the host is a copy whose IsAI is false; Fika marks it with its own
        // IsObservedAI field (read by name: this mod needs no Fika). The Thundergun's way
        private static FieldInfo _observedAI;
        private static bool _observedLooked;

        private static bool IsBot(Player target)
        {
            if (target.IsAI)
                return true;
            if (!_observedLooked)
            {
                _observedLooked = true;
                for (System.Type t = target.GetType(); t != null && _observedAI == null; t = t.BaseType)
                    _observedAI = t.GetField("IsObservedAI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            try
            {
                return _observedAI != null && _observedAI.DeclaringType.IsInstanceOfType(target) && (bool)_observedAI.GetValue(target);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Who the lightning leaves alone: the shooter's own group always; in normal play anyone else is fair game
        /// (scavs, PMCs, bosses, and other players' PMCs: user, 2026-11-16); in a Kennerz Zombies match every human
        /// player (the zombies are the enemy there).
        /// </summary>
        private static bool Spared(Player shooter, Player target)
        {
            string group = shooter.Profile?.Info?.GroupId;
            if (!string.IsNullOrEmpty(group) && target.Profile?.Info?.GroupId == group)
                return true;
            return InZombiesMatch() && !IsBot(target);
        }

        // The zombies core's match flag (KennerZombies.ZombiesMatch.Active), read by name: this mod needs no core
        private static PropertyInfo _zombiesActive;
        private static bool _zombiesLooked;

        private static bool InZombiesMatch()
        {
            if (!_zombiesLooked)
            {
                _zombiesLooked = true;
                _zombiesActive = HarmonyLib.AccessTools.TypeByName("KennerZombies.ZombiesMatch")?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
            }
            try
            {
                return _zombiesActive != null && (bool)_zombiesActive.GetValue(null);
            }
            catch
            {
                return false;
            }
        }

        private static Vector3 Chest(Player target) =>
            target.PlayerBones != null && target.PlayerBones.BodyPartCollidersDictionary.TryGetValue(EBodyPartColliderType.RibcageUp, out BodyPartCollider part) && part?.Collider != null
                ? part.Collider.bounds.center
                : target.Position + Vector3.up * 1.3f;

        /// <summary>
        /// Through the game's own damage, from the player with the Wunderwaffe, so the kill counts like any other (the
        /// Thundergun's way): the chest takes it all, and anything that survives that is killed outright, still credited.
        /// </summary>
        private static void Kill(Player shooter, Player target, Weapon gun, Vector3 origin, Vector3 forward, Vector3 chest)
        {
            BodyPartCollider part = null;
            target.PlayerBones?.BodyPartCollidersDictionary.TryGetValue(EBodyPartColliderType.RibcageUp, out part);
            var damage = new DamageInfo
            {
                DamageType = EDamageType.Bullet,
                Damage = 10000f,
                PenetrationPower = 0f,
                ArmorDamage = 0f,
                HitCollider = part?.Collider,
                Direction = forward,
                HitPoint = chest,
                MasterOrigin = origin,
                HitNormal = -forward,
                Player = Singleton<GameWorld>.Instance.GetAlivePlayerBridgeByProfileID(shooter.ProfileId),
                Weapon = gun,
                IsForwardHit = true,
                BodyPartColliderType = EBodyPartColliderType.RibcageUp,
                SourceId = gun?.TemplateId.ToString() ?? WunderwaffeIds.Weapon,
            };
            // Fika (co-op): another game's copy only records a direct hit, which it sends to the game that runs it
            if (target.GetType().Name == "ObservedPlayer")
                target.ApplyShot(damage, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, (EArmorPlateCollider)0, default);
            else
            {
                target.ApplyDamageInfo(damage, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, 0f);
                if (target.HealthController.IsAlive)
                    target.ActiveHealthController?.Kill(EDamageType.Bullet);
            }
        }

        // ---------- Shocked (zombies) ----------

        // How long a zombie stands shocked before it drops, if the core has no tesla deaths to play
        private const float ShockTime = 1.1f;

        // Zombies already shocked and going down: the lightning passes them by (it would start their death over)
        private static readonly Dictionary<Player, float> ShockedUntil = new Dictionary<Player, float>();

        private static bool Shocked(Player p)
        {
            if (!ShockedUntil.TryGetValue(p, out float until))
                return false;
            if (Time.time < until)
                return true;
            ShockedUntil.Remove(p);
            return false;
        }

        // The zombies core's shock (KennerZombies.ZombiesApi.Shock), read by name: this mod needs no core
        private static MethodInfo _coreShock;
        private static bool _coreShockLooked;

        /// <summary>
        /// The core plays the original's tesla death on the zombie (stood up jerking, then down) and holds it: the seconds
        /// until it's down, when it's killed; 0 if the core can't (an older core: it drops at once).
        /// </summary>
        private static float Shock(Player target, float seconds)
        {
            try
            {
                if (!_coreShockLooked)
                {
                    _coreShockLooked = true;
                    _coreShock = HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("KennerZombies.ZombiesApi"), "Shock",
                        new[] { typeof(Player), typeof(float) });
                }
                if (_coreShock == null)
                    return 0f;
                object down = _coreShock.Invoke(null, new object[] { target, seconds });
                // An older core's Shock returned nothing: its jerking hold, the asked seconds
                return down is float f ? f : _coreShock.ReturnType == typeof(void) ? seconds : 0f;
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe: the zombie shock couldn't be asked for ({e.GetBaseException().Message})");
                return 0f;
            }
        }

        private static IEnumerator KillAfter(Player shooter, Player target, Weapon gun, Vector3 from, Vector3 dir, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (target == null || target.HealthController?.IsAlive != true)
                yield break;
            try
            {
                Kill(shooter, target, gun, from, dir, Chest(target));
                // Down in smoke, burnt (user, 2026-11-16)
                WunderwaffePlugin.Runner.StartCoroutine(Burn(target));
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe kill skipped: {e.Message}");
            }
        }

        // ---------- Burnt ----------

        private const float SmokeFor = 5f;

        /// <summary>
        /// A shocked body going down burnt: dark smoke and a few embers rising off its chest and head as it falls and
        /// for a few seconds after, riding the bones (so the ragdoll carries them).
        /// </summary>
        private static IEnumerator Burn(Player target)
        {
            GameObject prefab = WunderwaffeBolts.Prefab("ww_burn");
            if (prefab == null || target?.PlayerBones == null)
                yield break;
            var smokes = new List<GameObject>();
            var at = new List<Transform>();
            if (target.PlayerBones.BodyPartCollidersDictionary.TryGetValue(EBodyPartColliderType.RibcageUp, out BodyPartCollider chest) && chest != null)
                at.Add(chest.transform);
            if (target.PlayerBones.Head?.Original != null)
                at.Add(target.PlayerBones.Head.Original);
            if (target.PlayerBones.Pelvis?.Original != null)
                at.Add(target.PlayerBones.Pelvis.Original);
            foreach (Transform bone in at)
            {
                GameObject smoke = Object.Instantiate(prefab, bone, false);
                smoke.transform.localPosition = Vector3.zero;
                smoke.SetActive(true);
                smokes.Add(smoke);
            }
            yield return new WaitForSeconds(SmokeFor);
            // Stop making smoke, let what's there drift off, then go
            foreach (GameObject smoke in smokes)
                if (smoke != null)
                    foreach (ParticleSystem ps in smoke.GetComponentsInChildren<ParticleSystem>())
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            yield return new WaitForSeconds(4f);
            foreach (GameObject smoke in smokes)
                if (smoke != null)
                    Object.Destroy(smoke);
        }

        // ---------- Fried ----------

        // The crackle over the body after it's down (it crackles all through the shock before that)
        private const float FryTime = 1.2f;

        /// <summary>
        /// The original's electrocution: blue (purple, Pack-a-Punched) crackling out of the head, a light flickering at
        /// the face, for a moment as the body goes down; in a Kennerz Zombies match each one also screams the nuke's
        /// death scream (user, 2026-11-16). Riding the head bone, so it follows the fall.
        /// </summary>
        private static IEnumerator Fry(Player target, bool upgraded, float seconds)
        {
            Transform head = target?.PlayerBones?.Head?.Original;
            if (head == null)
                yield break;
            if (InZombiesMatch())
                Scream(head.position);
            Light light = new GameObject("ww_fried_light").AddComponent<Light>();
            light.transform.SetParent(head, false);
            light.type = LightType.Point;
            light.color = upgraded ? new Color(0.8f, 0.3f, 1f) : new Color(0.3f, 0.55f, 1f);
            light.range = 1.6f;
            light.shadows = LightShadows.None;
            // The body's parts, for the lightning crawling over it (user, 2026-11-16: around the enemy, not just a light)
            var parts = new List<Transform>();
            if (target.PlayerBones?.BodyPartCollidersDictionary != null)
                foreach (BodyPartCollider c in target.PlayerBones.BodyPartCollidersDictionary.Values)
                    if (c != null && c.transform != null)
                        parts.Add(c.transform);
            float end = Time.time + seconds;
            float nextSpark = 0f, nextCrawl = 0f;
            while (Time.time < end && head != null)
            {
                float left = Mathf.Clamp01((end - Time.time) / FryTime);
                light.intensity = 5f * left * Random.Range(0.45f, 1.15f);
                // Busier than before (user, 2026-11-16: "a bit more obvious going around them")
                if (Time.time >= nextSpark)
                {
                    nextSpark = Time.time + Random.Range(0.03f, 0.07f);
                    WunderwaffeArcs.SparkAround(head, upgraded, 0.22f);
                }
                // Arcs jumping from one part of the body to another (two at a time), and crackling round a limb
                if (parts.Count > 1 && Time.time >= nextCrawl)
                {
                    nextCrawl = Time.time + Random.Range(0.025f, 0.05f);
                    for (int k = 0; k < 2; k++)
                    {
                        Transform a = parts[Random.Range(0, parts.Count)], b = parts[Random.Range(0, parts.Count)];
                        if (a != b)
                            WunderwaffeArcs.Between(a, b, upgraded, Random.Range(0.12f, 0.22f));
                    }
                    WunderwaffeArcs.SparkAround(parts[Random.Range(0, parts.Count)], upgraded, 0.3f);
                }
                yield return null;
            }
            if (light != null)
                Object.Destroy(light.gameObject);
        }

        // The zombies core's sound player (KennerZombies.ZombiesSounds.At), read by name: this mod needs no core
        private static MethodInfo _coreSoundAt;
        private static bool _coreSoundLooked;

        private static void Scream(Vector3 at)
        {
            try
            {
                if (!_coreSoundLooked)
                {
                    _coreSoundLooked = true;
                    _coreSoundAt = HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("KennerZombies.ZombiesSounds"), "At",
                        new[] { typeof(string), typeof(Vector3), typeof(float), typeof(float) });
                }
                _coreSoundAt?.Invoke(null, new object[] { "zm_pu_nuke_soul_#", at, 0.8f, 35f });
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe: the zombie scream couldn't play ({e.Message})");
            }
        }

        // ---------- Look ----------

        private static void Bolt(Vector3 from, Vector3 to, bool upgraded)
        {
            GameObject prefab = WunderwaffeBolts.Prefab(upgraded ? "ww_chain_up" : "ww_chain");
            if (prefab == null)
                return;
            GameObject bolt = Object.Instantiate(prefab, (from + to) / 2f, Quaternion.identity);
            // Its own light off: one light glides along the whole strike instead (Glide); a light per jump popped from
            // place to place (user, 2026-11-16: "teleports")
            foreach (Light l in bolt.GetComponentsInChildren<Light>(true))
                Object.Destroy(l);
            bolt.SetActive(true);
            WunderwaffePlugin.Runner.StartCoroutine(Crackle(bolt, bolt.GetComponentsInChildren<LineRenderer>(), from, to));
            Fog(from, to, upgraded);
        }

        private static IEnumerator Crackle(GameObject bolt, LineRenderer[] lines, Vector3 from, Vector3 to)
        {
            float end = Time.time + BoltLife;
            while (bolt != null && Time.time < end)
            {
                // The bolt and its soft halo on the same fresh path
                if (lines.Length > 0 && lines[0] != null)
                {
                    Jag(lines[0], from, to);
                    for (int i = 1; i < lines.Length; i++)
                        if (lines[i] != null)
                            for (int p = 0; p < Mathf.Min(lines[0].positionCount, lines[i].positionCount); p++)
                                lines[i].SetPosition(p, lines[0].GetPosition(p));
                }
                yield return new WaitForSeconds(Rejag);
            }
            if (bolt != null)
                Object.Destroy(bolt);
        }

        /// <summary>A fresh jagged path from one end to the other (the further from the ends, the wider the jags).</summary>
        private static void Jag(LineRenderer line, Vector3 from, Vector3 to)
        {
            int n = line.positionCount;
            float length = Vector3.Distance(from, to);
            float wild = Mathf.Clamp(length * 0.06f, 0.05f, 0.6f);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                Vector3 p = Vector3.Lerp(from, to, t);
                if (i > 0 && i < n - 1)
                    p += Random.insideUnitSphere * wild * Mathf.Sin(t * Mathf.PI);
                line.SetPosition(i, p);
            }
        }

        /// <summary>The thin fog the bolt leaves along its jump, stretched from one end to the other.</summary>
        private static void Fog(Vector3 from, Vector3 to, bool upgraded)
        {
            GameObject prefab = WunderwaffeBolts.Prefab(upgraded ? "ww_fog_up" : "ww_fog");
            Vector3 dir = to - from;
            float length = dir.magnitude;
            if (prefab == null || length < 0.2f)
                return;
            GameObject fog = Object.Instantiate(prefab, (from + to) / 2f, Quaternion.LookRotation(dir / length));
            foreach (ParticleSystem ps in fog.GetComponentsInChildren<ParticleSystem>(true))
            {
                var shape = ps.shape;
                shape.scale = new Vector3(0.08f, 0.08f, length);
                var emission = ps.emission;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.Clamp(length * 2f, 3f, 30f)) });
            }
            fog.SetActive(true);
            Object.Destroy(fog, 2.5f);
        }

        private static void Zap(Vector3 at, bool upgraded)
        {
            GameObject prefab = WunderwaffeBolts.Prefab(upgraded ? "ww_impact_up_0" : "ww_impact_0");
            if (prefab == null)
                return;
            GameObject fx = Object.Instantiate(prefab, at, Quaternion.identity);
            fx.SetActive(true);
            foreach (AudioSource audio in fx.GetComponentsInChildren<AudioSource>())
                audio.volume *= WunderwaffeSettings.ImpactVolume.Value;
            // Its flash dies straight away (it stayed lit after the lightning was gone: user, 2026-11-16)
            WunderwaffePlugin.Runner.StartCoroutine(FadeLights(fx, 0.25f));
            Object.Destroy(fx, 2.5f);
        }

        private static IEnumerator FadeLights(GameObject fx, float seconds)
        {
            Light[] lights = fx.GetComponentsInChildren<Light>(true);
            float[] full = lights.Select(l => l.intensity).ToArray();
            for (float t = 0f; t < seconds && fx != null; t += Time.deltaTime)
            {
                for (int i = 0; i < lights.Length; i++)
                    if (lights[i] != null)
                        lights[i].intensity = full[i] * (1f - t / seconds);
                yield return null;
            }
            foreach (Light l in lights)
                if (l != null)
                    l.enabled = false;
        }

        /// <summary>
        /// The strike's light: one light travelling smoothly from the muzzle along each jump of the lightning in step
        /// with it, then gone in a moment once the last jump lands.
        /// </summary>
        private static IEnumerator Glide(Vector3 muzzle, List<Vector3> points, bool upgraded)
        {
            var go = new GameObject("ww_strike_light");
            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = upgraded ? new Color(0.8f, 0.3f, 1f) : new Color(0.3f, 0.55f, 1f);
            light.range = 7f;
            light.intensity = 4f;
            light.shadows = LightShadows.None;
            go.transform.position = muzzle;
            Vector3 from = muzzle;
            for (int i = 0; i < points.Count; i++)
            {
                float span = i == 0 ? 0.07f : JumpDelay;
                for (float t = 0f; t < span; t += Time.deltaTime)
                {
                    go.transform.position = Vector3.Lerp(from, points[i], Mathf.SmoothStep(0f, 1f, t / span));
                    light.intensity = 4f * Random.Range(0.75f, 1.1f);
                    yield return null;
                }
                go.transform.position = points[i];
                from = points[i];
            }
            for (float t = 0f; t < 0.18f; t += Time.deltaTime)
            {
                light.intensity = 4f * (1f - t / 0.18f);
                yield return null;
            }
            Object.Destroy(go);
        }
    }
}
