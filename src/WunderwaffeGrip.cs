using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// The left hand's fingers closed round the Wunderwaffe in third person (user, 2026-11-16: on the menu character the
    /// hand hung open under the gun). The body's own animation is for the gun it borrows its moves from, whose front grip
    /// sits elsewhere, so each frame, after the animation and the hand's reach, the four fingers curl in towards the
    /// palm and the thumb a little. Which way "in" is comes from the hand itself (the side of the palm the thumb sits on),
    /// so it holds whatever the bones' axes. On menu characters and on other players holding it in a raid (your own body
    /// is the first person view's, left as it is).
    /// </summary>
    [DefaultExecutionOrder(32050)]
    internal sealed class WunderwaffeGrip : MonoBehaviour
    {
        // Each finger's joints, base to tip, and how far each bends (degrees)
        private static readonly float[] Bend = { 55f, 65f, 40f };
        private static readonly float[] ThumbBend = { 10f, 20f, 15f };

        private Func<bool> _holding;
        private Transform _palm, _thumb, _index, _pinky;
        private readonly List<Transform[]> _fingers = new List<Transform[]>();
        private Transform[] _thumbJoints;
        // What this wrote last frame, and the pose under it: an animator that skipped a frame (off screen) leaves the
        // last curl in place, which would otherwise curl again on top
        private readonly Dictionary<Transform, (Quaternion written, Quaternion under)> _last = new Dictionary<Transform, (Quaternion, Quaternion)>();

        public static void On(GameObject host, Transform body, Func<bool> holding)
        {
            if (host == null || body == null)
                return;
            WunderwaffeGrip grip = host.GetComponent<WunderwaffeGrip>() ?? host.AddComponent<WunderwaffeGrip>();
            grip.Init(body, holding);
        }

        public static void Off(GameObject host)
        {
            WunderwaffeGrip grip = host != null ? host.GetComponent<WunderwaffeGrip>() : null;
            if (grip != null)
                Destroy(grip);
        }

        private void Init(Transform body, Func<bool> holding)
        {
            _holding = holding;
            var byName = new Dictionary<string, Transform>();
            foreach (Transform t in body.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name))
                    byName[t.name] = t;
            Transform Bone(string n) => byName.TryGetValue("Base HumanL" + n, out Transform t) ? t : null;
            _palm = Bone("Palm");
            _thumb = Bone("Digit11");
            _index = Bone("Digit21");
            _pinky = Bone("Digit51");
            _fingers.Clear();
            for (int f = 2; f <= 5; f++)
            {
                Transform[] joints = { Bone($"Digit{f}1"), Bone($"Digit{f}2"), Bone($"Digit{f}3") };
                if (joints.All(j => j != null))
                    _fingers.Add(joints);
            }
            Transform[] thumb = { Bone("Digit11"), Bone("Digit12"), Bone("Digit13") };
            _thumbJoints = thumb.All(j => j != null) ? thumb : null;
            _last.Clear();
        }

        private void LateUpdate()
        {
            if (_palm == null || _thumb == null || _index == null || _pinky == null || _fingers.Count == 0)
                return;
            bool holding;
            try
            {
                holding = _holding == null || _holding();
            }
            catch
            {
                holding = false;
            }
            if (!holding)
            {
                _last.Clear();
                return;
            }
            // Back to the animation's pose where the animator didn't write this frame
            foreach (var kv in _last)
                if (kv.Key != null && kv.Key.localRotation == kv.Value.written)
                    kv.Key.localRotation = kv.Value.under;

            // The palm's front: square to the fingers and the knuckle line, on the thumb's side
            Vector3 p = _palm.position;
            Vector3 along = ((_index.position + _pinky.position) * 0.5f - p).normalized;
            Vector3 across = (_pinky.position - _index.position).normalized;
            Vector3 front = Vector3.Cross(along, across).normalized;
            if (Vector3.Dot(front, _thumb.position - p) < 0f)
                front = -front;

            var wrote = new Dictionary<Transform, (Quaternion, Quaternion)>();
            foreach (Transform[] finger in _fingers)
                Curl(finger, Bend, front, wrote);
            if (_thumbJoints != null)
                Curl(_thumbJoints, ThumbBend, front, wrote);
            _last.Clear();
            foreach (var kv in wrote)
                _last[kv.Key] = kv.Value;
        }

        /// <summary>Each joint, base first, turned so the bone after it bends towards the palm's front.</summary>
        private static void Curl(Transform[] joints, float[] bend, Vector3 front, Dictionary<Transform, (Quaternion, Quaternion)> wrote)
        {
            for (int j = 0; j < joints.Length; j++)
            {
                Transform joint = joints[j];
                Transform next = j + 1 < joints.Length ? joints[j + 1] : (joint.childCount > 0 ? joint.GetChild(0) : null);
                Vector3 dir = next != null ? (next.position - joint.position) : joint.position - joints[Mathf.Max(0, j - 1)].position;
                if (dir.sqrMagnitude < 1e-8f)
                    continue;
                dir.Normalize();
                Vector3 axis = Vector3.Cross(dir, front);
                if (axis.sqrMagnitude < 1e-6f)
                    continue;
                axis.Normalize();
                Quaternion under = joint.localRotation;
                Quaternion turn = Quaternion.AngleAxis(bend[j], axis);
                // Whichever way round the turn goes, the bend must go towards the palm
                if (Vector3.Dot(turn * dir, front) < Vector3.Dot(dir, front))
                    turn = Quaternion.AngleAxis(-bend[j], axis);
                joint.rotation = turn * joint.rotation;
                wrote[joint] = (joint.localRotation, under);
            }
        }

        // ---------- Who gets it ----------

        private static float _nextScan;

        /// <summary>Every second or so in a raid: the other players holding a Wunderwaffe get the grip.</summary>
        public static void Scan()
        {
            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world?.AllAlivePlayersList == null)
                return;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (p == null || p.IsYourPlayer || p.GetComponent<WunderwaffeGrip>() != null || !WunderwaffeIds.Is(p.HandsController?.Item))
                    continue;
                Player holder = p;
                On(p.gameObject, p.PlayerBones != null ? p.PlayerBones.transform : ((Component)p).transform,
                    () => holder != null && WunderwaffeIds.Is(holder.HandsController?.Item));
            }
        }
    }
}
