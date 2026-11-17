using System.Collections;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// The original's idle lightning: while the gun is loaded and in your hands, an arc now and then crawls down the
    /// barrel coil with the original's crackle (ww_flux), from the coil's back (tag_fx) to the muzzle (tag_flash), reshaped every few hundredths of a second
    /// (blue; purple-pink on a Pack-a-Punched gun), ending in a crackle round the muzzle. The arc hangs off the coil's bone, so it rides the gun.
    /// </summary>
    internal static class WunderwaffeArcs
    {
        private const float CrawlTime = 0.28f, Reshape = 0.03f;
        // How much of the barrel one arc spans at a time, and how far it strays off the line (metres)
        private const float Span = 0.35f, Stray = 0.012f;
        private const int Points = 10;

        public static void Crawl(Transform start, Transform end, bool upgraded)
        {
            try
            {
                GameObject prefab = WunderwaffeBolts.Prefab(upgraded ? "ww_arc_up" : "ww_arc");
                if (prefab == null || start == null || end == null)
                    return;
                GameObject arc = Object.Instantiate(prefab, Follow(start), false);
                foreach (Transform t in arc.GetComponentsInChildren<Transform>(true))
                    t.gameObject.layer = start.gameObject.layer;
                LineRenderer line = arc.GetComponent<LineRenderer>();
                if (line != null)
                {
                    line.positionCount = Points;
                    line.widthMultiplier = 1.8f;
                }
                foreach (AudioSource audio in arc.GetComponentsInChildren<AudioSource>(true))
                {
                    audio.volume *= WunderwaffeSettings.BarrelVolume.Value;
                    audio.pitch = Random.Range(0.9f, 1.1f);
                }
                arc.SetActive(true);
                WunderwaffePlugin.Runner.StartCoroutine(Run(arc, line, start, end));
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe barrel arc skipped: {e.Message}");
            }
        }

        private static IEnumerator Run(GameObject arc, LineRenderer line, Transform start, Transform end)
        {
            float began = Time.time;
            Light light = arc.GetComponentInChildren<Light>();
            while (arc != null && start != null && end != null && Time.time - began < CrawlTime)
            {
                float k = (Time.time - began) / CrawlTime;
                // The far end, in the coil bone's own space (the gun's scale included)
                Vector3 tip = start.InverseTransformPoint(end.position);
                float from = Mathf.Clamp01(k - Span * 0.5f), to = Mathf.Clamp01(k + Span * 0.5f);
                if (line != null)
                    Shape(line, tip * from, tip * to);
                if (light != null)
                    light.transform.localPosition = tip * k;
                yield return new WaitForSeconds(Reshape);
            }
            if (arc != null)
            {
                // The line goes; the crackle plays out
                if (line != null)
                    line.enabled = false;
                if (light != null)
                    light.enabled = false;
                Object.Destroy(Holder(arc), 1.2f);
            }
            // At the end of its run it bursts round the muzzle (user, 2026-11-16): a few short arcs crackling there
            if (end != null && arc != null)
                for (int i = 0; i < BurstArcs; i++)
                    Spark(end, arc.name.Contains("_up"));
        }

        private const int BurstArcs = 4;

        /// <summary>
        /// A holder that follows <paramref name="bone"/> (place, turn, scale) every frame without being part of the gun's
        /// model. Arcs made as children of the gun's bones and destroyed a moment later broke the hands (user,
        /// 2026-11-17: melee then reload): the game notes a model's renderers when it puts the gun on your back, and
        /// switching that model off again touched the destroyed arc and failed, leaving no gun in the hands.
        /// </summary>
        private static Transform Follow(Transform bone)
        {
            var holder = new GameObject("ww_arc_holder");
            holder.layer = bone.gameObject.layer;
            holder.AddComponent<WwFollow>().Target = bone;
            holder.transform.SetPositionAndRotation(bone.position, bone.rotation);
            holder.transform.localScale = bone.lossyScale;
            return holder.transform;
        }

        /// <summary>What to destroy for an arc: its holder when it has one.</summary>
        private static GameObject Holder(GameObject arc) =>
            arc.transform.parent != null && arc.transform.parent.GetComponent<WwFollow>() != null ? arc.transform.parent.gameObject : arc;

        internal sealed class WwFollow : MonoBehaviour
        {
            public Transform Target;

            private void LateUpdate()
            {
                if (Target == null)
                {
                    Destroy(gameObject);
                    return;
                }
                transform.SetPositionAndRotation(Target.position, Target.rotation);
                transform.localScale = Target.lossyScale;
                // Shown only while the gun it rides is
                bool on = Target.gameObject.activeInHierarchy;
                for (int i = 0; i < transform.childCount; i++)
                    if (transform.GetChild(i).gameObject.activeSelf != on)
                        transform.GetChild(i).gameObject.SetActive(on);
            }
        }
        private const float BurstLife = 0.3f, BurstReach = 0.06f;
        private static float _sparkReach = BurstReach;

        /// <summary>A jagged arc between two moving points (two parts of a fried body) for a moment, silent, in world space.</summary>
        public static void Between(Transform a, Transform b, bool upgraded, float life)
        {
            GameObject prefab = WunderwaffeBolts.Prefab(upgraded ? "ww_arc_up" : "ww_arc");
            if (prefab == null || a == null || b == null)
                return;
            GameObject arc = Object.Instantiate(prefab);
            foreach (AudioSource audio in arc.GetComponentsInChildren<AudioSource>(true))
                Object.Destroy(audio);
            LineRenderer line = arc.GetComponent<LineRenderer>();
            if (line != null)
            {
                line.useWorldSpace = true;
                line.positionCount = 9;
                line.widthMultiplier = 2.5f;
            }
            arc.SetActive(true);
            WunderwaffePlugin.Runner.StartCoroutine(Jump(arc, line, a, b, life));
        }

        private static IEnumerator Jump(GameObject arc, LineRenderer line, Transform a, Transform b, float life)
        {
            float end = Time.time + life;
            while (arc != null && a != null && b != null && Time.time < end)
            {
                if (line != null)
                {
                    int n = line.positionCount;
                    Vector3 from = a.position, to = b.position;
                    float wild = Mathf.Clamp(Vector3.Distance(from, to) * 0.15f, 0.02f, 0.12f);
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)(n - 1);
                        Vector3 p = Vector3.Lerp(from, to, t);
                        if (i > 0 && i < n - 1)
                            p += Random.insideUnitSphere * wild * Mathf.Sin(t * Mathf.PI);
                        line.SetPosition(i, p);
                    }
                    Light light = arc.GetComponentInChildren<Light>();
                    if (light != null)
                        light.transform.position = (from + to) * 0.5f;
                    line.widthMultiplier = SeenWidth((from + to) * 0.5f, 2.5f);
                }
                yield return new WaitForSeconds(Reshape);
            }
            if (arc != null)
                Object.Destroy(arc);
        }

        /// <summary>One short arc flickering round a point (a head the lightning fried) for a moment, silent.</summary>
        public static void SparkAround(Transform at, bool upgraded, float reach)
        {
            float before = _sparkReach;
            _sparkReach = reach;
            try
            {
                Spark(at, upgraded);
            }
            finally
            {
                _sparkReach = before;
            }
        }

        /// <summary>One short arc flickering round the muzzle for a moment (silent: the crawl's crackle covers it).</summary>
        private static void Spark(Transform at, bool upgraded)
        {
            GameObject prefab = WunderwaffeBolts.Prefab(upgraded ? "ww_arc_up" : "ww_arc");
            if (prefab == null)
                return;
            GameObject arc = Object.Instantiate(prefab, Follow(at), false);
            foreach (Transform t in arc.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = at.gameObject.layer;
            foreach (AudioSource audio in arc.GetComponentsInChildren<AudioSource>(true))
                Object.Destroy(audio);
            LineRenderer line = arc.GetComponent<LineRenderer>();
            if (line != null)
            {
                line.positionCount = 6;
                line.widthMultiplier = 1.4f;
            }
            arc.SetActive(true);
            WunderwaffePlugin.Runner.StartCoroutine(Flicker(arc, line, _sparkReach));
        }

        private static IEnumerator Flicker(GameObject arc, LineRenderer line, float reach)
        {
            float end = Time.time + BurstLife * Random.Range(0.7f, 1.3f);
            while (arc != null && Time.time < end)
            {
                if (line != null)
                {
                    float scale = Mathf.Max(0.0001f, line.transform.lossyScale.x);
                    Vector3 a = Random.onUnitSphere * reach * 0.3f / scale;
                    Vector3 b = Random.onUnitSphere * reach / scale;
                    Shape(line, a, b);
                    line.widthMultiplier = SeenWidth(line.transform.position, 1.4f);
                }
                yield return new WaitForSeconds(Reshape);
            }
            if (arc != null)
                Object.Destroy(Holder(arc));
        }

        // The arc prefab's line is 2.2 mm wide, made for the barrel in your hands: on a body metres away that's well under
        // a pixel and never showed (user, 2026-11-16: "doesn't make lightning around them"). Widened with distance so it
        // stays about three pixels wide
        private const float LineWidth = 0.0022f, PixelsWide = 0.005f;

        /// <summary>The width multiplier that keeps an arc here visible from the camera (never under its own).</summary>
        private static float SeenWidth(Vector3 at, float own)
        {
            Camera cam = Camera.main;
            if (cam == null)
                return own;
            float distance = Vector3.Distance(cam.transform.position, at);
            return Mathf.Max(own, distance * PixelsWide / LineWidth);
        }

        /// <summary>A new jagged path between two points along the barrel.</summary>
        private static void Shape(LineRenderer line, Vector3 a, Vector3 b)
        {
            int n = line.positionCount;
            float stray = Stray / Mathf.Max(0.0001f, line.transform.lossyScale.x);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                Vector3 p = Vector3.Lerp(a, b, t);
                if (i > 0 && i < n - 1)
                    p += Random.insideUnitSphere * stray;
                line.SetPosition(i, p);
            }
        }
    }
}
