using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Communications;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Utils;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// The mod's own way to get the Wunderwaffe, no other mod needed: a chalk outline of it on a hideout wall (the original's wall
    /// buy) sells the gun, and a green ammo case beside it sells full batterys, both for roubles from the
    /// stash. A bought gun sits in its outline until the next raid; the wall offers it again on the first hideout visit
    /// after one. The two stations are root objects named <see cref="WallName"/> and <see cref="BoxName"/>, at fixed
    /// spots in the side room (<see cref="Positions"/>).
    /// </summary>
    internal class WunderwaffeStations : MonoBehaviour
    {
        public const string WallName = "Wunderwaffe_WallBuy";
        public const string BoxName = "Wunderwaffe_AmmoBox";
        private const string AmmoCaseTpl = "5aafbde786f774389d0cbc0f"; // the green "Ammunition case"
        private const string BuyAction = "WunderwaffeBuy";
        // Shown in the prompts; the server's config.json has the prices it charges (keep them in step)
        private const int WeaponPrice = WunderwaffeSellPrice.GunRub, CartridgePrice = WunderwaffeSellPrice.CartridgeRub;
        private const float UseDistance = 2.5f;

        private static HideoutPlayerOwner _owner;
        private static float _ownerSince;
        private static bool _bought, _raidSinceBuy, _busy;

        private readonly WunderwaffeInteraction _wallMenu = new WunderwaffeInteraction();
        private readonly WunderwaffeInteraction _boxMenu = new WunderwaffeInteraction();
        private Transform _wall, _box;
        private GameObject _wallGun;

        public static void Enable(Harmony harmony, GameObject host)
        {
            harmony.Patch(AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.Init)),
                postfix: new HarmonyMethod(typeof(WunderwaffeStations).GetMethod(nameof(OwnerInitPostfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            host.AddComponent<WunderwaffeStations>();
        }

        // Every hideout visit: a gun bought before the last raid is on sale again
        private static void OwnerInitPostfix(HideoutPlayerOwner __instance)
        {
            _owner = __instance;
            _ownerSince = Time.unscaledTime;
            if (_raidSinceBuy)
            {
                _bought = false;
                _raidSinceBuy = false;
            }
        }

        /// <summary>Told by the tracker while the player is in a raid.</summary>
        public static void RaidSeen()
        {
            if (_bought)
                _raidSinceBuy = true;
        }

        // The server's config.json InNormalGame: -1 not known yet, 0 off (zombies only), 1 on
        private static volatile int _inNormalGame = -1;
        private static volatile bool _asking;
        private static int _askFails;
        private static float _nextAsk;

        private static void AskServer()
        {
            if (_inNormalGame >= 0 || _asking || Time.unscaledTime < _nextAsk)
                return;
            _asking = true;
            _nextAsk = Time.unscaledTime + 5f;
            Task.Run(() =>
            {
                try
                {
                    string reply = SPT.Common.Http.RequestHandler.PostJson("/wunderwaffe/normalgame", "{}") ?? "";
                    // What this server start runs with ("now"); an older server without the route: on, as before
                    _inNormalGame = reply.Replace(" ", "").Contains("\"now\":false") ? 0 : 1;
                }
                catch
                {
                    if (++_askFails >= 3)
                        _inNormalGame = 1;
                }
                finally
                {
                    _asking = false;
                }
            });
        }

        private void Update()
        {
            HideoutPlayerOwner owner = _owner;
            if (owner == null || owner.HideoutPlayer == null)
            {
                _wallMenu.Reset();
                _boxMenu.Reset();
                return;
            }
            // Only once the hideout's side room is open; shut again (a guest's hideout, say), the stations go quiet.
            // Never while the server's InNormalGame is off (zombies only), or before it has said
            AskServer();
            bool open = _inNormalGame == 1 && SideRoomOpen();
            if (_wall != null && _wall.gameObject.activeSelf != open)
            {
                _wall.gameObject.SetActive(open);
                _box.gameObject.SetActive(open);
            }
            // A moment for the player to be placed before the stations are set out the first time
            if (_wall == null && open && Time.unscaledTime - _ownerSince > 2f)
                SetUp(owner);
            if (_wall == null || !open)
            {
                _wallMenu.Reset();
                _boxMenu.Reset();
                return;
            }
            // Placed from F12 (dev): moved live as the settings change
            var (wallPos, wallRot) = WunderwaffeSettings.WallAt();
            _wall.SetPositionAndRotation(wallPos, wallRot);
            var (boxPos, boxRot) = WunderwaffeSettings.BoxAt();
            _box.SetPositionAndRotation(boxPos, boxRot);
            if (_shownFlip != WunderwaffeSettings.FlipChalk.Value)
            {
                _shownFlip = WunderwaffeSettings.FlipChalk.Value;
                if (_chalk != null)
                    SizeChalk(_chalk, _chalkLength);
                if (_wallGun != null)
                    LayAlong(_wallGun, _wall);
            }
            ShowWallGun(_bought);
            _wallMenu.Tick(owner, _wall, UseDistance, WallOptions);
            _boxMenu.Tick(owner, _box, UseDistance, BoxOptions);
        }

        private const int SideRoomLevel = 6; // the Emergency Wall level that opens the side room

        /// <summary>The player's own hideout has its Emergency Wall upgraded to the side room opening.</summary>
        private static bool SideRoomOpen()
        {
            try
            {
                if (!Singleton<HideoutRepresentation>.Instantiated)
                    return false;
                AreaData wall = Singleton<HideoutRepresentation>.Instance.AreaDatas.FirstOrDefault(a => a.Template.Type == EAreaType.EmergencyWall);
                return wall != null && !wall.IsGuestData && wall.CurrentLevel >= SideRoomLevel;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private IEnumerable<(string, Action)> WallOptions()
        {
            if (_bought)
                yield return ("Wunderwaffe DG-2 bought", null);
            else
                yield return ($"Buy Wunderwaffe DG-2 ({WeaponPrice:N0} ₽)", _busy ? (Action)null : () => Buy("weapon"));
        }

        private IEnumerable<(string, Action)> BoxOptions()
        {
            yield return ($"Buy Wunderwaffe DG-2 battery ({CartridgePrice:N0} ₽)", _busy ? (Action)null : () => Buy("cartridge"));
        }

        // ---------- Buying ----------

        private void Buy(string what)
        {
            ClientBackendSession session = ClientAppUtils.GetMainApp()?.GetClientBackEndSession() as ClientBackendSession;
            if (session == null)
                return;
            _busy = true;
            string[] emptyBefore = what == "weapon" ? WunderwaffeDraw.EmptySlots(_owner) : null;
            session.SendOperationRightNow(new BuyOperation { Action = BuyAction, item = what }, result =>
            {
                _busy = false;
                if (result == null || result.Failed)
                {
                    WunderwaffeStationSounds.Play("deny", WunderwaffeSettings.DenyVolume.Value);
                    NotificationManager.DisplayWarningNotification(string.IsNullOrEmpty(result?.Error) ? "The purchase didn't go through." : result.Error);
                    return;
                }
                WunderwaffeStationSounds.Play("purchase_accept", WunderwaffeSettings.PurchaseVolume.Value);
                if (what == "weapon")
                {
                    // The original's wall buy: the cha-ching and the gun coming off the wall together (the wall sound runs longer)
                    WunderwaffeStationSounds.Play("wallbuy", WunderwaffeSettings.WallBuyVolume.Value);
                    _bought = true;
                    _raidSinceBuy = false;
                    _slideOut = true;
                    // The server equips it when a primary slot is free (otherwise it goes to the stash): then it's drawn
                    if (!WunderwaffeDraw.TryDrawBought(_owner, emptyBefore))
                        NotificationManager.DisplayMessageNotification("Wunderwaffe DG-2 bought.");
                }
                else
                {
                    NotificationManager.DisplayMessageNotification("Battery bought. It's in your stash.");
                }
            });
        }

        /// <summary>Item event body; field names match the server's WunderwaffeBuyRequest.</summary>
        private class BuyOperation
        {
            public string Action;
            public string item;
        }

        // ---------- Setting out ----------

        private void SetUp(HideoutPlayerOwner owner)
        {
            _wall = Root(WallName, WunderwaffeSettings.WallAt());
            Transform chalk = BuildChalk(_wall);
            _chalk = chalk;
            _shownFlip = WunderwaffeSettings.FlipChalk.Value;
            // Drawn life size: the gun's real length, measured from its model once
            StartCoroutine(Model(WunderwaffeIds.Weapon, _wall, model =>
            {
                Bounds bounds = WorldBounds(model);
                float measured = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                // A gun is somewhere between half a metre and two: anything else is a bad measurement
                float length = measured > 0.5f && measured < 2f ? measured : 1.1f;
                WunderwaffePlugin.Log.LogInfo($"Wall buy chalk sized to the gun: {measured:F2} m measured, {length:F2} m used");
                model.transform.SetParent(null, false);
                AssetPoolObject.ReturnToPool(model, true);
                _chalkLength = length;
                if (chalk != null)
                    SizeChalk(chalk, length);
            }, loaded: true));
            _box = Root(BoxName, WunderwaffeSettings.BoxAt());
            StartCoroutine(Model(AmmoCaseTpl, _box, model => FitCollider(_box, model)));
            WunderwaffePlugin.Log.LogInfo("Hideout stations set out");
        }

        private static Transform Root(string name, (Vector3 position, Quaternion rotation) at)
        {
            GameObject old = GameObject.Find(name);
            if (old != null)
                Destroy(old);
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(at.position, at.rotation);
            return go.transform;
        }

        /// <summary>The outline: a flat chalk drawing of the gun, its forward face (+Z) out of the wall.</summary>
        private static Transform BuildChalk(Transform wall)
        {
            Texture2D chalk = WunderwaffeChalk.Make();
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "chalk";
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(wall, false);
            // A hair off the wall so it never flickers into it
            quad.transform.localPosition = new Vector3(0f, 0f, 0.005f);
            var material = new Material(Shader.Find("Sprites/Default")) { mainTexture = chalk };
            quad.GetComponent<Renderer>().sharedMaterial = material;
            wall.gameObject.AddComponent<BoxCollider>();
            SizeChalk(quad.transform, 1f);
            return quad.transform;
        }

        // The picture frames the gun with 4% to spare (TgIcons), so the drawing is that much wider than the gun
        private const float PictureMargin = 1.04f;

        /// <summary>
        /// Sized to the gun, and mirrored: the picture is the gun's left side, muzzle left, and the bought model hangs
        /// the other way round, so the drawing is flipped to match its outline.
        /// </summary>
        private static void SizeChalk(Transform quad, float gunLength)
        {
            Texture2D chalk = quad.GetComponent<Renderer>().sharedMaterial.mainTexture as Texture2D;
            float width = gunLength * PictureMargin, height = chalk != null ? width * chalk.height / chalk.width : width * 0.4f;
            quad.localScale = new Vector3(WunderwaffeSettings.FlipChalk.Value ? width : -width, height, 1f);
            BoxCollider box = quad.parent.GetComponent<BoxCollider>();
            if (box != null)
                box.size = new Vector3(width, height, 0.05f);
        }

        /// <summary>
        /// A bought gun hangs inside its outline until the next raid, loaded with a battery. Just bought, it slides
        /// slowly out of the wall as in the original; on a later visit it's simply there.
        /// </summary>
        private void ShowWallGun(bool show)
        {
            if (show && _wallGun == null && !_loadingGun)
            {
                _loadingGun = true;
                bool slide = _slideOut;
                _slideOut = false;
                StartCoroutine(Model(WunderwaffeIds.Weapon, _wall, model =>
                {
                    _loadingGun = false;
                    _wallGun = model;
                    LayAlong(model, _wall);
                    if (slide)
                        StartCoroutine(SlideOut(model.transform));
                }, loaded: true));
            }
            else if (!show && _wallGun != null)
            {
                // Back to the game's model pool, as it came out
                _wallGun.transform.SetParent(null, false);
                AssetPoolObject.ReturnToPool(_wallGun, true);
                _wallGun = null;
            }
        }

        private bool _loadingGun;
        private Transform _chalk;
        private float _chalkLength = 1f;
        private bool _shownFlip;
        private static bool _slideOut;

        private const float SlideDepth = 0.35f, SlideSeconds = 1.6f;

        /// <summary>From inside the wall out to its place, easing to a stop.</summary>
        private static IEnumerator SlideOut(Transform gun)
        {
            Vector3 end = gun.localPosition, start = end - new Vector3(0f, 0f, Mathf.Sign(end.z) * SlideDepth);
            for (float t = 0f; t < SlideSeconds && gun != null; t += Time.deltaTime)
            {
                float k = t / SlideSeconds;
                gun.localPosition = Vector3.Lerp(start, end, 1f - (1f - k) * (1f - k) * (1f - k));
                yield return null;
            }
            if (gun != null)
                gun.localPosition = end;
        }

        /// <summary>The model laid flat against the wall: its longest side across it, centred, just out from the chalk.</summary>
        private static void LayAlong(GameObject model, Transform wall)
        {
            model.transform.SetParent(null, false);
            model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Bounds bounds = WorldBounds(model);
            // Longest side along the wall's width, thinnest out of the wall
            Quaternion lay = bounds.size.z > bounds.size.x ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
            // Flipped with the chalk: turned about the up axis, so it shows its other side, muzzle the other way
            if (WunderwaffeSettings.FlipChalk.Value)
                lay = Quaternion.Euler(0f, 180f, 0f) * lay;
            model.transform.SetParent(wall, false);
            model.transform.localRotation = lay;
            // Half of it out of the chalk (user, 2026-11-16), on the side the player is: the chalk shows from both sides, so
            // a station turned round had its gun go into the wall
            float side = 1f;
            Camera cam = Camera.main;
            if (cam != null && Vector3.Dot(cam.transform.position - wall.position, wall.forward) < 0f)
                side = -1f;
            model.transform.localPosition = -(lay * bounds.center) + new Vector3(0f, 0f, side * 0.01f);
        }

        private static void FitCollider(Transform root, GameObject model)
        {
            model.transform.SetParent(root, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            Bounds bounds = WorldBounds(model);
            var box = root.gameObject.GetComponent<BoxCollider>() ?? root.gameObject.AddComponent<BoxCollider>();
            box.center = root.InverseTransformPoint(bounds.center);
            box.size = bounds.size;
        }

        /// <summary>
        /// A display model of an item, built the way the hideout's weapon stands build theirs: its bundles loaded, made
        /// by the game's factory, and purely visual (no physics; the colliders off, so only the station's box is hit).
        /// </summary>
        private static IEnumerator Model(string template, Transform parent, Action<GameObject> done, bool loaded = false)
        {
            ItemFactory factory = Singleton<ItemFactory>.Instance;
            Item item = factory.CreateItem(MongoID.Generate(), template, null);
            if (item == null)
                yield break;
            // A display gun with a full battery in it
            if (loaded && item is Weapon weapon && factory.CreateItem(MongoID.Generate(), WunderwaffeIds.Clip, null) is Magazine cartridge)
            {
                Item charges = factory.CreateItem(MongoID.Generate(), WunderwaffeIds.Charge, null);
                if (charges != null)
                {
                    charges.StackObjectsCount = WunderwaffeRounds.Clip;
                    cartridge.Cartridges.Add(charges, false);
                }
                weapon.GetMagazineSlot()?.AddWithoutRestrictions(cartridge);
            }
            Task bundles = EasyAssetsExtensions.WaitForAllBundlesJob(item.GetAllBundleTokens(), () => { }, CancellationToken.None, null);
            while (!bundles.IsCompleted)
                yield return null;
            if (bundles.IsFaulted || parent == null)
                yield break;
            GameObject model = Singleton<ObjectsFactory>.Instance?.CreateItem(item, false);
            if (model == null)
                yield break;
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true))
                c.enabled = false;
            foreach (Rigidbody rb in model.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            model.SetActive(true);
            done(model);
        }

        /// <summary>
        /// The visible model only: active meshes that draw. Inactive parts (the gun's air-wave template), particles and
        /// hidden charges report bounds that can sit at the world's origin and would stretch it.
        /// </summary>
        private static Bounds WorldBounds(GameObject go)
        {
            // The gun's hidden parts (the reload's clip, the materials holder) off first: measured with them, the gun read
            // 1.67 m and its chalk came out far bigger than it (user, 2026-11-16)
            WunderwaffeView.Tidy(go);
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(false)
                .Where(r => r.enabled && !r.forceRenderingOff && (r is MeshRenderer || r is SkinnedMeshRenderer) && r.transform.lossyScale.x > 0.01f).ToArray();
            if (renderers.Length == 0)
                return new Bounds(go.transform.position, Vector3.one * 0.3f);
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer r in renderers.Skip(1))
                bounds.Encapsulate(r.bounds);
            return bounds;
        }

    }

    /// <summary>
    /// The stations' the original sounds (sounds\*.wav beside the plugin): "purchase_accept" (the cha-ching), "wallbuy" (the gun
    /// coming off the wall) and "deny" (refused). Played in the player's head like the original's, at their F12 volumes.
    /// </summary>
    internal static class WunderwaffeStationSounds
    {
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
        private static AudioSource _source;

        public static void Play(string name, float volume)
        {
            try
            {
                if (!Clips.TryGetValue(name, out AudioClip clip))
                    Clips[name] = clip = LoadWav(Path.Combine(Path.GetDirectoryName(typeof(WunderwaffeStationSounds).Assembly.Location), "sounds", name + ".wav"));
                if (clip == null || volume <= 0f)
                    return;
                if (_source == null)
                {
                    var go = new GameObject("wunderwaffe_station_sounds");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _source = go.AddComponent<AudioSource>();
                    _source.spatialBlend = 0f;
                }
                // One source for all: one-shots overlap, so the cha-ching and the wall sound play together
                _source.PlayOneShot(clip, volume);
            }
            catch (Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Station sound {name} failed: {e.Message}");
            }
        }

        /// <summary>A plain 16-bit PCM WAV (Greyhound's export) into a clip.</summary>
        private static AudioClip LoadWav(string path)
        {
            if (!File.Exists(path))
            {
                WunderwaffePlugin.Log.LogWarning($"Sound missing: {path}");
                return null;
            }
            byte[] bytes = File.ReadAllBytes(path);
            int channels = BitConverter.ToInt16(bytes, 22), rate = BitConverter.ToInt32(bytes, 24);
            // Walk the chunks to "data" (there can be others before it)
            int at = 12;
            while (at + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
                int size = BitConverter.ToInt32(bytes, at + 4);
                if (id == "data")
                {
                    int count = Math.Min(size, bytes.Length - at - 8) / 2;
                    var samples = new float[count];
                    for (int i = 0; i < count; i++)
                        samples[i] = BitConverter.ToInt16(bytes, at + 8 + i * 2) / 32768f;
                    AudioClip clip = AudioClip.Create("wunderwaffe_purchase", count / channels, channels, rate, false);
                    clip.SetData(samples, 0);
                    return clip;
                }
                at += 8 + size + (size & 1);
            }
            return null;
        }
    }

    /// <summary>
    /// The chalk outline: drawn from the gun's own inventory picture (icons\wunderwaffe.png, rendered at build time).
    /// Its silhouette's edge as a thick chalk line, the bigger shapes inside as fainter strokes, all broken up a little
    /// like chalk on a rough wall.
    /// </summary>
    internal static class WunderwaffeChalk
    {
        private static Texture2D _made;

        public static Texture2D Make()
        {
            if (_made != null)
                return _made;
            string path = Path.Combine(Path.GetDirectoryName(typeof(WunderwaffeChalk).Assembly.Location), "icons", "wunderwaffe.png");
            if (!File.Exists(path))
            {
                WunderwaffePlugin.Log.LogWarning($"No {path}: the wall buy has no chalk outline");
                return null;
            }
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            source.LoadImage(File.ReadAllBytes(path), false);
            const int w = 768;
            int h = Mathf.Max(32, w * source.height / source.width);

            var solid = new bool[w * h];
            var lum = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = source.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
                    solid[y * w + x] = c.a > 0.3f;
                    lum[y * w + x] = (c.r + c.g + c.b) / 3f;
                }
            }
            UnityEngine.Object.Destroy(source);

            var random = new System.Random(935);
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    // Silhouette edge: anything solid and empty within 2 px of each other
                    bool any = false, all = true;
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            bool s = xx >= 0 && yy >= 0 && xx < w && yy < h && solid[yy * w + xx];
                            any |= s;
                            all &= s;
                        }
                    }
                    float a = any && !all ? 0.95f : 0f;
                    // Inner detail: strong shading changes inside the gun, as thinner, fainter strokes
                    if (a == 0f && solid[i] && x > 0 && y > 0 && x < w - 1 && y < h - 1)
                    {
                        float gx = lum[i + 1] - lum[i - 1], gy = lum[i + w] - lum[i - w];
                        if (gx * gx + gy * gy > 0.035f)
                            a = 0.55f;
                    }
                    if (a > 0f)
                    {
                        // Chalk on a rough wall: uneven pressure and the odd skipped grain
                        a *= 0.65f + 0.35f * Mathf.PerlinNoise(x * 0.09f, y * 0.09f);
                        if (random.NextDouble() < 0.14)
                            a *= 0.25f;
                    }
                    pixels[i] = new Color32(238, 242, 255, (byte)(a * 255));
                }
            }
            _made = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "wunderwaffe_chalk", wrapMode = TextureWrapMode.Clamp };
            _made.SetPixels32(pixels);
            _made.Apply(true, true);
            return _made;
        }
    }
}
