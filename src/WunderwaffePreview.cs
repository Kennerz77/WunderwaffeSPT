using System.Reflection;
using EFT.UI.WeaponModding;
using HarmonyLib;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// The 3D view in the item window centres a gun on its saved pivot, which only fits the pose the model was saved in
    /// (the Wunderwaffe came out off to the side, user 2026-11-15). Here it is centred on what's actually drawn, every time
    /// (the Ray Gun's fix).
    /// </summary>
    internal static class WunderwaffePreview
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(WeaponPreview), nameof(WeaponPreview.PositionGameObject)),
                postfix: new HarmonyMethod(typeof(WunderwaffePreview).GetMethod(nameof(PositionPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void PositionPostfix(GameObject itemGameObject)
        {
            try
            {
                if (itemGameObject == null || !IsOurs(itemGameObject))
                    return;
                // Centred on the gun itself: not the clip (at rest it sits off behind the gun; hidden here, as the view
                // hides it outside a reload) nor the hidden holder of the upgraded materials
                Transform clip = null;
                foreach (Transform t in itemGameObject.GetComponentsInChildren<Transform>(true))
                    if (t.name == "tag_clip")
                        clip = t;
                // Shrunk parts (the magazine slot's model rides the clip's bones, off behind the gun at rest) switched off
                // too: EFT frames the item window on every active renderer
                foreach (Renderer r in itemGameObject.GetComponentsInChildren<Renderer>(true))
                    if (r.transform.lossyScale.x < 0.01f)
                        r.gameObject.SetActive(false);
                Bounds bounds = default;
                bool any = false;
                foreach (Renderer r in itemGameObject.GetComponentsInChildren<Renderer>(true))
                {
                    if (clip != null && r.transform.IsChildOf(clip))
                    {
                        r.forceRenderingOff = true;
                        continue;
                    }
                    if (r.forceRenderingOff || !r.enabled || r.name == "ww_upgraded_materials" || !(r is MeshRenderer || r is SkinnedMeshRenderer))
                        continue;
                    if (any)
                        bounds.Encapsulate(r.bounds);
                    else
                        bounds = r.bounds;
                    any = true;
                }
                if (!any || bounds.extents == Vector3.zero)
                    return;
                Transform parent = itemGameObject.transform.parent;
                Vector3 centre = parent != null ? parent.position : Vector3.zero;
                itemGameObject.transform.position += centre - bounds.center;
            }
            catch (System.Exception e)
            {
                WunderwaffePlugin.Log.LogWarning($"Wunderwaffe preview centring skipped: {e.Message}");
            }
        }

        // Our model's weapon object (inside the container) is named weapon_dg2*
        private static bool IsOurs(GameObject go)
        {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith("weapon_dg2"))
                    return true;
            return false;
        }
    }
}
