using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// The local player, cheaply: the game world's main player in a raid (and the hideout), else the hideout player
    /// found by a scene search at most every few seconds.
    /// </summary>
    internal static class LocalPlayer
    {
        private static Player _cached;
        private static float _nextSearch;

        public static Player Get()
        {
            if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer is Player main && main != null)
                return main;
            if (_cached != null)
                return _cached;
            if (Time.unscaledTime < _nextSearch)
                return null;
            _nextSearch = Time.unscaledTime + 3f;
            return _cached = Object.FindObjectsOfType<Player>().FirstOrDefault(p => p.IsYourPlayer);
        }
    }

    /// <summary>
    /// Every way the game builds an item model (icons, the inspect window, loot, the hands): a Wunderwaffe model gets its
    /// <see cref="WunderwaffeView"/>; a charge's model (the copper slug it's cloned from: in the chamber, at the shell
    /// port as the gun "ejects" it) is hidden. Also our own icon hash, so the pictures in icons/ are cached on their own.
    /// </summary>
    internal static class WunderwaffeModels
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(IconsHash), nameof(IconsHash.GetItemHash)),
                postfix: new HarmonyMethod(Method(nameof(IconHashPostfix))));
            harmony.Patch(AccessTools.FirstMethod(typeof(ObjectsFactory), m =>
                    m.Name == nameof(ObjectsFactory.CreateItemAsync) && m.ReturnType == typeof(Task<GameObject>)
                    && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(Item)),
                postfix: new HarmonyMethod(Method(nameof(CreateAsyncPostfix))));
            foreach (MethodInfo create in AccessTools.GetDeclaredMethods(typeof(ObjectsFactory)).Where(m =>
                         (m.Name == nameof(ObjectsFactory.CreateItem) || m.Name == nameof(ObjectsFactory.CreateCleanLootPrefab)
                          || m.Name == nameof(ObjectsFactory.CreateLootPrefab) || m.Name == nameof(ObjectsFactory.CreateItemUsablePrefab))
                         && m.ReturnType == typeof(GameObject)
                         && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(Item)))
                harmony.Patch(create, postfix: new HarmonyMethod(Method(nameof(CreatePostfix))));
        }

        private static MethodInfo Method(string name) => typeof(WunderwaffeModels).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static void IconHashPostfix(Item item, ref int __result)
        {
            if (WunderwaffeIds.Is(item))
                __result = WunderwaffeIcons.Salted(__result * 31 + (WunderwaffePaP.IsInfused(item) ? (WunderwaffePaP.IsOrigins(item) ? 2 : 1) : 0));
            else if (WunderwaffeIds.IsClip(item))
                __result = WunderwaffeIcons.Salted(__result * 31 + WunderwaffeBattery.Lit(item));
            else if (WunderwaffeIds.IsCharge(item))
                __result = WunderwaffeIcons.Salted(__result * 17);
        }

        private static void CreatePostfix(GameObject __result, Item item)
        {
            if (__result != null)
                Dress(__result, item);
        }

        private static void CreateAsyncPostfix(ref Task<GameObject> __result, Item item)
        {
            if (WunderwaffeIds.Is(item) || WunderwaffeIds.IsClip(item) || WunderwaffeIds.IsCharge(item))
                __result = Dressed(__result, item);
        }

        private static async Task<GameObject> Dressed(Task<GameObject> task, Item item)
        {
            GameObject go = await task;
            if (go != null)
                Dress(go, item);
            return go;
        }

        private static void Dress(GameObject go, Item item)
        {
            try
            {
                if (WunderwaffeIds.Is(item))
                {
                    WunderwaffeView.Tidy(go);
                    WunderwaffeTracker.Attach(go);
                    // A pooled model already had its view: redo its look for this gun
                    go.GetComponentInChildren<WunderwaffeView>(true)?.Refresh();
                }
                if (WunderwaffeIds.IsClip(item))
                    WunderwaffeBattery.Attach(go, item);
                HiddenCharge.Set(go, WunderwaffeIds.IsCharge(item));
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe model setup skipped: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Marks a charge's model as hidden. Models are pooled by prefab, and the charge shares the vanilla copper slug's,
    /// so a model built for a real slug afterwards gets its renderers back.
    /// </summary>
    internal class HiddenCharge : MonoBehaviour
    {
        public static void Set(GameObject go, bool hide)
        {
            HiddenCharge marker = go.GetComponent<HiddenCharge>();
            if (hide == (marker != null))
                return;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                r.forceRenderingOff = hide;
            if (hide)
                go.AddComponent<HiddenCharge>();
            else
                Destroy(marker);
        }
    }

    /// <summary>
    /// Twice a second: the Wunderwaffe in the hands gets its view if the factory hooks missed it (a pooled model, say).
    /// </summary>
    internal class WunderwaffeTracker : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.1f; // quick enough that a reload never shows one over
            Player player = LocalPlayer.Get();
            WunderwaffeCharges.Tick(player);
            // A raid seen since a wall buy: the wall offers the gun again on the next hideout visit
            if (player != null && !(player is HideoutPlayer))
                WunderwaffeStations.RaidSeen();
            // Other players holding one: their left hand closed round it
            if (player != null && !(player is HideoutPlayer))
                WunderwaffeGrip.Scan();
            if (!(player?.HandsController is Player.FirearmController hands) || !WunderwaffeIds.Is(hands.Item))
                return;
            WunderwaffeRounds.Check(hands);
            if (hands._weaponPrefab != null && hands._weaponPrefab.GetComponentInChildren<WunderwaffeView>(true) == null)
            {
                WunderwaffePlugin.Log.LogInfo("Wunderwaffe hands model had no view; attaching it from the firearm controller");
                Attach(hands._weaponPrefab.gameObject);
            }
        }

        /// <summary>A Wunderwaffe model just built: the view goes on its animator (the weapon root).</summary>
        public static void Attach(GameObject model)
        {
            Animator animator = model.GetComponentInChildren<Animator>(true);
            if (animator == null)
                return;
            // A pooled model the menu froze (WunderwaffeMenuPose) animates again wherever it's used next
            if (WunderwaffeMenuPose.Frozen.Remove(animator))
                animator.enabled = true;
            if (animator.GetComponent<WunderwaffeView>() == null)
                animator.gameObject.AddComponent<WunderwaffeView>();
        }
    }
    /// <summary>
    /// A Wunderwaffe model's look: its sound player at the F12 levels, the original's Pack-a-Punched materials on an
    /// upgraded gun (they ride in the model on the switched-off object ww_upgraded_materials, each as "name_up"), and
    /// the glow by charge, as in the original (reference video): one bright bulb per charge left, the one furthest from
    /// you going out first (user, 2026-11-16), and the core (the tube in the middle) lit while it has any, its glow a
    /// drifting, pulsing plasma like the Ray Gun's tube; each with a small light. Warm white, or on a Pack-a-Punched gun
    /// shifting between purple and pink (user, 2026-11-16). In your hands: through a reload the lit bulbs stay put and
    /// lit, only the spent ones drop out and come back lit with the new battery; the clip that brings them in exists
    /// only during a reload (at rest it sits off behind the gun, which put it in the item window and its framing); and
    /// now and then the idle lightning crawls down the barrel and crackles round the muzzle.
    /// </summary>
    internal class WunderwaffeView : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<WeaponPrefab, Weapon> WeaponData = AccessTools.FieldRefAccess<WeaponPrefab, Weapon>("_weaponData");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly int MainTexST = Shader.PropertyToID("_MainTex_ST");

        private const string Holder = "ww_upgraded_materials", ClipBone = "tag_clip";

        // The core's plasma: UV per second along and round the tube, a slow wobble, and the pulse (the Ray Gun's)
        private static readonly Vector2 Scroll = new Vector2(0.11f, 0.045f);
        private const float WobbleAmount = 0.06f, WobbleHz = 0.23f, PulseDepth = 0.18f, PulseHz = 1.1f;

        private static readonly Color Warm = new Color(1f, 0.74f, 0.32f), Purple = new Color(0.62f, 0.22f, 1f), Pink = new Color(1f, 0.32f, 0.75f);

        /// <summary>The bulbs' and the core's glow colour right now (unscaled): warm white, or purple to pink and back.</summary>
        public static Color Tint(bool upgraded) => upgraded ? Color.Lerp(Purple, Pink, 0.5f + 0.5f * Mathf.Sin(Time.time * 0.8f)) : Warm;

        /// <summary>The bulbs' and the core's glow: bright, not blown out (the build's WwImporter.CoreGlow is the resting look).</summary>
        public static Color Glow(bool upgraded) => Tint(upgraded) * 3.2f;

        /// <summary>
        /// A lit bulb: about what the old dim step was (user, 2026-11-16: that's the right full brightness); a
        /// Pack-a-Punched gun's half-spent bulb dims to a third of it.
        /// </summary>
        public static Color BulbGlow(bool upgraded) => Tint(upgraded) * 0.95f;

        /// <summary>
        /// How lit bulb b (1 nearest you .. 3 nearest the muzzle) is for this many charges left: 0 out, 1 full. The
        /// muzzle end goes first. On a Pack-a-Punched gun each bulb holds two shots: the first dims it, the second puts
        /// it out (user, 2026-11-16).
        /// </summary>
        public static float Level(int bulb, int charges, bool upgraded)
        {
            if (!upgraded)
                return charges >= bulb ? 1f : 0f;
            int left = Mathf.Clamp(charges - 2 * (bulb - 1), 0, 2);
            return left == 2 ? 1f : left == 1 ? DimLevel : 0f;
        }

        public const float DimLevel = 0.33f;

        /// <summary>
        /// A filament's flicker, each light its own: an uneven wander, a fast buzz, and now and then a sharp dip, like a
        /// loose contact (user, 2026-11-16: the flashing should be more obvious, but it only ever dims, never goes out).
        /// </summary>
        public static float Flicker(int bulb)
        {
            float t = Time.time;
            float wander = 0.74f + 0.4f * Mathf.PerlinNoise(t * 6f + bulb * 13.1f, bulb * 3.7f);
            float buzz = 1f + 0.14f * (Mathf.PerlinNoise(t * 38f + bulb * 5.3f, bulb * 2.1f) - 0.5f);
            float n = Mathf.PerlinNoise(t * 2.6f + bulb * 7.7f, 11.3f + bulb);
            float dip = n > 0.68f ? Mathf.Lerp(1f, 0.55f, Mathf.InverseLerp(0.68f, 0.76f, n)) : 1f;
            return wander * buzz * dip;
        }

        /// <summary>Live views, so a Pack-a-Punch change can refresh them at once.</summary>
        public static readonly HashSet<WunderwaffeView> Live = new HashSet<WunderwaffeView>();

        /// <summary>
        /// A freshly built model (any: hands, loot, the item window): the clip and the materials holder switched off.
        /// The item window sizes and centres the gun on every renderer of an active object, hidden or not.
        /// </summary>
        public static void Tidy(GameObject model)
        {
            Transform clip = null;
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
                if (t.name == ClipBone)
                    clip = t;
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name == Holder || (clip != null && r.transform.IsChildOf(clip) && r.transform != clip))
                    r.gameObject.SetActive(false);
            }
        }

        // Every renderer slot with an upgraded twin: its plain and Pack-a-Punched materials
        private readonly List<(Renderer renderer, int index, Material plain, Material upgraded)> _slots = new List<(Renderer, int, Material, Material)>();
        // The bulbs' slots, by bulb (1 nearest you .. 3 nearest the muzzle: tag_bulb_1..3), and the core's
        private readonly List<(Renderer renderer, int index, int bulb)> _bulbs = new List<(Renderer, int, int)>();
        private readonly List<(Renderer renderer, int index)> _core = new List<(Renderer, int)>();
        // The big glow tube at the back (ww_body_glow): dark until the gun is switched on too (user, 2026-11-16)
        private readonly List<(Renderer renderer, int index)> _tube = new List<(Renderer, int)>();
        private readonly MaterialPropertyBlock _tubeBlock = new MaterialPropertyBlock();
        private static readonly int AlbedoColor = Shader.PropertyToID("_Color");
        // The clip's mesh objects (switched on only during a reload)
        private readonly List<GameObject> _clip = new List<GameObject>();
        private readonly MaterialPropertyBlock _dark = new MaterialPropertyBlock(), _plasma = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock[] _bulbBlocks = { null, new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock() };
        // The power switch: its pose in the idle (on), to see a reload flick it off and back on
        private Transform _switch;
        private Quaternion _switchOn;
        private bool _switchWentOff;
        private readonly Transform[] _bulbBones = new Transform[4];
        private readonly Vector3[] _bulbRestPos = new Vector3[4];
        private readonly Quaternion[] _bulbRestRot = new Quaternion[4];
        private readonly Light[] _bulbLights = new Light[4];
        private Light _coreLight;

        // Charges shown (and whether as a Pack-a-Punched gun's), the last ones outside a reload, and those at its start
        private int _chargesShown = 3, _lastCharges = 3, _reloadCharges;
        private bool _upShown, _reloadUp;
        private bool? _shownInfused, _shownClip;
        private bool _reloading, _infused;
        private float _nextCheck, _nextArc;
        private Transform _coilStart, _coilEnd;

        public WeaponPrefab Prefab { get; private set; }

        private void Awake()
        {
            Live.Add(this);
            // The switch's "on" pose, taken as the model is made (the idle's pose, before any animation moves it)
            _switch = GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "tag_switch_animate");
            if (_switch != null)
                _switchOn = _switch.localRotation;
        }

        // The draw: dark until the switch is flicked on (user, 2026-11-16), as in the original's first raise
        private bool _wasInHands, _drawing, _drawWentOff;
        private float _drawStart, _power = 1f, _poweredAt = -10f, _nextFlick;
        private const float FlickerTime = 0.5f;

        /// <summary>
        /// Draw tracking: from the moment the gun comes into your hands it's dark, until its switch has gone off and come
        /// back on; a draw that never moves the switch (the plain pullout) lights after a moment.
        /// </summary>
        private void TrackDraw(bool inHands)
        {
            if (inHands && !_wasInHands)
            {
                _drawing = true;
                _drawWentOff = false;
                _drawStart = Time.time;
            }
            _wasInHands = inHands;
            if (!inHands || !_drawing)
                return;
            float off = _switch != null ? Quaternion.Angle(_switch.localRotation, _switchOn) : 0f;
            if (off > 15f)
                _drawWentOff = true;
            if ((_drawWentOff && off < 5f) || (!_drawWentOff && Time.time - _drawStart > 0.9f) || Time.time - _drawStart > 4f)
            {
                _drawing = false;
                _poweredAt = Time.time;
            }
        }

        private void OnDestroy() => Live.Remove(this);

        private void Start()
        {
            Tidy(gameObject);
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int b = 1; b <= 3; b++)
            {
                Transform bone = all.FirstOrDefault(t => t.name == $"tag_bulb_{b}");
                _bulbBones[b] = bone;
                if (bone == null)
                    continue;
                _bulbRestPos[b] = bone.localPosition;
                _bulbRestRot[b] = bone.localRotation;
                _bulbLights[b] = AddLight(bone, Vector3.zero, 0.12f, 0.9f);
            }
            var upgraded = new Dictionary<string, Material>();
            Renderer holder = GetComponentsInChildren<Renderer>(true).FirstOrDefault(r => r.name == Holder);
            if (holder != null)
                foreach (Material m in holder.sharedMaterials)
                    if (m != null && m.name.EndsWith("_up"))
                        upgraded[m.name.Substring(0, m.name.Length - 3)] = m;
            Transform clipBone = all.FirstOrDefault(t => t.name == ClipBone);
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == holder)
                    continue;
                if (clipBone != null && r.transform.IsChildOf(clipBone) && r.transform != clipBone)
                    _clip.Add(r.gameObject);
                // Which bulb, by the bone it hangs off (the bulb materials' own numbers needn't match their places)
                int bulb = 0;
                for (int b = 1; b <= 3; b++)
                    if (_bulbBones[b] != null && r.transform.IsChildOf(_bulbBones[b]))
                        bulb = b;
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null)
                        continue;
                    string name = mats[i].name.Replace(" (Instance)", "");
                    // Only the parts Pack-a-Punch never paints (bulbs, glow, core) take the original's upgraded
                    // materials: swapping the body under PaP's camo undid Classic / Chronicles, and asking PaP to
                    // repaint after it stacked the Origins crystal into a cyan glow on reloads (user, 2026-11-16)
                    if (upgraded.TryGetValue(name, out Material up) && (name.Contains("_bulb") || name.Contains("_glow") || name.Contains("_power")))
                        _slots.Add((r, i, mats[i], up));
                    if (name.StartsWith("ww_body_bulb"))
                        _bulbs.Add((r, i, bulb > 0 ? bulb : name.Length > 12 && char.IsDigit(name[12]) ? name[12] - '0' : 1));
                    if (name.StartsWith("ww_body_power"))
                        _core.Add((r, i));
                    if (name.StartsWith("ww_body_glow"))
                        _tube.Add((r, i));
                }
            }
            _dark.SetColor(EmissionColor, Color.black);
            if (_core.Count > 0)
            {
                Renderer core = _core[0].renderer;
                _coreLight = AddLight(core.transform, core.transform.InverseTransformPoint(core.bounds.center), 0.35f, 1.6f);
            }
            _coilStart = all.FirstOrDefault(t => t.name == "tag_fx");
            _coilEnd = all.FirstOrDefault(t => t.name == "tag_flash");
            _nextArc = Time.time + Random.Range(ArcMin, ArcMax);

            Prefab = GetComponentInParent<WeaponPrefab>();
            WeaponSoundPlayer sounds = Prefab?.GetComponent<WeaponSoundPlayer>();
            WunderwaffeShotSound.Load(sounds);
            WunderwaffeSounds.Register(sounds);
            WunderwaffePaP.KeepOwnShot();
            WunderwaffeShot.PatchOverrides();
            Refresh();
        }

        // The idle lightning down the barrel: rare (user, 2026-11-16: twice now asked for less often)
        private const float ArcMin = 12f, ArcMax = 25f;

        private static Light AddLight(Transform parent, Vector3 at, float range, float intensity)
        {
            var light = new GameObject("ww_glow_light").AddComponent<Light>();
            light.transform.SetParent(parent, false);
            light.transform.localPosition = at;
            light.type = LightType.Point;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            light.gameObject.layer = parent.gameObject.layer;
            return light;
        }

        /// <summary>The gun this model shows: the container's weapon data, else the local player's held Wunderwaffe.</summary>
        public Weapon ShownWeapon()
        {
            Weapon w = Prefab != null ? WeaponData(Prefab) : null;
            if (w != null && WunderwaffeIds.Is(w))
                return w;
            Player me = LocalPlayer.Get();
            return me?.HandsController is Player.FirearmController fc && fc._weaponPrefab != null && fc._weaponPrefab == Prefab && WunderwaffeIds.Is(fc.Item)
                ? fc.Item as Weapon : null;
        }

        /// <summary>Charges left: the battery's plus the chambered one.</summary>
        private static int Charges(Weapon gun)
        {
            if (gun == null)
                return 3;
            int chambered = gun.Chambers?.Count(c => c.ContainedItem != null) ?? 0;
            return (gun.GetCurrentMagazine()?.Count ?? 0) + chambered;
        }

        private Player.FirearmController MyHands()
        {
            Player me = LocalPlayer.Get();
            return me?.HandsController is Player.FirearmController fc && fc._weaponPrefab != null && fc._weaponPrefab == Prefab ? fc : null;
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 0.05f;
                Weapon gun = ShownWeapon();
                _infused = WunderwaffePaP.IsInfused(gun);
                if (_shownInfused != _infused)
                    Refresh();
                Player.FirearmController hands = MyHands();
                TrackDraw(hands != null);
                bool reloading = hands != null && WunderwaffeRounds.Reloading(hands);
                if (reloading && !_reloading)
                {
                    _reloadCharges = _lastCharges;
                    _reloadUp = _infused;
                    _switchWentOff = false;
                }
                _reloading = reloading;
                if (_reloading)
                {
                    // The original's reload (user, 2026-11-16): all three bulbs come off as the handle goes back, the new
                    // ones come in showing the charge the gun had (the spent ones dim), and the switch flicked back on
                    // lights them. Read from the switch itself: off, then back where the idle has it
                    float off = _switch != null ? Quaternion.Angle(_switch.localRotation, _switchOn) : 0f;
                    if (off > 15f)
                        _switchWentOff = true;
                    bool backOn = _switchWentOff && off < 5f;
                    _chargesShown = backOn ? Charges(gun) : _reloadCharges;
                    _upShown = backOn ? _infused : _reloadUp;
                    // Switched off: dark. Switched back on with the new battery in: it blinks to life like the draw and
                    // the lightning runs down the barrel (user, 2026-11-16)
                    if (_switchWentOff && !backOn)
                        _reloadDark = true;
                    else if (backOn && _reloadDark)
                        PowerBackOn(gun);
                }
                else
                {
                    // A reload that ended without the switch coming back (cut short): power back anyway
                    if (_reloadDark)
                        PowerBackOn(gun);
                    _chargesShown = Charges(gun);
                    _upShown = _infused;
                    _lastCharges = _chargesShown;
                }
                ShowClip(_reloading);
            }
            ShowGlow();
            // The original's idle lightning: now and then an arc crawls down the barrel coil, with its crackle, and
            // sparks round the muzzle, while the gun is loaded, in your hands
            if (Time.time >= _nextArc && _coilStart != null && _coilEnd != null)
            {
                _nextArc = Time.time + Random.Range(ArcMin, ArcMax);
                Weapon gun = ShownWeapon();
                if (MyHands() != null && !_reloading && Charges(gun) > 0)
                    WunderwaffeArcs.Crawl(_coilStart, _coilEnd, _infused);
            }
        }

        private bool _reloadDark;

        /// <summary>After a reload's switch-off: the flicker to life and, loaded, the lightning down the barrel.</summary>
        private void PowerBackOn(Weapon gun)
        {
            _reloadDark = false;
            _poweredAt = Time.time;
            if (Charges(gun) > 0)
                ShotCrawl();
        }

        /// <summary>A shot: the lightning runs down the barrel too (user, 2026-11-16), and the idle one waits a while.</summary>
        public void ShotCrawl()
        {
            if (_coilStart == null || _coilEnd == null)
                return;
            WunderwaffeArcs.Crawl(_coilStart, _coilEnd, _infused);
            _nextArc = Time.time + Random.Range(ArcMin, ArcMax);
        }

        /// <summary>Every frame: the bulbs and the core by charge, the core's plasma moving, the colour drifting when upgraded.</summary>
        private void ShowGlow()
        {
            float t = Time.time;
            // Powered: off while a draw waits for its switch, then it flickers to life like failing electrics, the
            // flicker steadying over half a second (user, 2026-11-16)
            float since = Time.time - _poweredAt;
            if (_drawing || _reloadDark)
                _power = 0f;
            else if (since < FlickerTime)
            {
                if (Time.time >= _nextFlick)
                {
                    _nextFlick = Time.time + Random.Range(0.02f, 0.06f);
                    float onChance = Mathf.Lerp(0.25f, 0.95f, since / FlickerTime);
                    _power = Random.value < onChance ? Random.Range(0.6f, 1.15f) : Random.Range(0f, 0.12f);
                }
            }
            else
                _power = 1f;
            Color glow = Glow(_infused) * _power;
            float pulse = 1f + PulseDepth * Mathf.Sin(t * PulseHz * 2f * Mathf.PI);
            // The core: a softer cloud than the bulbs, drifting, pulsing and flickering
            _plasma.SetColor(EmissionColor, glow * 1.1f * pulse * Flicker(4));
            // The glow tube: the build's own glow (WwImporter.PowerGlow x 1.6), switched and flickering with the rest
            Color tube = (_infused ? new Color(0.8f, 0.3f, 0.95f) : new Color(1f, 0.85f, 0.6f)) * 3.84f;
            // Dark with the gun out of charges, like the core (user, 2026-11-16: it never went off)
            float tubeOn = _chargesShown > 0 ? _power : 0f;
            _tubeBlock.SetColor(EmissionColor, tube * tubeOn * Flicker(5));
            // Its colour map is a bright cream, which still read as lit with the glow off: dulled to an unlit bulb's grey
            _tubeBlock.SetColor(AlbedoColor, Color.Lerp(new Color(0.22f, 0.21f, 0.19f), Color.white, Mathf.Clamp01(tubeOn)));
            foreach (var (renderer, index) in _tube)
                if (renderer != null)
                    renderer.SetPropertyBlock(_tubeBlock, index);
            for (int b = 1; b <= 3; b++)
                _bulbBlocks[b].SetColor(EmissionColor, BulbGlow(_infused) * _power * Level(b, _chargesShown, _upShown) * Flicker(b));
            _plasma.SetVector(MainTexST, new Vector4(1f, 1f, Scroll.x * t, Scroll.y * t + WobbleAmount * Mathf.Sin(t * WobbleHz * 2f * Mathf.PI)));
            foreach (var (renderer, index, bulb) in _bulbs)
                if (renderer != null)
                    renderer.SetPropertyBlock(_bulbBlocks[Mathf.Clamp(bulb, 1, 3)], index);
            foreach (var (renderer, index) in _core)
                if (renderer != null)
                    renderer.SetPropertyBlock(_chargesShown > 0 ? _plasma : _dark, index);
            Color light = Tint(_infused);
            for (int b = 1; b <= 3; b++)
            {
                if (_bulbLights[b] == null)
                    continue;
                float level = Level(b, _chargesShown, _upShown);
                _bulbLights[b].enabled = level > 0f && _power > 0.01f;
                _bulbLights[b].color = light;
                _bulbLights[b].intensity = 0.45f * level * _power * Flicker(b);
            }
            if (_coreLight != null)
            {
                _coreLight.enabled = _chargesShown > 0 && _power > 0.01f;
                _coreLight.color = light;
                _coreLight.intensity = 1.6f * pulse * _power * Flicker(4);
            }
        }

        private void ShowClip(bool show)
        {
            if (_shownClip == show)
                return;
            _shownClip = show;
            foreach (GameObject go in _clip)
                if (go != null)
                    go.SetActive(show);
        }

        /// <summary>The plain or the Pack-a-Punched materials, by the gun's current state.</summary>
        public void Refresh()
        {
            Weapon weapon = ShownWeapon();
            bool infused = WunderwaffePaP.IsInfused(weapon);
            _shownInfused = infused;
            _infused = infused;
            foreach (var (renderer, index, plain, upgraded) in _slots)
            {
                if (renderer == null)
                    continue;
                Material want = infused ? upgraded : plain;
                Material[] mats = renderer.sharedMaterials;
                if (index < mats.Length && mats[index] != want)
                {
                    mats[index] = want;
                    renderer.sharedMaterials = mats;
                }
            }
            _shownClip = null;
        }
    }

    /// <summary>
    /// A battery item's model (in the inventory, the item window, on the ground): its three bulbs lit by the charge
    /// it still holds (user, 2026-11-16). A used battery keeps its count (charges can't be loaded back in).
    /// </summary>
    internal class WunderwaffeBattery : MonoBehaviour
    {
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private readonly MaterialPropertyBlock[] _blocks = { null, new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock() };
        private readonly Renderer[] _bulbs = new Renderer[4];
        private Item _item;
        private int _shown = -1;
        private float _next;

        /// <summary>Bulbs lit: one per charge, at most three.</summary>
        public static int Lit(Item battery) => Mathf.Clamp((battery as Magazine)?.Count ?? 0, 0, 3);

        public static void Attach(GameObject go, Item item)
        {
            WunderwaffeBattery b = go.GetComponent<WunderwaffeBattery>() ?? go.AddComponent<WunderwaffeBattery>();
            b._item = item;
            b._shown = -1;
            b.Show();
        }

        private void Awake()
        {
            for (int b = 1; b <= 3; b++)
                _bulbs[b] = transform.Find($"bulb{b}")?.GetComponent<Renderer>();
        }

        private void Update() => Show();

        /// <summary>
        /// Its bulbs by the charges it holds, the gun's way (a battery holding more than three has been in a
        /// Pack-a-Punched gun: two shots a bulb), in the plain warm glow, flickering.
        /// </summary>
        private void Show()
        {
            int charges = (_item as Magazine)?.Count ?? 0;
            bool upgraded = charges > 3;
            Color glow = WunderwaffeView.BulbGlow(false);
            for (int b = 1; b <= 3; b++)
            {
                if (_bulbs[b] == null)
                    continue;
                _blocks[b].SetColor(EmissionColor, glow * WunderwaffeView.Level(b, charges, upgraded) * WunderwaffeView.Flicker(b));
                _bulbs[b].SetPropertyBlock(_blocks[b]);
            }
        }
    }
}
