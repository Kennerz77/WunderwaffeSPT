using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EFT;
using Wunderwaffe.ModelFormats;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Wunderwaffe.Editor
{
    /// <summary>
    /// Builds the Wunderwaffe DG-2's prefabs from the original's HD model and viewmodel animations (no Blender/FBX):
    /// weapon_wunderwaffe (EFT's weapon rig with the gun model, the original clips and the rebuilt Saiga controller),
    /// weapon_wunderwaffe_container (WeaponPrefab, sound player and the other EFT components the game reads), and the
    /// battery clip magazine (the clip mesh, split off the gun: EFT shows the magazine item in mod_magazine).
    /// </summary>
    public static class WwImporter
    {
        public static string WeaponPrefabPath => $"{WwPaths.Root}/{WwPaths.WeaponName}.prefab";
        public static string ContainerPrefabPath => $"{WwPaths.Root}/{WwPaths.ContainerName}.prefab";
        public static string MagPrefabPath => $"{WwPaths.Root}/{WwPaths.MagName}.prefab";
        public static string ControllerPath => $"{WwPaths.Generated}/{WwPaths.WeaponName}.controller";

        /// <summary>The battery under the gun (tag_clip): the magazine item's model.</summary>
        private const string ClipBone = "tag_clip";

        /// <summary>A gun part left out of the model (none on the Wunderwaffe).</summary>
        private const string UsedBatteries = "";

        /// <summary>The glowing power cells (green; red on the Pack-a-Punched gun): kept out of the camo. The model's name,
        /// and the name our material is saved under.</summary>
        public const string SourcePower = "";
        public const string PowerMaterial = "ww_body_glow";

        /// <summary>The three bulbs (one lit per shot left: the client sets them) and the coil's glow.</summary>
        public static bool Glows(string source) => source.Contains("_bulb") || source.EndsWith("_glow") || source.EndsWith("_power");

        /// <summary>
        /// Our name for one of the model's materials: never containing "raygun" (the released Ray Gun mod tells
        /// Pack-a-Punch to always paint any material with that word, which camo'd the bare metal and the cells).
        /// </summary>
        public static string AssetName(string source) => source.Replace("mtl_wpn_t7_zmb_dg2", "ww_body");

        private const string Anims = "vm_dg2_";

        /// <summary>
        /// Which of the original's animations (and frame range, 30 fps) plays in each of the Saiga's Hands states. The
        /// reload is the original's one (always the empty reload), split at its own notes: raised 1, switch 6, clip out 30,
        /// clip in 76, end 105. The first raise (switch flicked on) is the arming draw and the inspect.
        /// </summary>
        private static readonly Dictionary<string, (string anim, int first, int last, bool loop)> HandsMap =
            new Dictionary<string, (string, int, int, bool)>
            {
                // The original's idle is one still frame: the gun held steady (EFT's own breathing sway moves it)
                ["IDLE"] = (Anims + "idle", 0, 0, true),
                ["TOO IDLE"] = (Anims + "idle", 0, 0, true),
                ["SPAWN"] = (Anims + "pullout", 0, 0, false),
                // A plain draw is the original's pullout; its first raise (the switch flicked on) only when the gun is
                // newly in the hands: OUT TO IDLE ARM, picked by the WwBootUp bool (AddBootUpChoice, WunderwaffeBootUp)
                ["OUT TO IDLE"] = (Anims + "pullout", 0, 999, false),
                ["OUT TO IDLE ARM"] = (Anims + "first_raise", 0, 999, false),
                ["IDLE TO OUT"] = (Anims + "putaway", 0, 999, false),
                ["FAST IDLE TO OUT"] = (Anims + "putaway_quick", 0, 999, false),
                ["FIRE"] = (FireSoft, 0, 999, false),
                // An empty trigger is only the click: no shot animation, no kick
                ["DRY FIRE"] = (Anims + "idle", 0, 0, false),
                ["DRY FIRE DISARMED"] = (Anims + "idle", 0, 0, false),
                ["DRY FIRE JAMS"] = (Anims + "idle", 0, 0, false),
                // Reload: raise and switch off, clip out, clip in, back down
                // The original's reload: switch off 22, handle back 49, battery in 93, handle released 116, switch
                // on 149, end 180
                ["RELOAD START"] = (Anims + "reload_empty", 0, 60, false),
                ["RELOAD OUT"] = (Anims + "reload_empty", 60, 85, false),
                ["RELOAD IN"] = (Anims + "reload_empty", 85, 125, false),
                ["RELOAD END"] = (Anims + "reload_empty", 125, 999, false),
                ["RELOAD CHARGE END"] = (Anims + "reload_empty", 125, 999, false),
                ["RELOAD START EMPTY"] = (Anims + "reload_empty", 50, 60, false),
                ["RELOAD OUT ALL"] = (Anims + "reload_empty", 0, 85, false),
                // Arms down for the inventory, and back up
                ["OPEN INVENTORY"] = (Anims + "putaway", 0, 999, false),
                ["IN INVENTORY"] = (Anims + "putaway", 999, 999, false),
                ["CLOSE INVENTORY"] = (Anims + "pullout", 0, 999, false),
                ["RELOAD INVENTORY END"] = (Anims + "putaway", 0, 999, false),
                ["INV TO RELOAD"] = (Anims + "pullout", 0, 10, false),
                ["HANDS OUT"] = (Anims + "putaway", 0, 999, false),
                ["HANDS IN"] = (Anims + "pullout", 0, 999, false),
                ["PATROL OUT"] = (Anims + "sprint_in", 0, 999, false),
                ["PATROL"] = (Anims + "sprint_loop", 0, 999, true),
                ["PATROL IN"] = (Anims + "sprint_out", 0, 999, false),
                // Inspecting the gun: the original's first raise (the switch flicked, the gun turned to look at)
                ["LOOK"] = (Anims + "first_raise", 0, 999, false),
                ["CHECK"] = (Anims + "idle", 0, 0, true),
            };

        /// <summary>
        /// Playback speed of some Hands states. EFT waits for the shot state to finish before the next trigger pull:
        /// the original fires a shot every 0.12 s (its 8-frame shot at 2.2x), so a burst comes out at the original's pace.
        /// </summary>
        /// <summary>The shot with its kick softened (see <see cref="Softened"/>).</summary>
        private const string FireSoft = "ww_firesoft";

        /// <summary>
        /// How much of the original's shot kick is kept (1 = all) and how many smoothing passes spread its snap. The
        /// original throws the gun up and to the right on every shot; at three a burst that read as far too much (user,
        /// 2026-11-15).
        /// </summary>
        private const float KickScale = 0.5f;
        private const int KickSmoothPasses = 2;

        /// <summary>
        /// The shot softened: each pass averages every frame with its neighbours (the one-frame snap spread over about
        /// three), then every frame is pulled towards the first (the whole kick smaller). Arms and gun move together, so
        /// the hands stay on it; the moving parts (fins, flaps, bolt) keep their own motion.
        /// </summary>
        private static SrcAnim Softened(SrcAnim anim, float scale, int passes)
        {
            var frames = anim.Frames.Select(f => (SrcPose[])f.Clone()).ToList();
            for (int pass = 0; pass < passes; pass++)
            {
                var next = new List<SrcPose[]>(frames.Count) { frames[0] };
                for (int k = 1; k < frames.Count - 1; k++)
                    next.Add(LerpFrame(frames[k], LerpFrame(frames[k - 1], frames[k + 1], 0.5f), 0.5f));
                if (frames.Count > 1)
                    next.Add(frames[frames.Count - 1]);
                frames = next;
            }
            SrcPose[] rest = frames[0];
            frames = frames.Select(f => LerpFrame(rest, f, scale)).ToList();
            // The gun's own moving parts: their rest pose comes from the gun, which the kick no longer moves as much,
            // so they're left to the retarget's model pose
            return new SrcAnim { FrameRate = anim.FrameRate, Parts = anim.Parts, Frames = frames };
        }

        private static SrcPose[] LerpFrame(SrcPose[] a, SrcPose[] b, float t)
        {
            var result = new SrcPose[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                Quaternion qa = Rot(a[i]), qb = Rot(b[i]), q = Quaternion.Slerp(qa, qb, t);
                Vector3 pa = new Vector3(a[i].Pos.X, a[i].Pos.Y, a[i].Pos.Z), pb = new Vector3(b[i].Pos.X, b[i].Pos.Y, b[i].Pos.Z);
                Vector3 pos = Vector3.Lerp(pa, pb, t), x = q * Vector3.right, y = q * Vector3.up, z = q * Vector3.forward;
                result[i] = new SrcPose
                {
                    Pos = new SrcVec { X = pos.x, Y = pos.y, Z = pos.z },
                    AxisX = new SrcVec { X = x.x, Y = x.y, Z = x.z },
                    AxisY = new SrcVec { X = y.x, Y = y.y, Z = y.z },
                    AxisZ = new SrcVec { X = z.x, Y = z.y, Z = z.z },
                };
            }
            return result;
        }

        private static Quaternion Rot(SrcPose p) =>
            Quaternion.LookRotation(new Vector3(p.AxisZ.X, p.AxisZ.Y, p.AxisZ.Z), new Vector3(p.AxisY.X, p.AxisY.Y, p.AxisY.Z));

        private static readonly Dictionary<string, float> StateSpeeds = new Dictionary<string, float>
        {
            ["FIRE"] = 1.3f,
        };

        /// <summary>
        /// Frames over which a state's clip eases in from / out to the exact idle pose, where it starts or ends away
        /// from it (the reload, the draws), so states hand over without a hitch.
        /// </summary>
        private static readonly Dictionary<string, (int easeIn, int easeOut)> Ease = new Dictionary<string, (int, int)>
        {
            ["RELOAD START"] = (6, 0),
            ["RELOAD OUT ALL"] = (6, 0),
            ["RELOAD END"] = (0, 10),
            ["RELOAD CHARGE END"] = (0, 10),
            ["OUT TO IDLE"] = (0, 8),
            ["OUT TO IDLE ARM"] = (0, 10),
            ["CLOSE INVENTORY"] = (0, 8),
            ["HANDS IN"] = (0, 8),
        };

        /// <summary>
        /// The original's sound notes ("sndnt#alias") and the sound each plays: (our event, clip). States playing a range of an
        /// animation with notes get exactly those sounds instead of the Saiga's.
        /// </summary>
        private static readonly Dictionary<string, (string sound, string clip)> SoundAliases = new Dictionary<string, (string, string)>
        {
            ["wpn_tesla_foley"] = ("WwFoley", "tesla_start_reload"),
            // The first raise's own start note sits two frames before its foley note, which plays the same clip: once
            // is enough (the two together flanged)
            ["wpn_tesla_first_raise_start"] = ("WwToIdle", "silence"),
            ["wpn_tesla_switch_flip_off"] = ("WwSwitchOff", "tesla_switch_flip_off"),
            ["wpn_tesla_switch_flip_on"] = ("WwSwitchOn", "tesla_switch_flip_on"),
            ["wpn_tesla_handle_pullback"] = ("WwHandleBack", "tesla_handle_pullback"),
            ["wpn_tesla_handle_release"] = ("WwHandleRelease", "tesla_handle_release"),
            ["wpn_tesla_clip_in"] = ("WwClipIn", "tesla_clip_in"),
            ["wpn_tesla_to_idle"] = ("WwToIdle", "silence"),
        };

        /// <summary>Sounds the original's notes don't have.</summary>
        private static readonly Dictionary<string, (int frame, string sound, string clip)[]> ExtraNotes = new Dictionary<string, (int, string, string)[]>();

        /// <summary>Sound notes per animation, read from the animations as they load (frame, our sound event).</summary>
        private static readonly Dictionary<string, List<(int frame, string sound)>> Notetracks = new Dictionary<string, List<(int, string)>>();

        /// <summary>Our sound events and their clips (event name "Snd" + sound).</summary>
        private static Dictionary<string, string> NotetrackClips => SoundAliases.Values
            .Select(a => new KeyValuePair<string, string>(a.sound, a.clip))
            .Concat(ExtraNotes.Values.SelectMany(n => n).Select(n => new KeyValuePair<string, string>(n.sound, n.clip)))
            .Concat(new[]
            {
                // Not an animation note: the original's Pack-a-Punched shot, played by the client mod on an upgraded gun
                new KeyValuePair<string, string>("WwPapShot", "ww_shot"),
                new KeyValuePair<string, string>("WwLastShot", "ww_shot_last"),
                new KeyValuePair<string, string>("WwIdleHum", "ww_idle"),
                // Not animation notes either: the plain shots, played by the client mod (WunderwaffeShotSound) from its own
                // audio sources; EFT's shot bank is silent (its weapon sound player cut each shot short and muffled them)
                new KeyValuePair<string, string>("WwShot0", "ww_shot"),
            })
            .GroupBy(kv => kv.Key).Select(g => g.First())
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        /// <summary>
        /// Every way into OUT TO IDLE (the plain pullout) gets a twin into OUT TO IDLE ARM (the first raise) that only
        /// runs while the WwBootUp bool is set (WunderwaffeBootUp sets it as a draw starts); the plain one only without it.
        /// </summary>
        private static void AddBootUpChoice(AnimatorController controller)
        {
            const string param = "WwBootUp";
            if (!controller.parameters.Any(p => p.name == param))
                controller.AddParameter(param, AnimatorControllerParameterType.Bool);
            int twins = 0;
            foreach (AnimatorControllerLayer layer in controller.layers)
            {
                AnimatorStateMachine sm = layer.stateMachine;
                AnimatorState plain = sm.states.Select(c => c.state).FirstOrDefault(st => st.name == "OUT TO IDLE");
                AnimatorState boot = sm.states.Select(c => c.state).FirstOrDefault(st => st.name == "OUT TO IDLE ARM");
                if (plain == null || boot == null)
                    continue;
                foreach (ChildAnimatorState from in sm.states)
                    foreach (AnimatorStateTransition t in from.state.transitions.Where(t => t.destinationState == plain).ToArray())
                    {
                        Twin(t, from.state.AddTransition(boot), param);
                        twins++;
                    }
                foreach (AnimatorStateTransition t in sm.anyStateTransitions.Where(t => t.destinationState == plain).ToArray())
                {
                    Twin(t, sm.AddAnyStateTransition(boot), param);
                    twins++;
                }
            }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Wunderwaffe] First raise draw: {twins} way(s) into OUT TO IDLE twinned to OUT TO IDLE ARM on {param}");
        }

        private static void Twin(AnimatorStateTransition t, AnimatorStateTransition copy, string param)
        {
            copy.hasExitTime = t.hasExitTime;
            copy.exitTime = t.exitTime;
            copy.duration = t.duration;
            copy.offset = t.offset;
            copy.hasFixedDuration = t.hasFixedDuration;
            copy.interruptionSource = t.interruptionSource;
            copy.orderedInterruption = t.orderedInterruption;
            copy.canTransitionToSelf = t.canTransitionToSelf;
            foreach (AnimatorCondition c in t.conditions)
                copy.AddCondition(c.mode, c.threshold, c.parameter);
            copy.AddCondition(AnimatorConditionMode.If, 0f, param);
            t.AddCondition(AnimatorConditionMode.IfNot, 0f, param);
        }

        // A state can leave before its clip ends (EFT's transitions use exit times), so late notes are pulled in
        private const float LatestNote = 0.88f;

        private static Dictionary<string, List<(float, string)>> StateSounds(Dictionary<string, (string anim, int first, int last, bool loop)> map,
            Dictionary<string, int> lastFrame)
        {
            var result = new Dictionary<string, List<(float, string)>>
            {
                // The plain pullout plays no draw sound (the base gun's SndDraw is the first raise's): only the first
                // raise has its own
                ["OUT TO IDLE"] = new List<(float, string)>(),
            };
            foreach (var (state, (anim, first, last, _)) in map)
            {
                if (!Notetracks.TryGetValue(anim, out var notes))
                    continue;
                int span = Math.Max(1, last - first);
                // An empty list still replaces the Saiga's sounds: only the original's own play during the reload. A note on
                // the frame where one piece hands over to the next belongs to the next (it played in both)
                int end = lastFrame[anim];
                result[state] = notes.Where(n => n.frame >= first && (n.frame < last || last == end))
                    .Select(n => (Mathf.Min(LatestNote, (n.frame - first) / (float)span), n.sound)).ToList();
            }
            return result;
        }

        /// <summary>
        /// State pairs that play one the original animation in consecutive pieces (the reload's start, out, in and end): the
        /// next piece starts where the last one stopped, so the hand-over must be instant and at the very end. The
        /// Saiga's exit times and crossfades, tuned to its own clips, made the reload stall and jump.
        /// </summary>
        private static HashSet<(string from, string to)> Contiguous(Dictionary<string, (string anim, int first, int last, bool loop)> map)
        {
            var pairs = new HashSet<(string, string)>();
            foreach (var (from, a) in map)
                foreach (var (to, b) in map)
                    if (from != to && a.anim == b.anim && a.last == b.first && a.last > a.first)
                        pairs.Add((from, to));
            return pairs;
        }

        [MenuItem("Wunderwaffe/1. Import Wunderwaffe")]
        public static void ImportAll()
        {
            // Clips are named after their animation and range: drop earlier imports' so none linger in the project
            AssetDatabase.DeleteAsset(WwPaths.Clips);
            WwPaths.EnsureDirs();
            var warnings = new List<string>();

            // The original's HD Wunderwaffe; the original's arms rig (same bone names) gives the hierarchy above the arms
            SrcModel gun = WwClassic.LoadModel();
            SrcModel bind = SrcModel.Load(Path.Combine(WwPaths.ModelDir, WwPaths.ArmsRig));
            Debug.Log($"[Wunderwaffe] Gun model: {gun.Bones.Count} bones, {gun.Vertices.Count} verts, {gun.Faces.Count} faces, {gun.Materials.Count} materials");

            Dictionary<string, AudioClip> sounds = ImportSounds();
            WwFx.BuildAll(sounds);

            var rig = new Dictionary<string, Transform>();
            GameObject root = WwRig.Build(WwPaths.WeaponName, rig);
            try
            {
                Transform weapon = rig["weapon"];
                // The gun model's root (j_gun) faces the way the original's identity pose does in prefab space: forward +Z, up +Y
                var jgun = new GameObject("j_gun").transform;
                jgun.SetParent(weapon, false);
                jgun.gameObject.layer = weapon.gameObject.layer;
                Quaternion jgunLocal = Quaternion.Inverse(weapon.rotation) * WwSpace.Rot(gun.Bones[0].Rest);
                jgun.localRotation = jgunLocal;
                Dictionary<string, Transform> gunBones = BuildGunSkeleton(gun, jgun);

                var materials = new Dictionary<int, Material>();
                for (int m = 0; m < gun.Materials.Count; m++)
                    materials[m] = BuildMaterial(gun.Materials[m], false);
                BuildGunMeshes(gun, gunBones, materials);
                AddUpgradedMaterials(gun, jgun);

                var anims = new Dictionary<string, SrcAnim>();
                Notetracks.Clear();
                SrcAnim Anim(string n)
                {
                    if (anims.TryGetValue(n, out SrcAnim a))
                        return a;
                    a = anims[n] = WwClassic.LoadAnim(n, gun, bind, out var notes);
                    var noteSounds = new List<(int, string)>();
                    foreach (var (frame, note) in notes.Where(x => x.note.StartsWith("sndnt#")))
                    {
                        string alias = note.Substring("sndnt#".Length);
                        if (SoundAliases.TryGetValue(alias, out var s))
                            noteSounds.Add((frame, s.sound));
                        else
                            warnings.Add($"{n}: no sound for note {alias} at frame {frame}");
                    }
                    if (ExtraNotes.TryGetValue(n, out var extra))
                        noteSounds.AddRange(extra.Select(x => (x.frame, x.sound)));
                    if (noteSounds.Count > 0)
                        Notetracks[n] = noteSounds;
                    return a;
                }

                anims[FireSoft] = Softened(Anim(Anims + "fire"), KickScale, KickSmoothPasses);
                var retarget = new WwRetarget(root.transform, rig, gun, gunBones, jgunLocal, bind, Anim(Anims + "idle"));
                Debug.Log($"[Wunderwaffe] View space offset: {retarget.Offset}, shoulders moved {retarget.ShoulderShifts}");
                retarget.PlaceMarkers(Anim(Anims + "idle"), null);

                // One clip per distinct (animation, range)
                var clips = new Dictionary<string, AnimationClip>();
                var handsClips = new Dictionary<string, AnimationClip>();
                foreach (var (state, (anim, first, last, loop)) in HandsMap)
                {
                    SrcAnim a = Anim(anim);
                    int f0 = Math.Min(first, a.Frames.Count - 1), f1 = Math.Min(last, a.Frames.Count - 1);
                    Ease.TryGetValue(state, out var ease);
                    string key = $"{anim.Replace(Anims, "ww_")}_{f0}_{f1}" + (ease.easeIn + ease.easeOut > 0 ? $"_e{ease.easeIn}_{ease.easeOut}" : "");
                    if (!clips.TryGetValue(key, out AnimationClip clip))
                    {
                        var clipWarnings = new List<string>();
                        // The shot keeps the original's own kick (its fins and flaps snap with it)
                        clip = retarget.BakeClip(key, a, f0, f1, loop, clipWarnings, kick: false,
                            easeIn: ease.easeIn, easeOut: ease.easeOut);
                        string clipPath = $"{WwPaths.Clips}/{key}.anim";
                        AssetDatabase.DeleteAsset(clipPath);
                        AssetDatabase.CreateAsset(clip, clipPath);
                        clips[key] = clip;
                        if (clipWarnings.Count > 0)
                            warnings.Add($"{key}: {clipWarnings.Count} frame(s) out of reach, first: {clipWarnings[0]}");
                    }
                    handsClips[state] = clip;
                }
                AnimationClip idle = handsClips["IDLE"];
                Debug.Log($"[Wunderwaffe] Baked {clips.Count} clips for {handsClips.Count} Hands states");

                // HandsMap's "999" (to the end) is resolved per animation for the notetrack spans
                var resolved = HandsMap.ToDictionary(kv => kv.Key, kv => (kv.Value.anim, Math.Min(kv.Value.first, Anim(kv.Value.anim).Frames.Count - 1),
                    Math.Min(kv.Value.last, Anim(kv.Value.anim).Frames.Count - 1), kv.Value.loop));
                AnimatorController controller = WwController.Build(ControllerPath, root.transform, handsClips, idle,
                    StateSounds(resolved, anims.ToDictionary(kv => kv.Key, kv => kv.Value.Frames.Count - 1)), Contiguous(resolved), StateSpeeds);
                AddBootUpChoice(controller);
                AddGunCollider(weapon, jgun);
                if (!rig.ContainsKey("fireport"))
                    warnings.Add("no fireport in the rig: the gun can't fire");

                // The clip is part of the gun model (the original animates it through every reload), so the magazine
                // item EFT hangs in the magazine slots, and the round it shows in the chamber, stay out of sight. The
                // slots still ride the clip's bones.
                foreach (string slot in new[] { "mod_magazine", "mod_magazine_new", "patron_in_weapon" })
                {
                    if (rig.TryGetValue(slot, out Transform t))
                        t.localScale = Vector3.one * 0.001f;
                }

                // Rest pose for the prefab (inventory icons, and before the Animator runs): the idle
                retarget.ApplyFrame(Anim(Anims + "idle"), 0);

                var animator = root.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var links = root.AddComponent<TransformLinks>();
                CopyFields(links, ComponentFields("TransformLinks", "_10_12g.generated"), r => ResolveInRig(r, root.transform));
                links.Self = root.transform;

                AssetDatabase.DeleteAsset(WeaponPrefabPath);
                GameObject weaponPrefab = PrefabUtility.SaveAsPrefabAsset(root, WeaponPrefabPath);
                BuildContainer(weaponPrefab, controller, sounds);
                BuildMag(gun, materials, MagPrefabPath, Anim(Anims + "reload_empty"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            foreach (string w in warnings)
                Debug.LogWarning("[Wunderwaffe] " + w);
            AssetDatabase.SaveAssets();
            Debug.Log("[Wunderwaffe] Import done");
        }

        // ---------- Gun model ----------

        private static Dictionary<string, Transform> BuildGunSkeleton(SrcModel gun, Transform jgun)
        {
            var bones = new Dictionary<string, Transform> { [gun.Bones[0].Name] = jgun };
            Quaternion[] world = gun.Bones.Select(b => WwSpace.Rot(b.Rest)).ToArray();
            Vector3[] pos = gun.Bones.Select(b => WwSpace.Pos(b.Rest.Pos)).ToArray();
            for (int i = 1; i < gun.Bones.Count; i++)
            {
                SrcBone b = gun.Bones[i];
                var t = new GameObject(b.Name).transform;
                t.SetParent(bones[gun.Bones[b.Parent].Name], false);
                t.gameObject.layer = jgun.gameObject.layer;
                Quaternion parentRot = world[b.Parent];
                t.localPosition = Quaternion.Inverse(parentRot) * (pos[i] - pos[b.Parent]);
                t.localRotation = Quaternion.Inverse(parentRot) * world[i];
                bones[b.Name] = t;
            }
            return bones;
        }

        /// <summary>The model is rigid (one bone per vertex): a plain mesh per bone, no skinning.</summary>
        private static void BuildGunMeshes(SrcModel gun, Dictionary<string, Transform> bones, Dictionary<int, Material> materials)
        {
            int used = gun.BoneIndex(UsedBatteries);
            Dictionary<int, Texture2D> reveals = BuildMetalMaterials(gun, materials);
            try
            {
                foreach (var group in gun.Faces.GroupBy(f => FaceBone(gun, f)).Where(g => g.Key != used))
                {
                    string boneName = gun.Bones[group.Key].Name;
                    Mesh mesh = BuildMesh(gun, group.ToList(), group.Key, $"{WwPaths.WeaponName}_{boneName}", out List<int> mats);
                    if (mesh == null)
                        continue;
                    var go = new GameObject($"{WwPaths.WeaponName}_{boneName}_LOD0");
                    go.layer = bones[boneName].gameObject.layer;
                    go.transform.SetParent(bones[boneName], false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterials = mats.Select(m => materials[m]).ToArray();

                    // The bare metal over it, cut out to the pixel (see BuildMetalMaterials)
                    List<SrcFace> metal = group.Where(f => reveals.TryGetValue(f.Material, out Texture2D r) && TouchesMetal(f, r))
                        .Select(f => new SrcFace { Object = f.Object, Material = MetalMaterialBase + f.Material, Corners = f.Corners }).ToList();
                    if (metal.Count == 0)
                        continue;
                    Mesh metalMesh = BuildMesh(gun, metal, group.Key, $"{WwPaths.WeaponName}_{boneName}_metal", out List<int> metalMats);
                    if (metalMesh == null)
                        continue;
                    var over = new GameObject($"{WwPaths.WeaponName}_{boneName}_metal_LOD0");
                    over.layer = go.layer;
                    over.transform.SetParent(bones[boneName], false);
                    over.AddComponent<MeshFilter>().sharedMesh = metalMesh;
                    var renderer = over.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = metalMats.Select(m => materials[m]).ToArray();
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }
            finally
            {
                foreach (Texture2D t in reveals.Values)
                    Object.DestroyImmediate(t);
            }
        }

        /// <summary>
        /// A box around the gun, switched off like the vanilla guns' (the game turns it on when the gun is dropped).
        /// Left on, it pushes against the player's own body while held: the slide across the floor.
        /// </summary>
        private static void AddGunCollider(Transform weapon, Transform jgun)
        {
            var bounds = new Bounds();
            bool first = true;
            foreach (MeshFilter mf in jgun.GetComponentsInChildren<MeshFilter>())
            {
                foreach (Vector3 v in mf.sharedMesh.vertices)
                {
                    Vector3 p = weapon.InverseTransformPoint(mf.transform.TransformPoint(v));
                    if (first) { bounds = new Bounds(p, Vector3.zero); first = false; }
                    else bounds.Encapsulate(p);
                }
            }
            var go = new GameObject($"{WwPaths.WeaponName}_collider");
            go.layer = weapon.gameObject.layer;
            go.transform.SetParent(weapon, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = bounds.size;
            box.enabled = false;
        }

        /// <summary>
        /// Render queue that keeps Pack-a-Punch's camo off a material: PaP doesn't paint anything from the alpha-test
        /// queue up. Still drawn as solid. Used for the glowing power cells.
        /// </summary>
        private const int KeepLookQueue = 2450;

        private static int FaceBone(SrcModel gun, SrcFace f)
        {
            var votes = f.Corners.Select(c => gun.Vertices[c.Vertex].Weights.OrderByDescending(w => w.weight).First().bone).ToList();
            return votes.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        }

        /// <summary>Faces into a mesh in the bone's local space, one submesh per material.</summary>
        private static Mesh BuildMesh(SrcModel gun, List<SrcFace> faces, int bone, string name, out List<int> materialOrder)
        {
            Quaternion inv = Quaternion.Inverse(WwSpace.Rot(gun.Bones[bone].Rest));
            Vector3 origin = WwSpace.Pos(gun.Bones[bone].Rest.Pos);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var unique = new Dictionary<(int, Vector3, Vector2), int>();
            var submeshes = new Dictionary<int, List<int>>();

            foreach (SrcFace f in faces)
            {
                if (!submeshes.TryGetValue(f.Material, out List<int> tris))
                    submeshes[f.Material] = tris = new List<int>();
                var corner = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    SrcCorner k = f.Corners[c];
                    Vector3 n = (inv * WwSpace.Dir(k.Normal)).normalized;
                    var uv = new Vector2(k.U, 1f - k.V);
                    var key = (k.Vertex, n, uv);
                    if (!unique.TryGetValue(key, out int index))
                    {
                        index = vertices.Count;
                        unique[key] = index;
                        vertices.Add(inv * (WwSpace.Pos(gun.Vertices[k.Vertex].Pos) - origin));
                        normals.Add(n);
                        uvs.Add(uv);
                    }
                    corner[c] = index;
                }
                tris.AddRange(corner);
            }
            if (vertices.Count == 0)
            {
                materialOrder = null;
                return null;
            }

            materialOrder = submeshes.Keys.OrderBy(k => k).ToList();
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = materialOrder.Count;
            for (int i = 0; i < materialOrder.Count; i++)
            {
                int[] tris = submeshes[materialOrder[i]].ToArray();
                // The axis mirror flips winding: keep whichever winding agrees with the vertex normals
                int agree = 0;
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 a = vertices[tris[t]], b = vertices[tris[t + 1]], c = vertices[tris[t + 2]];
                    if (Vector3.Dot(Vector3.Cross(b - a, c - a), normals[tris[t]]) > 0f)
                        agree++;
                }
                if (agree * 2 < tris.Length / 3)
                {
                    for (int t = 0; t < tris.Length; t += 3)
                        (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
                }
                // The ammo bulbs are tiny cards set into the drum: draw them from both sides so none is culled away
                int matIndex = materialOrder[i];
                if (matIndex < gun.Materials.Count && gun.Materials[matIndex].Name.Contains("_glow_"))
                    tris = tris.Concat(Enumerable.Range(0, tris.Length / 3).SelectMany(t => new[] { tris[t * 3], tris[t * 3 + 2], tris[t * 3 + 1] })).ToArray();
                mesh.SetTriangles(tris, i);
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            string path = $"{WwPaths.Generated}/{name}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        // ---------- Magazine ----------

        /// <summary>
        /// The battery clip item model: the clip block with its three batteries (tag_clip and the bones under it),
        /// split off the gun model. The fresh and spent batteries a reload swaps (tag_battery_new/used_animate) stay out:
        /// at rest they sit inside the clip.
        /// </summary>
        private static void BuildMag(SrcModel gun, Dictionary<int, Material> materials, string path, SrcAnim reload)
        {
            int clip = gun.BoneIndex(ClipBone);
            if (clip < 0)
            {
                Debug.LogError($"[Wunderwaffe] No {ClipBone} in the gun model: no magazine model");
                return;
            }
            var clipBones = new HashSet<int> { clip };
            for (int i = 0; i < gun.Bones.Count; i++)
                if (gun.Bones[i].Parent == clip && gun.Bones[i].Name != UsedBatteries)
                    clipBones.Add(i);
            List<SrcFace> faces = gun.Faces.Where(f => clipBones.Contains(FaceBone(gun, f))).ToList();
            string name = Path.GetFileNameWithoutExtension(path);
            Mesh mesh = BuildMesh(gun, faces, clip, $"{name}_mesh", out List<int> mats);
            if (mesh == null)
            {
                Debug.LogError($"[Wunderwaffe] No clip geometry on {ClipBone}");
                return;
            }

            var root = new GameObject(name);
            try
            {
                var lod = new GameObject($"{name}_LOD0");
                lod.transform.SetParent(root.transform, false);
                lod.AddComponent<MeshFilter>().sharedMesh = mesh;
                lod.AddComponent<MeshRenderer>().sharedMaterials = mats.Select(m => materials[m]).ToArray();
                // Off, like vanilla magazines: the clip rides in the gun, and an active collider there pushes
                // against the player's own body (the slide across the floor). The game enables it when dropped.
                var box = lod.AddComponent<BoxCollider>();
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size;
                box.enabled = false;

                // EFT places the top rounds here; the charges live inside the batteries, so keep them out of sight
                foreach (string patron in new[] { "patron_001", "patron_002" })
                {
                    var p = new GameObject(patron).transform;
                    p.SetParent(root.transform, false);
                    p.localPosition = mesh.bounds.center;
                    p.localScale = Vector3.one * 0.01f;
                }

                AddBatteryBulbs(gun, materials, root.transform, clip, reload);

                var pivot = root.AddComponent<PreviewPivot>();
                CopyFields(pivot, ComponentFields("PreviewPivot", "mag_saiga12", "mag_components.json"), _ => null);
                pivot.pivotPosition = mesh.bounds.center;

                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[Wunderwaffe] Clip prefab {path}: {faces.Count} faces, bounds {mesh.bounds.size}");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// The battery item carries its three bulbs (user, 2026-11-16), set in the clip as the reload brings them in:
        /// the frame of the original's reload where the clip and the bulbs come closest (the clip carrying the new
        /// bulbs in), each bulb's pose relative to the clip. Children bulb1..3 (the client lights them by the
        /// battery's charge).
        /// </summary>
        private static void AddBatteryBulbs(SrcModel gun, Dictionary<int, Material> materials, Transform root, int clip, SrcAnim reload)
        {
            int clipPart = reload?.PartIndex(ClipBone) ?? -1;
            int probe = reload?.PartIndex("tag_bulb_2") ?? -1;
            if (clipPart < 0 || probe < 0)
            {
                Debug.LogWarning("[Wunderwaffe] Reload has no clip or bulb poses: the battery item has no bulbs");
                return;
            }
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int f = 0; f < reload.Frames.Count; f++)
            {
                float d = Vector3.Distance(WwSpace.Pos(reload.Frames[f][clipPart].Pos), WwSpace.Pos(reload.Frames[f][probe].Pos));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = f;
                }
            }
            SrcPose clipPose = reload.Frames[best][clipPart];
            Vector3 clipPos = WwSpace.Pos(clipPose.Pos);
            Quaternion clipInv = Quaternion.Inverse(WwSpace.Rot(clipPose));
            for (int b = 1; b <= 3; b++)
            {
                int bone = gun.BoneIndex($"tag_bulb_{b}");
                int part = reload.PartIndex($"tag_bulb_{b}");
                if (bone < 0 || part < 0)
                    continue;
                List<SrcFace> faces = gun.Faces.Where(f => FaceBone(gun, f) == bone).ToList();
                Mesh mesh = BuildMesh(gun, faces, bone, $"{root.name}_bulb{b}", out List<int> mats);
                if (mesh == null)
                    continue;
                SrcPose pose = reload.Frames[best][part];
                var go = new GameObject($"bulb{b}");
                go.transform.SetParent(root, false);
                go.transform.localPosition = clipInv * (WwSpace.Pos(pose.Pos) - clipPos);
                go.transform.localRotation = clipInv * WwSpace.Rot(pose);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats.Select(m => BatteryMaterial(materials[m], b)).ToArray();
            }
            Debug.Log($"[Wunderwaffe] Battery bulbs set as at reload frame {best} (clip to bulb 2: {bestDistance * 100f:F1} cm)");
        }

        /// <summary>A copy of a bulb material for the battery item alone (its own asset: nothing done to the gun's reaches it).</summary>
        private static Material BatteryMaterial(Material source, int bulb)
        {
            // One copy per bulb and source material (a bulb can have more than one), updated in place
            string path = $"{WwPaths.Root}/Materials/ww_battery_bulb{bulb}_{source.name}.mat";
            var copy = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (copy == null)
            {
                copy = new Material(source) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(copy, path);
            }
            else
            {
                copy.shader = source.shader;
                copy.CopyPropertiesFromMaterial(source);
                EditorUtility.SetDirty(copy);
            }
            return copy;
        }

        // ---------- Container (WeaponPrefab and friends) ----------

        private static void BuildContainer(GameObject weaponPrefab, AnimatorController controller, Dictionary<string, AudioClip> sounds)
        {
            var root = new GameObject(WwPaths.ContainerName);
            try
            {
                // One single shot per round fired (the original's player fire sound is a whole burst: played per shot it
                // stacked into four-plus); EFT picks one of the three at random
                AudioClip[] shots = Enumerable.Range(0, 3).Select(i => sounds.TryGetValue($"ww_shot_0{i}", out AudioClip s) ? s : null)
                    .Where(c => c != null).DefaultIfEmpty(sounds["silence"]).ToArray();
                AudioClip shot = shots[0];
                // EFT's bank stays silent: the client plays the shots (see NotetrackClips)
                shots = new[] { sounds["silence"] };
                shot = shots[0];
                SoundBank body = BuildBank("wunderwaffe_fire", 600f, shots);
                SoundBank tail = BuildBank("wunderwaffe_tail", 600f, new[] { sounds["silence"] });
                SoundBank bodySilenced = BuildBank("wunderwaffe_fire_silenced", 130f, shots);
                SoundBank tailSilenced = BuildBank("wunderwaffe_tail_silenced", 130f, new[] { sounds["silence"] });

                Object Resolve(string r)
                {
                    if (r == null) return null;
                    if (r.StartsWith("GameObject:") && r.Contains(".generated")) return weaponPrefab;
                    if (r.StartsWith("SO:SoundBank:"))
                    {
                        string bank = r.Substring("SO:SoundBank:".Length);
                        return bank.EndsWith("tail_silenced") ? tailSilenced : bank.EndsWith("tail") ? tail : bank.EndsWith("silenced") ? bodySilenced : body;
                    }
                    if (r.StartsWith("MB:TransformLinks@")) return weaponPrefab.GetComponent<TransformLinks>();
                    if (r.StartsWith("Transform:")) return ResolveInRig(r, weaponPrefab.transform);
                    return null;
                }

                var wp = root.AddComponent<WeaponPrefab>();
                CopyFields(wp, ComponentFields("WeaponPrefab", "_container"), Resolve);
                wp._weaponObject = weaponPrefab;
                wp._weaponObjectSimple = weaponPrefab;
                wp._originalAnimatorController = controller;
                wp._animatorSimple = null;
                wp._animatorSpirit = null;
                wp._fastAnimatorControllerBinaryData = null;
                wp._avatar = null;
                wp.Colliders = new List<Collider>();
                wp.Renderers = new List<Renderer>();
                wp._originallyEnabledComponents = new List<Component>();
                wp.RegisteredComponentsToClean = new List<Component>();
                wp.PoolHistory = new List<string>();
                wp.Hierarchy = null;

                CopyFields(root.AddComponent<MuzzleManager>(), ComponentFields("MuzzleManager", "_container"), Resolve);
                CopyFields(root.AddComponent<ShellExtractionData>(), ComponentFields("ShellExtractionData", "_container"), Resolve);
                CopyFields(root.AddComponent<PreviewPivot>(), ComponentFields("PreviewPivot", "_container"), Resolve);

                var player = root.AddComponent<WeaponSoundPlayer>();
                CopyFields(player, ComponentFields("WeaponSoundPlayer", "_container"), Resolve);
                player.Body = body;
                player.Tail = tail;
                player.BodySilenced = bodySilenced;
                player.TailSilenced = tailSilenced;
                foreach (WeaponSoundPlayer_SoundElement e in player.AdditionalSounds)
                {
                    string clip = FoleyFor(e.EventName, out float volume);
                    e.SoundClips = new List<AudioClip> { sounds.TryGetValue(clip, out AudioClip c) ? c : sounds["silence"] };
                    e.Volume = volume;
                }
                foreach (var (sound, clip) in NotetrackClips)
                {
                    player.AdditionalSounds.Add(new WeaponSoundPlayer_SoundElement
                    {
                        EventName = "Snd" + sound, RollOff = 30, Volume = 1f,
                        SoundClips = new List<AudioClip> { sounds.TryGetValue(clip, out AudioClip c) ? c : sounds["silence"] },
                    });
                }
                player.MainSounds = player.MainSounds ?? new List<WeaponSoundPlayer_SoundElement>();
                foreach (WeaponSoundPlayer_SoundElement e in player.MainSounds)
                    e.SoundClips = new List<AudioClip> { shot };
                // Semi-auto: the whole shot plays (the client fires the burst one shot at a time). As an "automatic"
                // weapon EFT loops the body sound and cuts it one beat later for the tail.
                player.Non_auto = 1;

                AssetDatabase.DeleteAsset(ContainerPrefabPath);
                PrefabUtility.SaveAsPrefabAsset(root, ContainerPrefabPath);
                Debug.Log($"[Wunderwaffe] Container prefab {ContainerPrefabPath}: {player.AdditionalSounds.Count} foley events");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>The original's Wunderwaffe foley for each of EFT's weapon sound events.</summary>
        private static string FoleyFor(string eventName, out float volume)
        {
            volume = 1f;
            switch (eventName)
            {
                case "SndDraw": return "tesla_start_reload";
                case "SndHolster": volume = 0.6f; return "tesla_switch_flip_off";
                case "SndMagOut": case "SndMagPullOut": case "SndMagOutAll": case "SndMagSbrosnik": return "tesla_handle_pullback";
                case "SndMagIn": return "tesla_clip_in";
                case "SndBoltOut": case "SndBoltOutSlow": case "SndBoltOutTry": return "tesla_handle_pullback";
                case "SndBoltIn": return "tesla_handle_release";
                case "SndMagHit": case "SndSelector": case "SndButton": case "SndModSwitch": volume = 0.7f; return "tesla_switch_flip_on";
                // Empty trigger: EFT's own click (the Saiga's), exported from the game's audio bundle
                case "SndDryFire": case "SndMisfire": case "SndJam": return "eft_saiga_trigger_empty";
                default: return "silence";
            }
        }

        private static SoundBank BuildBank(string name, float rolloff, AudioClip[] clips)
        {
            string path = $"{WwPaths.Generated}/{name}.asset";
            AssetDatabase.DeleteAsset(path);
            var bank = ScriptableObject.CreateInstance<SoundBank>();
            bank.name = name;
            bank.BaseVolume = 1f;
            bank.BasePitch = 1f;
            bank.Rolloff = rolloff;
            bank.HasEnvironment = 1;
            bank._clipLiength = clips.Max(c => c.length);
            // Outdoor and indoor; close, distant and far (BlendOptions left empty: EFT then always uses the close set)
            bank.Environments = Enumerable.Range(0, 2).Select(_ => new SoundBank_EnvironmentVariety
            {
                Clips = Enumerable.Range(0, 3).Select(__ => new SoundBank_DistanceVarity { Clips = clips.ToList() }).ToList(),
            }).ToList();
            AssetDatabase.CreateAsset(bank, path);
            return bank;
        }

        // ---------- Sounds ----------

        private static Dictionary<string, AudioClip> ImportSounds()
        {
            // The original's Wunderwaffe sounds, copied flat into sounds_source\ (tools\prepare_sounds.py)
            var sources = new List<string>();
            if (Directory.Exists(WwPaths.SourceSoundDir))
                sources.AddRange(Directory.GetFiles(WwPaths.SourceSoundDir, "*.wav"));
            foreach (string needed in new[] { "ww_shot", "ww_shot_last", "ww_impact", "ww_idle", "tesla_clip_in", "tesla_handle_pullback", "tesla_handle_release", "tesla_start_reload", "tesla_switch_flip_off", "tesla_switch_flip_on" })
            {
                if (!sources.Any(s => Path.GetFileNameWithoutExtension(s) == needed))
                    Debug.LogWarning($"[Wunderwaffe] sound missing: {needed}.wav in {WwPaths.SourceSoundDir}");
            }

            string silence = $"{WwPaths.Audio}/silence.wav";
            if (!File.Exists(silence))
                File.WriteAllBytes(silence, SilentWav(0.05f));

            var clips = new Dictionary<string, AudioClip>();
            foreach (string source in sources)
            {
                string asset = $"{WwPaths.Audio}/{Path.GetFileName(source)}";
                // Refreshed whenever the prepared sound changed (louder takes, 2026-11-15)
                if (!File.Exists(asset) || new FileInfo(asset).Length != new FileInfo(source).Length
                    || File.GetLastWriteTimeUtc(asset) < File.GetLastWriteTimeUtc(source))
                    File.Copy(source, asset, true);
            }

            // Replacements: a file in sounds_custom named like a sound (any format Unity imports) takes its place,
            // e.g. sounds_custom/wunderwaffe_reload.mp3 replaces wunderwaffe_reload
            var custom = new Dictionary<string, string>();
            if (Directory.Exists(WwPaths.CustomSoundDir))
            {
                foreach (string file in Directory.GetFiles(WwPaths.CustomSoundDir).Where(f => new[] { ".wav", ".mp3", ".ogg", ".aif", ".aiff" }.Contains(Path.GetExtension(f).ToLowerInvariant())))
                {
                    string asset = $"{WwPaths.Audio}/custom_{Path.GetFileName(file)}";
                    File.Copy(file, asset, true);
                    custom[Path.GetFileNameWithoutExtension(file)] = asset;
                }
            }
            AssetDatabase.Refresh();
            foreach (string asset in Directory.GetFiles(WwPaths.Audio).Where(f => !f.EndsWith(".meta") && !Path.GetFileName(f).StartsWith("custom_")))
            {
                string path = asset.Replace('\\', '/');
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null)
                    clips[Path.GetFileNameWithoutExtension(path)] = clip;
            }
            foreach (var (name, asset) in custom)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(asset);
                if (clip == null)
                    continue;
                clips[name] = clip;
                Debug.Log($"[Wunderwaffe] Custom sound: {name} ({clip.length:F2}s)");
            }
            Debug.Log($"[Wunderwaffe] {clips.Count} sounds");
            return clips;
        }

        private static byte[] SilentWav(float seconds)
        {
            const int rate = 22050;
            int samples = (int)(rate * seconds);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + samples * 2);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write(samples * 2);
                w.Write(new byte[samples * 2]);
                return ms.ToArray();
            }
        }

        // ---------- Materials ----------

        /// <summary>
        /// The Pack-a-Punched look: every material the upgraded model has its own of (body, bulbs, glow, pipe, wood)
        /// rides on a switched-off renderer (ww_upgraded_materials) in the gun as "<name>_up"; the client swaps them in
        /// on an upgraded gun, and back out.
        /// </summary>
        private static void AddUpgradedMaterials(SrcModel gun, Transform jgun)
        {
            var upgraded = new List<Material>();
            foreach (SrcMaterial m in gun.Materials)
                if (Directory.Exists(Path.Combine(WwClassic.UpgModelDir, "_images", m.Name + "_upg")))
                    upgraded.Add(BuildMaterial(m, true));
            var go = new GameObject("ww_upgraded_materials");
            go.layer = jgun.gameObject.layer;
            go.transform.SetParent(jgun, false);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = upgraded.ToArray();
            r.enabled = false;
            Debug.Log($"[Wunderwaffe] Upgraded materials: {string.Join(", ", upgraded.Select(m => m.name))}");
        }

        /// <summary>The glow: the plain gun's warm white, the Pack-a-Punched gun's red.</summary>
        public static Color PowerGlow(bool upgraded) => upgraded ? new Color(0.8f, 0.3f, 0.95f) * 2.4f : new Color(1f, 0.85f, 0.6f) * 2.4f;

        /// <summary>
        /// The bulbs and the core (the tube in the middle): bright, near white-hot as in the original (reference video,
        /// user 2026-11-16: "nothing lights up nicely"). Flat: their colour maps are dull. The client sets it per
        /// charge (WunderwaffeView: same values) and this is what the prefab and the icons show.
        /// </summary>
        public static Color CoreGlow(bool upgraded) => upgraded ? new Color(0.8f, 0.27f, 0.9f) * 3.2f : new Color(1f, 0.74f, 0.32f) * 3.2f;

        /// <summary>
        /// The core's glow map: the Ray Gun's grey plasma noise (Plasma/plasma_e.png), so its light reads cloudy; the
        /// client slides and pulses it as the Ray Gun's tube (user, 2026-11-16).
        /// </summary>
        private static Texture2D Plasma() => AssetDatabase.LoadAssetAtPath<Texture2D>($"{WwPaths.Root}/Plasma/plasma_e.png");

        /// <summary>
        /// Standard (Specular setup) from the converted per-material maps (_c colour, _n normal, _s specular with _g
        /// gloss in its alpha, _o occlusion). The power cells glow (emission) and keep their look on the PaP gun.
        /// </summary>
        public static Material BuildMaterial(SrcMaterial m, bool upgraded)
        {
            string path = $"{WwPaths.Materials}/{AssetName(m.Name)}{(upgraded ? "_up" : "")}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard (Specular setup)")) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetTexture("_EmissionMap", null);
            mat.SetColor("_EmissionColor", Color.black);
            mat.DisableKeyword("_EMISSION");

            bool upg = upgraded && Directory.Exists(Path.Combine(WwClassic.UpgModelDir, "_images", m.Name + "_upg"));
            string dir = upg ? WwClassic.UpgModelDir : WwClassic.ModelDir;
            string folderName = upg ? m.Name + "_upg" : m.Name;
            string folder = Path.Combine(dir, "_images", folderName);
            string albedo = FindBySuffix(folder, "_c.png");
            mat.SetTexture("_MainTex", albedo != null ? ImportTexture(albedo, TextureImporterType.Default, true) : null);
            mat.color = Color.white;
            string normal = FindBySuffix(folder, "_n.png");
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", ImportTexture(normal, TextureImporterType.NormalMap, false));
                mat.EnableKeyword("_NORMALMAP");
            }
            // Specular and gloss from the original's own maps (the converter only bakes flat values for these, which
            // left the gun matte and burnt-looking), the baked flat ones as a fallback
            string spec = WwClassic.OriginalImage(m.Name, "_s") ?? FindBySuffix(folder, "_s.png");
            if (spec != null)
            {
                mat.SetTexture("_SpecGlossMap", BuildSpecGloss(spec, WwClassic.OriginalImage(m.Name, "_g") ?? FindBySuffix(folder, "_g.png")));
                mat.SetFloat("_GlossMapScale", 1f);
                mat.EnableKeyword("_SPECGLOSSMAP");
            }
            string occlusion = FindBySuffix(folder, "_o.png");
            if (occlusion != null)
                mat.SetTexture("_OcclusionMap", ImportTexture(occlusion, TextureImporterType.Default, false));

            if (Glows(m.Name))
            {
                if (m.Name.EndsWith("_power"))
                {
                    // The core: a cloudy plasma glow over a dark tube; the client lights it by charge and moves it
                    mat.SetTexture("_MainTex", null);
                    mat.color = new Color(0.12f, 0.1f, 0.09f);
                    SetEmission(mat, Plasma(), CoreGlow(upgraded));
                    mat.SetTextureScale("_MainTex", Vector2.one);
                    mat.SetTextureOffset("_MainTex", Vector2.zero);
                }
                else if (m.Name.Contains("_bulb"))
                {
                    // Bulbs: a flat bright glow (their colour maps are dull); the client lights them by charge
                    mat.color = new Color(0.95f, 0.9f, 0.8f);
                    SetEmission(mat, null, CoreGlow(upgraded));
                }
                else
                {
                    SetEmission(mat, mat.GetTexture("_MainTex") as Texture2D, PowerGlow(upgraded) * 1.6f);
                }
                KeepLook(mat);
            }
            else if (m.Name.EndsWith("_glass"))
            {
                mat.color = new Color(0.8f, 0.85f, 0.9f, 1f);
                MakeTransparent(mat, 0.3f);
            }
            else
            {
                mat.renderQueue = -1; // the shader's own
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>First synthetic material index: the bare-metal overlay copies of the body materials, by index.</summary>
        private const int MetalMaterialBase = 1000;

        /// <summary>
        /// Pack-a-Punch paints whole materials, but the Wunderwaffe's materials mix painted panels with bare metal. The
        /// original's own camo reveal map (_r: white where its camo goes) marks the metal: every face that touches
        /// metal is drawn a second time, over the painted one, with a copy of its material whose colour map is cut out
        /// to the metal only (alpha = 1 - reveal). The cut-out copy sits in the alpha-test queue, which PaP leaves alone,
        /// so on a Pack-a-Punched gun the metal shows through the camo pixel for pixel; on a plain gun the two match.
        /// Returns the reveal maps (readable), by the model's material index, for the face test.
        /// </summary>
        private static Dictionary<int, Texture2D> BuildMetalMaterials(SrcModel gun, Dictionary<int, Material> materials)
        {
            var reveals = new Dictionary<int, Texture2D>();
            for (int m = 0; m < gun.Materials.Count; m++)
            {
                string revealPath = WwClassic.OriginalImage(gun.Materials[m].Name, "_r");
                string albedoPath = FindBySuffix(Path.Combine(WwClassic.ModelDir, "_images", gun.Materials[m].Name), "_c.png");
                if (revealPath == null || albedoPath == null || !materials.TryGetValue(m, out Material body))
                    continue;
                var reveal = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                reveal.LoadImage(File.ReadAllBytes(revealPath));
                reveals[m] = reveal;

                string texPath = $"{WwPaths.Textures}/{body.name}_metal_c.png";
                if (!File.Exists(texPath) || File.GetLastWriteTimeUtc(texPath) < File.GetLastWriteTimeUtc(albedoPath))
                {
                    var albedo = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                    albedo.LoadImage(File.ReadAllBytes(albedoPath));
                    int w = albedo.width, h = albedo.height;
                    Color[] pixels = albedo.GetPixels();
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            pixels[y * w + x].a = 1f - reveal.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h).r;
                    albedo.SetPixels(pixels);
                    File.WriteAllBytes(texPath, albedo.EncodeToPNG());
                    Object.DestroyImmediate(albedo);
                    AssetDatabase.ImportAsset(texPath);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(texPath);
                    importer.sRGBTexture = true;
                    importer.alphaIsTransparency = false;
                    importer.maxTextureSize = 4096;
                    importer.SaveAndReimport();
                }
                string matPath = $"{WwPaths.Materials}/{body.name}_metal.mat";
                AssetDatabase.DeleteAsset(matPath);
                var metal = new Material(body) { name = body.name + "_metal" };
                metal.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
                MakeCutout(metal, 0.5f);
                AssetDatabase.CreateAsset(metal, matPath);
                materials[MetalMaterialBase + m] = metal;
            }
            return reveals;
        }

        /// <summary>A face touching bare metal: any corner or its middle on black in the reveal map.</summary>
        private static bool TouchesMetal(SrcFace f, Texture2D reveal)
        {
            float u = 0f, v = 0f;
            foreach (SrcCorner c in f.Corners)
            {
                if (reveal.GetPixelBilinear(c.U, 1f - c.V).r < 0.5f)
                    return true;
                u += c.U;
                v += c.V;
            }
            return reveal.GetPixelBilinear(u / 3f, 1f - v / 3f).r < 0.5f;
        }

        /// <summary>
        /// Out of Pack-a-Punch's reach (see <see cref="KeepLookQueue"/>). Unity puts an opaque Standard material back
        /// in the shader's own queue whenever it validates it, so a custom queue alone never reached the bundle; a
        /// cutout that clips nothing keeps the alpha-test queue and draws exactly like the opaque one.
        /// </summary>
        private static void KeepLook(Material mat) => MakeCutout(mat, 0f);

        private static void MakeCutout(Material mat, float cutoff)
        {
            mat.SetFloat("_Mode", 1f);
            mat.SetFloat("_Cutoff", cutoff);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        private static string FindBySuffix(string dir, string suffix) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + suffix).FirstOrDefault(f => !Path.GetFileName(f).StartsWith("$")) : null;

        private static void SetEmission(Material mat, Texture2D map, Color color)
        {
            mat.SetTexture("_EmissionMap", map);
            mat.SetColor("_EmissionColor", color);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.EnableKeyword("_EMISSION");
        }

        private static void MakeTransparent(Material mat, float alpha)
        {
            mat.SetFloat("_Mode", 2f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = 3000;
            Color c = mat.color;
            c.a = alpha;
            mat.color = c;
        }

        /// <summary>Prefers the port's full-resolution "_-g" maps over the "~-g" previews.</summary>
        private static string Pick(string dir, string suffix)
        {
            if (!Directory.Exists(dir))
                return null;
            string[] files = Directory.GetFiles(dir, "*" + suffix).Where(f => !Path.GetFileName(f).StartsWith("$")).ToArray();
            return files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("_-")) ?? files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }

        private static string Largest(string dir, string suffix) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + suffix).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault() : null;

        private static string FindContaining(string dir, string part) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").FirstOrDefault(f => Path.GetFileName(f).Contains(part)) : null;

        private static Texture2D ImportTexture(string source, TextureImporterType type, bool srgb)
        {
            if (!File.Exists(source))
                return null;
            string assetPath = $"{WwPaths.Textures}/{Path.GetFileName(source).Replace("&", "_").Replace("$", "_").Replace("~", "_")}";
            if (!File.Exists(assetPath))
            {
                File.Copy(source, assetPath);
                AssetDatabase.ImportAsset(assetPath);
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            if (importer.textureType != type || importer.sRGBTexture != srgb || importer.maxTextureSize != 4096)
            {
                importer.textureType = type;
                importer.sRGBTexture = srgb;
                importer.maxTextureSize = 4096; // the port's full-resolution maps: 2K read pixelated in first person
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        private const int EmissiveSize = 1024;

        /// <summary>
        /// The original's glow maps are small (the grille sections' is 128x128) and compressed they read as a grid of pixels up
        /// close. Imported as a smooth copy instead: upscaled bilinearly to <see cref="EmissiveSize"/>, blurred by
        /// about one source pixel so no texel edges remain, and stored uncompressed with trilinear filtering.
        /// </summary>
        private static Texture2D ImportEmissive(string source)
        {
            if (source == null || !File.Exists(source))
                return null;
            string assetPath = $"{WwPaths.Textures}/{Path.GetFileNameWithoutExtension(source).Replace("&", "_").Replace("$", "_").Replace("~", "_")}_smooth.png";
            if (!File.Exists(assetPath))
            {
                var src = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                src.LoadImage(File.ReadAllBytes(source));
                int size = Mathf.Max(EmissiveSize, Mathf.Max(src.width, src.height));
                int radius = Mathf.Max(1, size / Mathf.Max(src.width, src.height) / 2);
                var up = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        up[y * size + x] = src.GetPixelBilinear((x + 0.5f) / size, (y + 0.5f) / size);
                Color[] smooth = BoxBlur(BoxBlur(up, size, radius, true), size, radius, false);
                var dst = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                dst.SetPixels(smooth);
                File.WriteAllBytes(assetPath, dst.EncodeToPNG());
                AssetDatabase.ImportAsset(assetPath);
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            if (importer.textureCompression != TextureImporterCompression.Uncompressed || importer.filterMode != FilterMode.Trilinear)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.maxTextureSize = 4096;
                importer.mipmapEnabled = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 4;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>One pass of a box blur, across rows or down columns (edges clamp).</summary>
        private static Color[] BoxBlur(Color[] pixels, int size, int radius, bool horizontal)
        {
            var result = new Color[pixels.Length];
            float weight = 1f / (radius * 2 + 1);
            for (int a = 0; a < size; a++)
            {
                for (int b = 0; b < size; b++)
                {
                    Color sum = Color.clear;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int c = Mathf.Clamp(b + k, 0, size - 1);
                        sum += horizontal ? pixels[a * size + c] : pixels[c * size + a];
                    }
                    result[horizontal ? a * size + b : b * size + a] = sum * weight;
                }
            }
            return result;
        }

        /// <summary>Standard (Specular setup) reads smoothness from the specular map's alpha, so pack _g into _s.a.</summary>
        private static Texture2D BuildSpecGloss(string specSource, string glossSource)
        {
            string assetPath = $"{WwPaths.Textures}/{Path.GetFileNameWithoutExtension(specSource).Replace("&", "_").Replace("$", "_").Replace("~", "_")}_sg.png";
            if (!File.Exists(assetPath))
            {
                var spec = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                spec.LoadImage(File.ReadAllBytes(specSource));
                Texture2D gloss = null;
                if (glossSource != null)
                {
                    gloss = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    gloss.LoadImage(File.ReadAllBytes(glossSource));
                }
                int w = spec.width, h = spec.height;
                Color[] pixels = spec.GetPixels();
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        pixels[i].a = gloss != null ? gloss.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h).r : 0.4f;
                    }
                }
                spec.SetPixels(pixels);
                File.WriteAllBytes(assetPath, spec.EncodeToPNG());
                Object.DestroyImmediate(spec);
                if (gloss != null)
                    Object.DestroyImmediate(gloss);
                AssetDatabase.ImportAsset(assetPath);
                var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                importer.maxTextureSize = 4096;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // ---------- Copying the Saiga's component values ----------

        /// <summary>The dumped fields of the Saiga component of this class whose owner path contains <paramref name="ownerPart"/>.</summary>
        private static object ComponentFields(string cls, string ownerPart, string file = "saiga_components.json")
        {
            object all = MiniJson.Parse(File.ReadAllText(WwPaths.Reference(file)));
            object entry = all.Arr().FirstOrDefault(c => c.Str("class") == cls && c.Str("owner").Contains(ownerPart));
            if (entry == null)
                throw new InvalidOperationException($"No {cls} on {ownerPart} in {file}");
            return entry.Get("fields");
        }

        /// <summary>Saiga rig paths are "Transform:weapon_..._10_12g.generated/a/b"; ours are the same below the root.</summary>
        private static Object ResolveInRig(string reference, Transform root)
        {
            if (reference == null || !reference.StartsWith("Transform:"))
                return null;
            string path = reference.Substring("Transform:".Length);
            int slash = path.IndexOf('/');
            if (slash < 0)
                return path.EndsWith(".generated") ? root : null;
            return root.Find(path.Substring(slash + 1));
        }

        /// <summary>
        /// Fills a stub's serialized fields from a dumped component: values by name, nested classes and lists
        /// recursively, object references through <paramref name="resolve"/> (unknown ones become null).
        /// </summary>
        public static void CopyFields(object target, object json, Func<string, Object> resolve)
        {
            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = json.Get(field.Name);
                if (value == null && !(json is Dictionary<string, object> d && d.ContainsKey(field.Name)))
                    continue;
                field.SetValue(target, Convert(value, field.FieldType, resolve));
            }
        }

        private static object Convert(object value, Type type, Func<string, Object> resolve)
        {
            if (typeof(Object).IsAssignableFrom(type))
            {
                Object o = value is Dictionary<string, object> r ? resolve(r.Str("ref")) : null;
                return o != null && type.IsInstanceOfType(o) ? o : null;
            }
            if (value == null)
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(string)) return value as string ?? "";
            if (type == typeof(float)) return (float)(double)value;
            if (type == typeof(double)) return (double)value;
            if (type == typeof(int)) return (int)(long)(double)value;
            if (type == typeof(uint)) return (uint)(long)(double)value;
            if (type == typeof(long)) return (long)(double)value;
            if (type == typeof(short)) return (short)(double)value;
            if (type == typeof(ushort)) return (ushort)(double)value;
            if (type == typeof(byte)) return value is bool b ? (byte)(b ? 1 : 0) : (byte)(double)value;
            if (type == typeof(sbyte)) return (sbyte)(double)value;
            if (type == typeof(bool)) return value is bool bb ? bb : (double)value != 0;
            if (type == typeof(Vector2)) return new Vector2(value.Num("x"), value.Num("y"));
            if (type == typeof(Vector3)) return new Vector3(value.Num("x"), value.Num("y"), value.Num("z"));
            if (type == typeof(Vector4)) return new Vector4(value.Num("x"), value.Num("y"), value.Num("z"), value.Num("w"));
            if (type == typeof(Quaternion)) return new Quaternion(value.Num("x"), value.Num("y"), value.Num("z"), value.Num("w"));
            if (type == typeof(Color)) return new Color(value.Num("r"), value.Num("g"), value.Num("b"), value.Num("a"));
            if (type == typeof(LayerMask)) return (LayerMask)value.Int("m_Bits");
            if (type == typeof(AnimationCurve))
            {
                var keys = (value.Get("m_Curve") as List<object> ?? new List<object>())
                    .Select(k => new Keyframe(k.Num("time"), k.Num("value"), k.Num("inSlope"), k.Num("outSlope"), k.Num("inWeight"), k.Num("outWeight"))).ToArray();
                return new AnimationCurve(keys);
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type element = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                if (value is List<object> items)
                    foreach (object item in items)
                        list.Add(Convert(item, element, resolve));
                return list;
            }
            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                object instance = Activator.CreateInstance(type);
                if (value is Dictionary<string, object>)
                    CopyFields(instance, value, resolve);
                return instance;
            }
            return null;
        }
    }
}
