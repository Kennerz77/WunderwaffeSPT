using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Wunderwaffe.Editor
{
    /// <summary>
    /// The Wunderwaffe's bolt and impact effects, built as prefabs for ww_fx.bundle (loaded by the client from its
    /// plugin folder). The bolt: a hot glowing ball at the front with a sharp, bright green line trailing it, as in the
    /// original. The impact: a quick green burst with sparks and a flash, with one of the original's three impact sounds
    /// (ww_impact_0..2). Green for the plain gun, red for the Pack-a-Punched one ("_up").
    /// Particles use Unity's legacy particle shaders (proven in EFT by the Ray Gun).
    /// </summary>
    public static class WwFx
    {
        public const string BundleName = "ww_fx.bundle";
        public const int ImpactSounds = 1;

        public static IEnumerable<string> Prefabs
        {
            get
            {
                yield return "ww_burn";
                foreach (string tag in new[] { "", "_up" })
                {
                    yield return "ww_chain" + tag;
                    yield return "ww_fog" + tag;
                    yield return "ww_arc" + tag;
                    for (int i = 0; i < ImpactSounds; i++)
                        yield return $"ww_impact{tag}_{i}";
                }
            }
        }

        /// <summary>
        /// A shocked body going down burnt (user, 2026-11-16): dark smoke curling up off it, swelling as it rises, and a
        /// few orange embers. Loops; the client parents it to the body's bones and stops it after a few seconds.
        /// </summary>
        private static void BuildBurn(string name, Material smokeMat, Material emberMat)
        {
            var root = new GameObject(name);
            try
            {
                ParticleSystem smoke = Particles(root, "smoke", smokeMat, local: false);
                var main = smoke.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.2f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.22f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.16f, 0.15f, 0.14f, 0.5f), new Color(0.3f, 0.29f, 0.27f, 0.4f));
                main.gravityModifier = -0.06f;
                main.maxParticles = 150;
                var emission = smoke.emission;
                emission.rateOverTime = 20f;
                var shape = smoke.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.12f;
                var drift = smoke.velocityOverLifetime;
                drift.enabled = true;
                drift.space = ParticleSystemSimulationSpace.World;
                drift.x = new ParticleSystem.MinMaxCurve(-0.08f, 0.08f);
                drift.y = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
                drift.z = new ParticleSystem.MinMaxCurve(-0.08f, 0.08f);
                var spin = smoke.rotationOverLifetime;
                spin.enabled = true;
                spin.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);
                Grow(smoke, 4f);
                Fade(smoke);

                ParticleSystem embers = Particles(root, "embers", emberMat, local: false);
                main = embers.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.2f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.7f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.025f);
                main.startColor = new Color(1f, 0.55f, 0.2f, 1f);
                main.gravityModifier = -0.15f;
                main.maxParticles = 40;
                emission = embers.emission;
                emission.rateOverTime = 9f;
                shape = embers.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.1f;
                Fade(embers);
                Save(root, name);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        public static string PrefabPath(string name) => $"{WwPaths.Root}/Fx/{name}.prefab";

        // The original's lightning: white-blue; the Pack-a-Punched gun's red
        // Blue lightning (user, 2026-11-16: the idle lightning read yellow); purple-pink on the Pack-a-Punched gun
        private static readonly Color Green = new Color(0.3f, 0.55f, 1f);
        private static readonly Color Red = new Color(0.8f, 0.3f, 1f);

        public static void BuildAll(Dictionary<string, AudioClip> sounds)
        {
            Directory.CreateDirectory($"{WwPaths.Root}/Fx");
            Texture2D glow = SaveTexture("ww_fx_glow", 64, GlowPixel);
            Texture2D puff = SaveTexture("ww_fx_puff", 128, PuffPixel);
            Texture2D streak = SaveTexture("ww_fx_streak", 64, StreakPixel);
            foreach (bool up in new[] { false, true })
            {
                Color c = up ? Red : Green;
                string tag = up ? "_up" : "";
                Material glowMat = SaveMaterial($"ww_fx_glow{tag}", "Legacy Shaders/Particles/Additive", glow, c);
                Material smokeMat = SaveMaterial($"ww_fx_smoke{tag}", "Legacy Shaders/Particles/Alpha Blended", puff, Color.white);
                Material lineMat = SaveMaterial($"ww_fx_line{tag}", "Legacy Shaders/Particles/Additive", streak, c);
                BuildChain("ww_chain" + tag, c, lineMat);
                BuildFog("ww_fog" + tag, c, smokeMat);
                sounds.TryGetValue("ww_flux_l", out AudioClip flux);
                if (flux == null)
                    Debug.LogWarning("[Wunderwaffe] No flux sound ww_flux_l");
                BuildArc("ww_arc" + tag, c, lineMat, flux);
                for (int i = 0; i < ImpactSounds; i++)
                {
                    sounds.TryGetValue("ww_impact", out AudioClip boom);
                    if (boom == null)
                        Debug.LogWarning($"[Wunderwaffe] No impact sound ww_impact_0{i}");
                    BuildImpact($"ww_impact{tag}_{i}", c, glowMat, smokeMat, boom);
                }
            }
            BuildBurn("ww_burn", SaveMaterial("ww_fx_burnsmoke", "Legacy Shaders/Particles/Alpha Blended", puff, Color.white),
                SaveMaterial("ww_fx_ember", "Legacy Shaders/Particles/Additive", glow, new Color(1f, 0.45f, 0.12f)));
            AssetDatabase.SaveAssets();
            Debug.Log("[Wunderwaffe] FX prefabs built");
        }

        // ---------- The bolt ----------

        private static void BuildBolt(string name, Color c, Material glowMat, Material smokeMat, Material lineMat)
        {
            var root = new GameObject(name);
            try
            {
                // The ball at the front: a few bright sprites held on the bolt, a hot white middle in a green glow
                ParticleSystem core = Particles(root, "core", glowMat, local: true);
                var main = core.main;
                main.loop = true;
                main.startLifetime = 0.05f;
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.11f, 0.15f);
                main.startColor = c * 2.4f;
                main.maxParticles = 20;
                var emission = core.emission;
                emission.rateOverTime = 140f;

                ParticleSystem hot = Particles(root, "hot", glowMat, local: true);
                main = hot.main;
                main.loop = true;
                main.startLifetime = 0.05f;
                main.startSpeed = 0f;
                main.startSize = 0.05f;
                main.startColor = Color.Lerp(c, Color.white, 0.7f) * 2f;
                main.maxParticles = 10;
                emission = hot.emission;
                emission.rateOverTime = 120f;

                // The trail, as the Ray Gun's (without its rings): a thin, see-through green smoke left hanging in the
                // world all the way back to the gun, swelling and fading, with green sparks inside it
                ParticleSystem trail = Particles(root, "trail", smokeMat, local: false);
                main = trail.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.7f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.15f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.025f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new Color(c.r, c.g, c.b, 0.18f);
                main.maxParticles = 400;
                emission = trail.emission;
                emission.rateOverTime = 0f;
                emission.rateOverDistance = 30f;
                Grow(trail, 1.5f);
                Fade(trail);

                ParticleSystem sparks = Particles(root, "sparks", glowMat, local: false);
                main = sparks.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.3f);
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.12f);
                main.startColor = c * 1.5f;
                main.maxParticles = 300;
                emission = sparks.emission;
                emission.rateOverTime = 0f;
                emission.rateOverDistance = 25f;
                Fade(sparks);

                // The line: a bright streak chasing the ball
                var line = root.AddComponent<TrailRenderer>();
                line.sharedMaterial = lineMat;
                // A solid, thin, bright line (user, 2026-11-15: too thick and fading)
                line.time = 0.2f;
                line.minVertexDistance = 0.08f;
                line.widthCurve = new AnimationCurve(new Keyframe(0f, 0.012f), new Keyframe(0.9f, 0.012f), new Keyframe(1f, 0.004f));
                line.colorGradient = Solid(c * 2.4f);
                line.textureMode = LineTextureMode.Stretch;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.alignment = LineAlignment.View;
                line.emitting = true;

                Light light = new GameObject("light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false);
                light.type = LightType.Point;
                light.color = c;
                light.range = 3f;
                light.intensity = 2f;
                light.shadows = LightShadows.None;

                Save(root, name);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- The shot: a bolt of lightning, gun to target and on from one target to the next ----------

        /// <summary>
        /// One jagged lightning bolt (world space): the client lays it between two points and re-jags it every few
        /// hundredths of a second for its short life (WunderwaffeLightning). Solid and bright, with a light that
        /// rides its middle.
        /// </summary>
        private static void BuildChain(string name, Color c, Material lineMat)
        {
            var root = new GameObject(name);
            try
            {
                var line = root.AddComponent<LineRenderer>();
                line.sharedMaterial = lineMat;
                line.useWorldSpace = true;
                line.positionCount = 16;
                // Bolder and brighter (user, 2026-11-16: the lightning could be more apparent)
                line.widthCurve = AnimationCurve.Constant(0f, 1f, 0.05f);
                line.colorGradient = Solid(Color.Lerp(c, Color.white, 0.35f) * 4f);
                line.textureMode = LineTextureMode.Stretch;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.alignment = LineAlignment.View;
                Light light = new GameObject("light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false);
                light.type = LightType.Point;
                light.color = c;
                light.range = 8f;
                light.intensity = 4.5f;
                light.shadows = LightShadows.None;
                // A soft, wide glow round the bolt (the client lays it on the same jagged path)
                var halo = new GameObject("halo").AddComponent<LineRenderer>();
                halo.transform.SetParent(root.transform, false);
                halo.sharedMaterial = lineMat;
                halo.useWorldSpace = true;
                halo.positionCount = 16;
                halo.widthCurve = AnimationCurve.Constant(0f, 1f, 0.22f);
                halo.colorGradient = Solid(c * 0.7f);
                halo.textureMode = LineTextureMode.Stretch;
                halo.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                halo.receiveShadows = false;
                halo.alignment = LineAlignment.View;
                Save(root, name);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// The thin fog a bolt leaves hanging along its path (user, 2026-11-16): soft, faintly tinted puffs along a box
        /// the client stretches from one end of the jump to the other, swelling and fading over a second or so.
        /// </summary>
        private static void BuildFog(string name, Color c, Material smokeMat)
        {
            var root = new GameObject(name);
            try
            {
                ParticleSystem fog = Particles(root, "fog", smokeMat, local: false);
                var main = fog.main;
                main.loop = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.6f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.12f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.55f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                var tint = Color.Lerp(c, Color.white, 0.6f);
                main.startColor = new Color(tint.r, tint.g, tint.b, 0.08f);
                main.maxParticles = 200;
                Burst(fog, 24);
                var shape = fog.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(0.08f, 0.08f, 1f);
                Grow(fog, 1.8f);
                Fade(fog);
                Save(root, name);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- The arcs crawling down the barrel ----------

        /// <summary>
        /// One crackling arc: a thin, bright jagged line the client bends into a new shape every few hundredths of a
        /// second (WunderwaffeArcs), sparking off the rear fins as the gun fires, as in the original.
        /// </summary>
        private static void BuildArc(string name, Color c, Material lineMat, AudioClip flux)
        {
            var root = new GameObject(name);
            try
            {
                var line = root.AddComponent<LineRenderer>();
                line.sharedMaterial = lineMat;
                line.useWorldSpace = false;
                line.positionCount = 8;
                line.widthCurve = AnimationCurve.Constant(0f, 1f, 0.0022f);
                line.colorGradient = Solid(c * 3f);
                line.textureMode = LineTextureMode.Stretch;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.alignment = LineAlignment.View;
                Light light = new GameObject("light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false);
                light.type = LightType.Point;
                light.color = c;
                light.range = 0.6f;
                light.intensity = 1.5f;
                light.shadows = LightShadows.None;
                if (flux != null)
                {
                    // The original's crackle as the arc runs down the barrel (user, 2026-11-16)
                    var audio = root.AddComponent<AudioSource>();
                    audio.clip = flux;
                    audio.playOnAwake = true;
                    audio.spatialBlend = 1f;
                    audio.rolloffMode = AudioRolloffMode.Logarithmic;
                    audio.minDistance = 0.6f;
                    audio.maxDistance = 20f;
                    audio.volume = 0.7f;
                }
                Save(root, name);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- The impact ----------

        private static void BuildImpact(string name, Color c, Material glowMat, Material smokeMat, AudioClip boom)
        {
            var root = new GameObject(name);
            try
            {
                // The flash: a bright ball that pops and fades where the bolt hit
                ParticleSystem ball = Particles(root, "ball", glowMat, local: false);
                var main = ball.main;
                main.loop = false;
                main.duration = 0.3f;
                main.startLifetime = 0.3f;
                main.startSpeed = 0f;
                main.startSize = 1.1f;
                main.startColor = c * 2.2f;
                main.maxParticles = 3;
                Burst(ball, 2);
                SizeCurve(ball, new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(0.2f, 1f), new Keyframe(1f, 1.2f)));
                Fade(ball);

                // Sparks thrown out of the hit
                ParticleSystem sparks = Particles(root, "sparks", glowMat, local: false);
                main = sparks.main;
                main.loop = false;
                main.duration = 0.2f;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 7f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.09f);
                main.startColor = c * 2f;
                main.gravityModifier = 0.5f;
                main.maxParticles = 30;
                Burst(sparks, 18);
                var shape = sparks.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 0.05f;
                Fade(sparks);
                Drag(sparks, 3f);

                // A thin green wisp that hangs a moment
                ParticleSystem wisp = Particles(root, "wisp", smokeMat, local: false);
                main = wisp.main;
                main.loop = false;
                main.duration = 0.2f;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.55f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.startColor = new Color(c.r, c.g, c.b, 0.4f);
                main.gravityModifier = -0.04f;
                main.maxParticles = 12;
                Burst(wisp, 6);
                shape = wisp.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.15f;
                Grow(wisp, 2f);
                Fade(wisp);

                Light light = new GameObject("light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false);
                light.type = LightType.Point;
                light.color = c;
                light.range = 5f;
                light.intensity = 4f;
                light.shadows = LightShadows.None;

                if (boom != null)
                {
                    var audio = root.AddComponent<AudioSource>();
                    audio.clip = boom;
                    audio.loop = false;
                    audio.playOnAwake = true;
                    audio.spatialBlend = 1f;
                    audio.volume = 1f;
                    audio.rolloffMode = AudioRolloffMode.Linear;
                    audio.minDistance = 2f;
                    audio.maxDistance = 60f;
                }
                Save(root, name);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- Building blocks ----------

        private static Gradient Solid(Color c)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        private static Gradient Fading(Color c)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        private static ParticleSystem Particles(GameObject root, string name, Material mat, bool local)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = true;
            main.simulationSpace = local ? ParticleSystemSimulationSpace.Local : ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var shape = ps.shape;
            shape.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        private static void Burst(ParticleSystem ps, int count)
        {
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
        }

        private static void Grow(ParticleSystem ps, float grow) =>
            SizeCurve(ps, new AnimationCurve(new Keyframe(0f, 1f / grow), new Keyframe(1f, 1f)), grow);

        private static void SizeCurve(ParticleSystem ps, AnimationCurve curve, float scale = 1f)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(scale, curve);
        }

        private static void Fade(ParticleSystem ps)
        {
            var colour = ps.colorOverLifetime;
            colour.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.3f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(g);
        }

        private static void Drag(ParticleSystem ps, float drag)
        {
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 0f;
            limit.dampen = 0f;
            limit.drag = drag;
        }

        private static void Save(GameObject root, string name)
        {
            string path = PrefabPath(name);
            AssetDatabase.DeleteAsset(path);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }

        private delegate Color Pixel(float x, float y);

        // A soft round glow: bright middle, smooth falloff
        private static Color GlowPixel(float x, float y)
        {
            float d = Mathf.Clamp01(Mathf.Sqrt(x * x + y * y));
            return new Color(1f, 1f, 1f, Mathf.Pow(1f - d, 2.2f));
        }

        // Across a line: a sharp bright middle, quick soft edges (V runs across a trail)
        private static Color StreakPixel(float x, float y)
        {
            float a = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(y)), 3f);
            return new Color(1f, 1f, 1f, a);
        }

        // A smoke puff: a soft disc broken up by noise
        private static Color PuffPixel(float x, float y)
        {
            float d = Mathf.Clamp01(Mathf.Sqrt(x * x + y * y));
            float n = Mathf.PerlinNoise(x * 3.1f + 5f, y * 3.1f + 9f) * 0.6f + Mathf.PerlinNoise(x * 7.3f + 2f, y * 7.3f + 4f) * 0.4f;
            float a = Mathf.Clamp01((1f - d) * 1.6f - 0.25f) * Mathf.Lerp(0.45f, 1f, n);
            return new Color(1f, 1f, 1f, a);
        }

        private static Texture2D SaveTexture(string name, int size, Pixel pixel)
        {
            string path = $"{WwPaths.Root}/Fx/{name}.png";
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, pixel((x + 0.5f) / size * 2f - 1f, (y + 0.5f) / size * 2f - 1f));
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material SaveMaterial(string name, string shader, Texture2D tex, Color tint)
        {
            string path = $"{WwPaths.Root}/Fx/{name}.mat";
            AssetDatabase.DeleteAsset(path);
            var mat = new Material(Shader.Find(shader)) { name = name, mainTexture = tex };
            if (mat.HasProperty("_TintColor"))
                mat.SetColor("_TintColor", new Color(tint.r * 0.5f, tint.g * 0.5f, tint.b * 0.5f, 0.5f));
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
