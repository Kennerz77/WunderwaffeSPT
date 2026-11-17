using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// Normal play (user, 2026-11-16): an enemy the lightning hits yells in pain and stands jerking for a moment, the
    /// current running through it, before it drops. Held where it stands (no walking, no firing), its head, spine and
    /// arms twitching over whatever pose its own animation has, frozen facing where it looked; half of them clench on
    /// their trigger as it goes through them, firing into the floor, whatever the gun. Bots this game runs only (a co-op copy drops at once).
    /// In a Kennerz Zombies match the core's own tesla deaths do this instead.
    /// </summary>
    [DefaultExecutionOrder(32010)]
    internal sealed class WunderwaffeShock : MonoBehaviour
    {
        // Longer stood jerking before it drops (user, 2026-11-16: "shock for a bit longer")
        public const float Seconds = 2.6f;

        // How often a shocked bot's hand clenches on its trigger (user, 2026-11-16: "a chance to fire their gun")
        private const float SpasmChance = 0.5f;

        private static readonly Dictionary<Player, WunderwaffeShock> Held = new Dictionary<Player, WunderwaffeShock>();

        /// <summary>
        /// This player is being shocked right now and its trigger is held off (WunderwaffeShot.TriggerPrefix), unless its
        /// hand is clenching on it.
        /// </summary>
        public static bool IsHeld(Player p) => p != null && Held.TryGetValue(p, out WunderwaffeShock s) && !s._firing;

        /// <summary>A shocked bot's round, whichever way its gun points: down into the floor near its feet.</summary>
        public static void Enable(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot)),
                new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(WunderwaffeShock), nameof(ShotPrefix))));
        }

        private static readonly System.Reflection.FieldInfo HandsPlayer = HarmonyLib.AccessTools.Field(typeof(Player.FirearmController), "_player");

        private static void ShotPrefix(Player.FirearmController __instance, ref Vector3 shotDirection)
        {
            try
            {
                if (Held.Count == 0 || !(HandsPlayer?.GetValue(__instance) is Player p) || !Held.ContainsKey(p))
                    return;
                // Its facing, flattened, then 35-70 deg down and up to 25 deg either side: the floor a few metres off
                Vector3 flat = Vector3.ProjectOnPlane(shotDirection, Vector3.up);
                if (flat.sqrMagnitude < 1e-4f)
                    flat = p.Transform.forward;
                flat.Normalize();
                Vector3 side = Vector3.Cross(Vector3.up, flat);
                shotDirection = Quaternion.AngleAxis(Random.Range(-25f, 25f), Vector3.up)
                    * Quaternion.AngleAxis(Random.Range(35f, 70f), side) * flat;
            }
            catch
            {
            }
        }

        // Bone and how far it jerks (degrees)
        private static readonly (string bone, float degrees)[] Parts =
        {
            ("Base HumanSpine2", 5f), ("Base HumanSpine3", 6f), ("Base HumanNeck", 10f), ("Base HumanHead", 16f),
            ("Base HumanLUpperarm", 24f), ("Base HumanRUpperarm", 24f), ("Base HumanLForearm1", 20f), ("Base HumanRForearm1", 20f),
            ("Base HumanLPalm", 28f), ("Base HumanRPalm", 28f), ("Base HumanLThigh1", 4f), ("Base HumanRThigh1", 4f),
        };

        private Player _p;
        private readonly List<(Transform t, float degrees, float seed)> _bones = new List<(Transform, float, float)>();
        private float _from, _until;
        // The trigger clenched: when, and for a gun that fires one round a pull, the next pull and its release
        private bool _spasm, _auto, _firing;
        private float _spasmAt, _releaseAt, _nextPull;
        // Where it was looking when the lightning hit: held there (user, 2026-11-16: frozen still that way)
        private Vector2 _look;

        public static void Begin(Player target, float seconds)
        {
            if (target == null)
                return;
            WunderwaffeShock shock = target.GetComponent<WunderwaffeShock>() ?? target.gameObject.AddComponent<WunderwaffeShock>();
            shock.Init(target, seconds);
            try
            {
                // The pain yell, at once
                target.Say(EPhraseTrigger.OnAgony, true);
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe: no pain yell ({e.Message})");
            }
        }

        private void Init(Player p, float seconds)
        {
            _p = p;
            _from = Time.time;
            _until = Time.time + seconds;
            Held[p] = this;
            _spasm = Random.value < SpasmChance;
            _spasmAt = Time.time + Random.Range(0.08f, Mathf.Max(0.1f, seconds * 0.4f));
            _firing = false;
            _nextPull = 0f;
            try
            {
                _look = p.Rotation;
            }
            catch
            {
            }
            try
            {
                _auto = p.HandsController is Player.FirearmController hands && hands.Item is EFT.InventoryLogic.Weapon gun
                    && (gun.SelectedFireMode == EFT.InventoryLogic.Weapon.EFireMode.fullauto
                        || gun.SelectedFireMode == EFT.InventoryLogic.Weapon.EFireMode.burst);
            }
            catch
            {
                _auto = false;
            }
            if (_bones.Count > 0)
                return;
            var byName = new Dictionary<string, Transform>();
            foreach (Transform t in p.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name))
                    byName[t.name] = t;
            int i = 0;
            foreach (var (bone, degrees) in Parts)
                if (byName.TryGetValue(bone, out Transform t))
                    _bones.Add((t, degrees, 17.3f * ++i + Random.value * 50f));
        }

        private bool Over => _p == null || Time.time >= _until || _p.HealthController == null || !_p.HealthController.IsAlive;

        private void Update()
        {
            if (Over)
                return;
            // Held where it stands
            try
            {
                BotOwner bot = _p.AIData?.BotOwner;
                bot?.Mover?.Stop();
                bot?.SetTargetMoveSpeed(0f);
                bot?.Sprint(false);
                _p.Rotation = _look;
                if (_p.HandsController is Player.FirearmController fc)
                    Trigger(fc);
            }
            catch
            {
            }
        }

        private void LateUpdate()
        {
            if (Over)
            {
                Destroy(this);
                return;
            }
            try
            {
                _p.Rotation = _look;
            }
            catch
            {
            }
            float t = Time.time;
            float weight = Mathf.Clamp01((t - _from) / 0.1f);
            foreach (var (bone, degrees, seed) in _bones)
            {
                if (bone == null)
                    continue;
                // Fast, jagged twitches: two noises, one quick and one quicker
                float x = Noise(t * 17f, seed) + 0.5f * Noise(t * 41f, seed + 3.1f);
                float y = Noise(t * 15f, seed + 7.7f) + 0.5f * Noise(t * 37f, seed + 9.2f);
                float z = Noise(t * 19f, seed + 13.4f) + 0.5f * Noise(t * 43f, seed + 1.9f);
                bone.localRotation = bone.localRotation * Quaternion.Euler(new Vector3(x, y, z) * (degrees * weight / 1.5f));
            }
        }

        /// <summary>
        /// Off, unless the hand clenches: then once (a semi-auto) or held down till it dies (full auto), wherever the
        /// gun points (its rounds bent into the floor: ShotPrefix).
        /// </summary>
        private void Trigger(Player.FirearmController fc)
        {
            if (_spasm && Time.time >= _spasmAt)
            {
                _firing = true;
                // Held down whatever the gun (user, 2026-11-16): an automatic keeps firing; one that fires a round a
                // pull is pulled again and again, as fast as a clenching hand
                if (_auto)
                {
                    if (!fc.IsTriggerPressed)
                        fc.SetTriggerPressed(true);
                    return;
                }
                if (Time.time >= _nextPull)
                {
                    _nextPull = Time.time + Random.Range(0.14f, 0.24f);
                    _releaseAt = Time.time + 0.06f;
                    fc.SetTriggerPressed(true);
                    return;
                }
                if (Time.time >= _releaseAt && fc.IsTriggerPressed)
                    fc.SetTriggerPressed(false);
                return;
            }
            if (fc.IsTriggerPressed)
                fc.SetTriggerPressed(false);
        }

        private void OnDestroy()
        {
            if (_p == null)
                return;
            Held.Remove(_p);
            _firing = false;
            try
            {
                if (_p.HealthController?.IsAlive == true && _p.HandsController is Player.FirearmController fc && fc.IsTriggerPressed)
                    fc.SetTriggerPressed(false);
            }
            catch
            {
            }
        }

        private static float Noise(float t, float seed) => Mathf.PerlinNoise(t, seed) * 2f - 1f;
    }
}
