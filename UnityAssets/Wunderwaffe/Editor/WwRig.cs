using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wunderwaffe.ModelFormats;
using UnityEngine;

namespace Wunderwaffe.Editor
{
    /// <summary>Paths and conversions shared by the importer, the retarget and the controller builder.</summary>
    public static class WwPaths
    {
        // Only the arms rig comes from here: the original's arms-rig port (am_t5_thundergun: the original game's standard
        // viewmodel arm bones, the hierarchy above the arms that the SEAnims leave out)
        public const string PortRoot = @"C:\Git\2_References\t5_thundergun 2\t5_thundergun";
        public const string ArmsRig = "am_t5_thundergun.xmodel_bin";
        public static readonly string ModelDir = Directory.GetFiles(PortRoot + @"\model_export", ArmsRig,
            SearchOption.AllDirectories).Select(f => Path.GetDirectoryName(f)).FirstOrDefault();
        // The original's Wunderwaffe sounds (GrayWolf export), copied into the repo's sounds_source\ by tools\copy_sounds.ps1
        public static string SourceSoundDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../sounds_source"));

        public const string Root = "Assets/Wunderwaffe";
        public const string Generated = Root + "/Generated";
        public const string Textures = Root + "/Textures";
        public const string Materials = Root + "/Materials";
        public const string Audio = Root + "/Audio";
        public const string Clips = Root + "/Clips";

        public const string WeaponName = "weapon_dg2"; // never "raygun": the Ray Gun mod matches weapon_raygun* roots
        public const string ContainerName = "weapon_dg2_container";
        public const string MagName = "mag_dg2_clip";

        /// <summary>Sound replacements, named after the sound they replace (see WwImporter.ImportSounds).</summary>
        public static string CustomSoundDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../sounds_custom"));

        public static string ReferenceDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../reference"));
        public static string LogsDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../logs"));
        public static string Reference(string file) => Path.Combine(ReferenceDir, file);

        public static void EnsureDirs()
        {
            foreach (string dir in new[] { Generated, Textures, Materials, Audio, Clips })
                Directory.CreateDirectory(dir);
            Directory.CreateDirectory(LogsDir);
        }
    }

    /// <summary>
    /// The original game space (right-handed, Z up, X forward, Y left, inches) to Unity prefab space (left-handed, Y up, Z forward,
    /// metres). The mapping mirrors one axis; a bone's rotation is built from its mapped Y and Z axes so its X axis
    /// still maps to the mapped X (the proof: for a mirror M, Ma × Mb = −M(a × b)).
    /// </summary>
    public static class WwSpace
    {
        public const float Scale = 0.0254f;

        public static Vector3 Dir(SrcVec v) => new Vector3(-v.Y, v.Z, v.X);
        public static Vector3 Pos(SrcVec v) => Dir(v) * Scale;
        public static Quaternion Rot(SrcPose p) => Quaternion.LookRotation(-Dir(p.AxisZ), Dir(p.AxisY));

        /// <summary>The swing-free part of <paramref name="q"/> about <paramref name="axis"/> (unit).</summary>
        public static Quaternion Twist(Quaternion q, Vector3 axis)
        {
            Vector3 v = new Vector3(q.x, q.y, q.z);
            Vector3 p = Vector3.Dot(v, axis) * axis;
            var twist = new Quaternion(p.x, p.y, p.z, q.w);
            float n = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            return n < 1e-6f ? Quaternion.identity : new Quaternion(twist.x / n, twist.y / n, twist.z / n, twist.w / n);
        }

        /// <summary>Unity's path hash for avatar masks: CRC32 of the transform path relative to the Animator.</summary>
        public static uint Crc32(string text)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text))
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }
    }

    /// <summary>
    /// Rebuilds the EFT weapon rig (arms, camera, weapon root, markers and slots) from the vanilla Saiga-12K's
    /// prefab, dumped to reference/saiga_rig.json. The bones and their rest pose are standard across EFT weapons
    /// (custom guns use the same lengths), so our clips can drive the player's arms like any vanilla gun's.
    /// The Saiga's own meshes and effects are left out.
    /// </summary>
    public static class WwRig
    {
        public static GameObject Build(string name, Dictionary<string, Transform> bones)
        {
            object rig = MiniJson.Parse(File.ReadAllText(WwPaths.Reference("saiga_rig.json")));
            var root = new GameObject(name);
            foreach (object child in rig.Get("children").Arr())
                Add(child, root.transform, bones);
            return root;
        }

        private static void Add(object node, Transform parent, Dictionary<string, Transform> bones)
        {
            string name = node.Str("name");
            List<object> comps = node.Get("comps").Arr();
            // Saiga geometry, muzzle effect clones and its stock prop aren't part of the rig
            if (comps.Any(c => (string)c == "MeshFilter") || name.EndsWith("(Clone)") || name == "prop")
                return;

            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = V3(node.Get("pos"));
            t.localRotation = Q(node.Get("rot"));
            t.localScale = V3(node.Get("scale"));
            t.gameObject.SetActive(node.Bool("active"));
            t.gameObject.layer = node.Int("layer");
            bones[name] = t;
            foreach (object child in node.Get("children").Arr())
                Add(child, t, bones);
        }

        public static Vector3 V3(object a)
        {
            List<object> l = a.Arr();
            return new Vector3((float)(double)l[0], (float)(double)l[1], (float)(double)l[2]);
        }

        public static Quaternion Q(object a)
        {
            List<object> l = a.Arr();
            return new Quaternion((float)(double)l[0], (float)(double)l[1], (float)(double)l[2], (float)(double)l[3]);
        }

        public static string PathOf(Transform t, Transform root)
        {
            var names = new List<string>();
            for (Transform x = t; x != null && x != root; x = x.parent)
                names.Add(x.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
