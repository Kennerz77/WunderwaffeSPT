using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;

namespace Wunderwaffe
{
    /// <summary>
    /// The Wunderwaffe's shot, played from our own audio sources: the original's shot, and its own shot for the charge
    /// that empties the battery, each whole. EFT's weapon sound player cuts shots short and runs them through its own
    /// gunshot mix, which came out quiet and muffled on the Mk II (user, 2026-11-15), so the gun's own shot bank is
    /// silent. Yours plays in your head.
    /// </summary>
    internal static class WunderwaffeShotSound
    {
        private const int Voices = 8;
        private static AudioClip[] _shots = new AudioClip[0];
        private static AudioClip _pap, _last;
        private static readonly List<AudioSource> Pool = new List<AudioSource>();
        private static int _next;

        /// <summary>The clips ride in the gun's bundle, on its sound player (the view hands it over as a model appears).</summary>
        public static void Load(WeaponSoundPlayer sounds)
        {
            if (_shots.Length > 0 || sounds?.AdditionalSounds == null)
                return;
            AudioClip Clip(string ev) => sounds.AdditionalSounds.FirstOrDefault(e => e?.EventName == ev)?.SoundClips?.FirstOrDefault();
            _shots = new[] { Clip("SndWwShot0") }.Where(c => c != null).ToArray();
            _pap = Clip("SndWwPapShot");
            _last = Clip("SndWwLastShot");
            if (_shots.Length == 0)
                WunderwaffePlugin.Log.LogWarning("Wunderwaffe shot sounds missing from the gun's bundle");
        }

        /// <summary>A Wunderwaffe has just fired at this point (last: the charge that empties the battery).</summary>
        public static void Play(Vector3 at, bool upgraded, bool last)
        {
            AudioClip clip = last && _last != null ? _last
                : upgraded && _pap != null ? _pap : _shots.Length > 0 ? _shots[Random.Range(0, _shots.Length)] : null;
            if (clip == null)
                return;
            AudioSource s = Voice();
            Camera cam = Camera.main;
            bool mine = cam != null && Vector3.Distance(cam.transform.position, at) < 2.5f;
            s.transform.position = at;
            s.spatialBlend = mine ? 0f : 1f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = 4f;
            s.maxDistance = 150f;
            s.volume = WunderwaffeSettings.ShotVolume.Value;
            s.pitch = Random.Range(0.98f, 1.02f);
            s.clip = clip;
            s.Play();
        }

        private static AudioSource Voice()
        {
            Pool.RemoveAll(p => p == null);
            if (Pool.Count < Voices)
            {
                var go = new GameObject("Wunderwaffe_ShotVoice");
                Object.DontDestroyOnLoad(go);
                AudioSource src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                Pool.Add(src);
                return src;
            }
            return Pool[_next++ % Pool.Count];
        }
    }
}
