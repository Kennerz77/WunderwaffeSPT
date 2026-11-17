using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Wunderwaffe.Editor
{
    /// <summary>
    /// Inventory pictures, rendered here instead of by the game's icon studio (which washes these models out white):
    /// the Wunderwaffe, the Pack-a-Punched one in PaP's own camo (default and Origins looks) and the battery clip. Written
    /// as transparent PNGs to icons/ in the project root; the client plugin ships them and swaps them into the cells.
    /// </summary>
    public static class WwIcons
    {
        public static string IconsDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../icons"));

        // PaP's camo albedo (read only, never changed) and the tiling PaP paints it with
        // The DG-3 JZ's own camo (the original's engraved etching, as the client registers it with PaP)
        private const string CamoAlbedo = @"C:\Git\3_Working\ZM_Wunderwaffe\camo\ww_pap_etching_c.png";
        private const float EtchingTiling = 3f; // 1.2 x the material's 2.5, as in game (half the size: user, 2026-11-16)
        private const float CamoTiling = 2.5f; // as the client asks PaP for (WunderwaffePaP.CamoTiling)
        private const float CamoEmission = 0.06f;
        private const string OriginsCrystalPath = @"C:\Git\3_Working\PaPHideoutModule\UnityProject\Assets\PaP\Camo\origins_crystal_c.png";
        private static Texture2D _crystal;

        private const int Width = 1024;

        public static void RenderAll()
        {
            Directory.CreateDirectory(IconsDir);
            var rig = new List<Object>();
            try
            {
                rig.AddRange(Lights());
                RenderGun("wunderwaffe", camo: null, upgraded: false);
                Texture2D camo = LoadTexture(CamoAlbedo);
                if (camo != null)
                {
                    rig.Add(camo);
                    RenderGun("wunderwaffe_pap", camo, upgraded: true);
                    _crystal = LoadTexture(OriginsCrystalPath);
                    if (_crystal != null)
                    {
                        rig.Add(_crystal);
                        RenderGun("wunderwaffe_pap_origins", camo, upgraded: true, origins: true);
                    }
                }
                else
                {
                    Debug.LogWarning($"[Wunderwaffe] PaP camo not found at {CamoAlbedo}; no Pack-a-Punched icon");
                }
                for (int lit = 0; lit <= 3; lit++)
                    RenderClip($"clip_{lit}", lit);
                RenderCharge("charge");
            }
            finally
            {
                foreach (Object o in rig)
                    Object.DestroyImmediate(o);
            }
        }

        private static IEnumerable<Object> Lights()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.45f);
            foreach (var (rotation, intensity) in new[] { (Quaternion.Euler(30f, 60f, 0f), 1.0f), (Quaternion.Euler(-20f, -120f, 0f), 0.45f) })
            {
                var go = new GameObject("icon_light");
                Light light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = intensity;
                go.transform.rotation = rotation;
                yield return go;
            }
        }

        private static Texture2D LoadNormal(string path)
        {
            if (!File.Exists(path))
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat };
            tex.LoadImage(File.ReadAllBytes(path));
            return tex;
        }

        private static Texture2D LoadTexture(string path)
        {
            if (!File.Exists(path))
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            tex.LoadImage(File.ReadAllBytes(path));
            return tex;
        }

        /// <summary>
        /// The gun from its left side, muzzle to the left as EFT pictures its guns. Laid out in the original game space
        /// through j_gun: the barrel (+X) along +Z, up (+Z) along +Y, its left (+Y) along -X.
        /// </summary>
        private static void RenderGun(string name, Texture2D camo, bool upgraded, bool origins = false)
        {
            GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(WwImporter.WeaponPrefabPath));
            try
            {
                Transform jgun = go.GetComponentsInChildren<Transform>(true).First(t => t.name == "j_gun");
                jgun.SetParent(null, true);
                jgun.position = Vector3.zero;
                jgun.rotation = WwSpace.Rot(WwClassic.LoadModel().Bones[0].Rest);
                Object.DestroyImmediate(go);
                go = jgun.gameObject;
                // The clip (off behind the gun at rest) and the materials holder stay out of the picture and its framing
                Transform clip = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "tag_clip");
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                    if (r.name == "ww_upgraded_materials" || (clip != null && r.transform.IsChildOf(clip) && r.transform != clip))
                        Object.DestroyImmediate(r.gameObject);
                Dress(go, camo, upgraded, origins);
                Shoot(go, name, forward: Vector3.right, up: Vector3.up);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>The battery clip from its side: looking along its thinnest axis, its longest across the picture.</summary>
        private static void RenderClip(string name, int lit)
        {
            GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(WwImporter.MagPrefabPath));
            try
            {
                Dress(go, null, false, false);
                // The battery's bulbs lit by its charge: the spent ones dark
                for (int b = lit + 1; b <= 3; b++)
                {
                    Transform bulb = go.transform.Find($"bulb{b}");
                    if (bulb == null)
                        continue;
                    foreach (Material m in bulb.GetComponent<Renderer>().sharedMaterials)
                    {
                        if (m == null)
                            continue;
                        m.SetColor("_EmissionColor", Color.black);
                        m.color = new Color(0.45f, 0.42f, 0.38f);
                    }
                }
                MeshFilter mf = go.transform.GetChild(0).GetComponent<MeshFilter>() ?? go.GetComponentInChildren<MeshFilter>();
                Vector3 size = mf.sharedMesh.bounds.size;
                Vector3 thin = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
                Vector3 longest = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
                Vector3 forward = mf.transform.TransformDirection(thin);
                Vector3 up = Vector3.Cross(forward, mf.transform.TransformDirection(longest));
                Shoot(go, name, forward, up);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>A charge: one lit bulb (it showed the copper slug it's cloned from, a shotgun shell).</summary>
        private static void RenderCharge(string name)
        {
            GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(WwImporter.MagPrefabPath));
            try
            {
                Transform bulb = go.transform.Find("bulb1");
                if (bulb == null)
                {
                    Debug.LogWarning("[Wunderwaffe] No bulb on the battery: no charge icon");
                    return;
                }
                bulb.SetParent(null, true);
                Object.DestroyImmediate(go);
                go = bulb.gameObject;
                Dress(go, null, false, false);
                Shoot(go, name, forward: bulb.TransformDirection(Vector3.forward), up: bulb.TransformDirection(Vector3.up));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Copies of the model's materials: the power cells green or red, the rest in camo where PaP would paint it.</summary>
        private static void Dress(GameObject go, Texture2D camo, bool upgraded, bool origins)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterials = r.sharedMaterials.Select(m =>
                {
                    if (m == null)
                        return null;
                    var copy = new Material(m);
                    if (m.name.StartsWith(WwImporter.PowerMaterial))
                    {
                        copy.SetColor("_EmissionColor", WwImporter.PowerGlow(upgraded));
                        return copy;
                    }
                    // The bulbs and the core lit (user, 2026-11-16): a warm filament glow (purple-pink Pack-a-Punched),
                    // kept under full white, which the picture can't show past (their in-game glow blew out to white here)
                    if (m.name.Contains("_bulb") || m.name.StartsWith("ww_body_power"))
                    {
                        Color tint = upgraded ? new Color(0.81f, 0.27f, 0.88f) : new Color(1f, 0.74f, 0.32f);
                        copy.color = Color.Lerp(Color.white, tint, 0.55f);
                        copy.SetColor("_EmissionColor", tint * (m.name.Contains("_bulb") ? 0.95f : 0.8f));
                        copy.EnableKeyword("_EMISSION");
                        return copy;
                    }
                    // As PaPCamo.ShouldPaint: nothing from the alpha-test queue up, nothing without a colour map
                    Texture2D skin = origins ? _crystal : camo;
                    // The DG-3 JZ's own camo only on the body plates (as in game: WunderwaffePaPBridge.OwnCamo)
                    if (!origins && !m.name.StartsWith("ww_body_body"))
                        return copy;
                    if (camo != null && skin != null && m.renderQueue < 2450 && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null)
                    {
                        copy.SetTexture("_MainTex", skin);
                        copy.SetTextureScale("_MainTex", Vector2.one * (origins ? CamoTiling : EtchingTiling));
                        copy.color = Color.white;
                        // The etching as metal: its gold spec and gloss, and its engraving's relief
                        if (!origins)
                        {
                            Texture2D sg = LoadTexture(@"C:\Git\3_Working\ZM_Wunderwaffe\camo\ww_pap_etching_sg.png");
                            Texture2D nm = LoadNormal(@"C:\Git\3_Working\ZM_Wunderwaffe\camo\ww_pap_etching_n.png");
                            if (sg != null && copy.HasProperty("_SpecGlossMap"))
                            {
                                copy.SetTexture("_SpecGlossMap", sg);
                                copy.EnableKeyword("_SPECGLOSSMAP");
                                copy.SetFloat("_GlossMapScale", 1f);
                            }
                            if (nm != null && copy.HasProperty("_BumpMap"))
                            {
                                copy.SetTexture("_BumpMap", nm);
                                copy.SetFloat("_BumpScale", 1f);
                                copy.EnableKeyword("_NORMALMAP");
                            }
                        }
                        copy.SetTexture("_EmissionMap", skin);
                        copy.SetTextureScale("_EmissionMap", Vector2.one * (origins ? CamoTiling : EtchingTiling));
                        copy.SetColor("_EmissionColor", new Color(CamoEmission, CamoEmission, CamoEmission));
                        copy.EnableKeyword("_EMISSION");
                    }
                    return copy;
                }).ToArray();
            }
        }

        /// <summary>An orthographic shot framing the model tightly, transparent around it.</summary>
        private static void Shoot(GameObject go, string name, Vector3 forward, Vector3 up)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
            Quaternion view = Quaternion.LookRotation(forward, up);
            Quaternion inv = Quaternion.Inverse(view);
            // Bounds in the camera's frame: corners of every renderer's world bounds
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (Renderer r in renderers)
            {
                Bounds wb = r.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = wb.center + Vector3.Scale(wb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    Vector3 p = inv * corner;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            Vector3 centre = view * ((min + max) / 2f);
            float w = max.x - min.x, h = max.y - min.y;
            int width = Width, height = Mathf.Max(64, Mathf.RoundToInt(Width * h / w));

            var camGo = new GameObject("icon_cam");
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = h / 2f * 1.04f;
                cam.aspect = (float)width / height;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.transform.rotation = view;
                cam.transform.position = centre - forward.normalized * ((max.z - min.z) + 1f);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = (max.z - min.z) * 2f + 4f;
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(IconsDir, $"{name}.png"), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Debug.Log($"[Wunderwaffe] Icon {name}.png: {width}x{height}");
            }
            finally
            {
                RenderTexture.active = null;
                Object.DestroyImmediate(camGo);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
