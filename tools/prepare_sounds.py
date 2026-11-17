"""Prepares the original's Wunderwaffe DG-2 sounds (GrayWolf export) for the Unity import, into sounds_source\\.

    C:\\Git\\.venv\\Scripts\\python.exe -I tools\\prepare_sounds.py

  * ww_shot: the player shot's front, surround and low-end layers mixed (plr\\shot\\shot_00_f/_rs/_lfe)
  * ww_shot_last: the shot that empties the battery (shot_last_00_f), with the same low end
  * ww_impact: the bolt landing (projectile\\impact\\proj_impact)
  * ww_idle: the gun's idle hum (idle\\tesla_idle_00)
  * the reload foley (reload\\plr\\tesla_*)
All mono (EFT and the mod play them as 3D sounds) and levelled on their attack.
"""
import os
import wave

import numpy as np

ROOT = os.environ.get('KZ_GRAYWOLF_BO3', r'E:\GrayWolf\exported_files\bo3')
SRC = os.path.join(ROOT, 'sounds', 'all', 'wpn', 'energy', 'dg2')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'sounds_source')


def read(path):
    with wave.open(path) as w:
        sr, ch, n = w.getframerate(), w.getnchannels(), w.getnframes()
        a = np.frombuffer(w.readframes(n), dtype=np.int16).reshape(-1, ch).astype(np.float32) / 32768
    return a.mean(axis=1, keepdims=True), sr


def write(path, a, sr):
    a = np.clip(a, -1, 1)
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((a * 32767).astype(np.int16).tobytes())


def layer(*parts):
    n = max(len(a) for a, _ in parts)
    out = np.zeros((n, 1), np.float32)
    for a, g in parts:
        out[:len(a)] += a * g
    return out


def fade_out(a, sr, seconds):
    """The last stretch faded smoothly to nothing."""
    a = a.copy()
    n = min(len(a), int(seconds * sr))
    a[-n:] *= (np.cos(np.linspace(0, np.pi, n)) * 0.5 + 0.5)[:, None]
    return a


def punchy(a, sr, attack_rms_db):
    """Raised so the first 0.15 s sits at this loudness, soft-limited under full scale."""
    head = a[:int(0.15 * sr)]
    rms = np.sqrt((head ** 2).mean()) + 1e-9
    b = a * (10 ** (attack_rms_db / 20) / rms)
    knee = 0.6
    over = np.abs(b) > knee
    b[over] = np.sign(b[over]) * (knee + (1 - knee) * np.tanh((np.abs(b[over]) - knee) / (1 - knee)))
    return b * 0.98


def main():
    os.makedirs(OUT, exist_ok=True)
    f, sr = read(os.path.join(SRC, 'plr', 'shot', 'shot_00_f.wav'))
    rs, _ = read(os.path.join(SRC, 'plr', 'shot', 'shot_00_rs.wav'))
    lfe, _ = read(os.path.join(SRC, 'plr', 'shot', 'shot_00_lfe.wav'))
    # Louder (user, 2026-11-16: "more louder")
    write(os.path.join(OUT, 'ww_shot.wav'), punchy(layer((f, 1.0), (rs, 0.6), (lfe, 0.6)), sr, -3), sr)
    last, sr = read(os.path.join(SRC, 'plr', 'shot', 'shot_last_00_f.wav'))
    write(os.path.join(OUT, 'ww_shot_last.wav'), punchy(layer((last, 1.0), (lfe, 0.6)), sr, -3), sr)
    imp, sr = read(os.path.join(SRC, 'projectile', 'impact', 'proj_impact.wav'))
    write(os.path.join(OUT, 'ww_impact.wav'), punchy(imp, sr, -9), sr)
    # The crackle of the lightning running down the barrel (user, 2026-11-16)
    for side in ('l', 'r'):
        flux, sr = read(os.path.join(SRC, 'flux', 'wpn_tesla_flux_%s.wav' % side))
        write(os.path.join(OUT, 'ww_flux_%s.wav' % side), punchy(flux, sr, -16), sr)
    idle, sr = read(os.path.join(SRC, 'idle', 'tesla_idle_00.wav'))
    write(os.path.join(OUT, 'ww_idle.wav'), punchy(idle, sr, -22), sr)
    for name in ('tesla_clip_in', 'tesla_handle_pullback', 'tesla_handle_release', 'tesla_start_reload',
                 'tesla_switch_flip_off', 'tesla_switch_flip_on'):
        a, sr = read(os.path.join(SRC, 'reload', 'plr', name + '.wav'))
        # The original's switch-on hum stops dead at full level (a click as the draw ends, user 2026-11-16): cut to
        # 1.5 s, the last half second faded out. Every other piece gets a short fade so none ends on a click
        a = fade_out(a[:int(1.5 * sr)], sr, 0.5) if name == 'tesla_switch_flip_on' else fade_out(a, sr, 0.06)
        write(os.path.join(OUT, name + '.wav'), punchy(a, sr, -9), sr)
    print('sounds_source:', ', '.join(sorted(os.listdir(OUT))))


if __name__ == '__main__':
    main()
