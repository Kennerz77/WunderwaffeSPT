# Wunderwaffe DG-2

The **Wunderwaffe DG-2** from classic zombies, in SPT. The HD model with its own first-person animations and sounds, firing chain lightning that jumps from enemy to enemy, with a chalk wall buy in your hideout to get it. Pack-a-Punch it into **The Wunderwaffe DG-3 JZ**.

Version 1.0.0, for **SPT 4.1.6**. Made by **Kenner**.

**Dr K's SPZombies:** with Kenner's zombies mode installed, the Wunderwaffe is one of the wonder weapons the host can pick for the mystery box, and zombies it hits are electrocuted before they drop. On its own it works in normal play exactly as described here.

## Requirements

| Mod | Version | Why |
|---|---|---|
| SPT | 4.1.x (made on 4.1.6) | |
| [Hideout Uncensored](https://sp-mod.com/mod/3167/hideout-uncensored) by **trappuss** ([GitHub](https://github.com/trappuss/SPT-Mods-Release)) | 1.3.0 or newer, **both** its client and server parts | Weapons anywhere in the hideout: the wall buy hands you the Wunderwaffe straight away, outside the shooting range |

Optional:

| Mod | What it adds |
|---|---|
| [Pack-a-Punch Hideout Module](https://github.com/Kennerz77/papHideoutModule) **2.2.1** (by Kenner) | Pack-a-Punch the Wunderwaffe into **The Wunderwaffe DG-3 JZ**: purple lightning, longer chains and two shots a bulb. |
| [Dr K's SPZombies](https://github.com/Kennerz77/KennerZombiesSPT) **0.2.0 or newer** (by Kenner) | The Wunderwaffe as a wonder weapon in the mystery box. |

## Installing

1. Close the game and the SPT server.
2. Extract the zip into your SPT folder (the one with the `SPT_Runtime` folder). You should end up with:
   - `BepInEx\plugins\Wunderwaffe\` (`Wunderwaffe.dll`, `ww_fx.bundle`, `camo\`, `icons\`, `sounds\`)
   - `SPT_Runtime\user\mods\Wunderwaffe\` (`WunderwaffeServer.dll`, `bundles.json`, `bundles\`)
3. Start the server, then the game.

## Getting it

Once your hideout's **side room is open**, there's a **chalk outline of the Wunderwaffe** on a wall in your hideout, with a green **ammo case** beside it.

- **The wall:** buy the Wunderwaffe for **950,000 ₽** (from your stash). It slides out of the wall, loaded. If a primary slot is free it's equipped and drawn; otherwise it goes to your stash. The wall sells it again on your first hideout visit after a raid.
- **The ammo case:** buy a full **battery** for **20,000 ₽**. It goes into a free spot on you first, then into your stash.
- Not enough money or no room: nothing is charged.

The wall and the ammo case are the only way to get the Wunderwaffe and its batteries: they're never at traders, on the flea market, in loot or on bots.

## The gun

- A 4x2 gun for either primary slot, 4.5 kg.
- Each shot is a bolt of lightning at what you aim at (or the nearest enemy close to your aim), which then jumps to the nearest enemy, up to **10** in a chain. Each one it hits is killed. Your own group is never touched.
- A battery holds **3 charges**, shown by the three bulbs on the gun. The bulbs dim as you fire and light up again when a fresh battery goes in.
- Lightning crawls down the barrel now and then, and the gun flickers to life as you draw it.
- Bots hit by the lightning yell and jerk for a moment before they drop.

## The Wunderwaffe DG-3 JZ (with Pack-a-Punch Hideout Module)

- It goes into the machine as a plain Wunderwaffe and comes out as the DG-3 JZ, in its own gold engraved finish, with purple lightning.
- Chains up to **24** enemies, and each bulb lasts **two shots** (6 a battery).
- Stripping its power makes it a Wunderwaffe again.

## Selling

Traders pay the wall price for the Wunderwaffe (plus the Pack-a-Punch fee when infused). A battery sells by the charges left in it.

## Settings (F12)

- **Normal gameplay:** whether the Wunderwaffe and its wall buy are in your normal hideout and raids at all, or kept for the zombies mode only.
- **Volume:** **Shot**, **Reload and handling**, **Lightning strike**, **Barrel lightning**, **Purchase**, **Wall buy** and **Deny**.

Prices are in `SPT_Runtime\user\mods\Wunderwaffe\config.json` (create it to change them):

```json
{ "WallBuyPrice": 950000, "CartridgePrice": 20000 }
```

## Compatibility

- Never changes another mod's files, settings or load order.
- Works with Fika co-op: in a zombies match every player sees the lightning.
- Other gun and item mods work alongside it.

## Uninstalling

Delete `BepInEx\plugins\Wunderwaffe\` and `SPT_Runtime\user\mods\Wunderwaffe\`. The mod keeps its items out of your profile file and adds them back when the server loads, so without the mod they're simply gone.

## Credits

- Made by Kenner.
- **Hideout Uncensored** by **trappuss**: needed for the hideout wall buy. Not included.
- Third-party game assets belong to their respective owners. This is a free, non-commercial fan mod.

## Changelog

- **1.0.0:** first release.
