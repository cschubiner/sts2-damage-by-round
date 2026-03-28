# STS2 Damage By Round Overlay

<img width="625" height="430" alt="image" src="https://github.com/user-attachments/assets/60a989cd-ef8e-4f86-be7c-6ff9871a82a8" />

<img width="2032" height="1220" alt="image" src="https://github.com/user-attachments/assets/45b5d7c6-1702-4573-835c-10fb3c9b5db4" />

Slay the Spire 2 co-op does not do a great job of telling you how much each player actually contributed in a fight.

That is the whole point of this mod.

It adds a simple overlay so you can see how you are doing compared to the other players in your co-op run, instead of guessing after every combat.

A small Slay the Spire 2 overlay mod for co-op that shows how much damage each player contributed.

It displays:

- per-round damage by player for the current combat
- current combat total by player
- current Act total by player
- whole-run total by player

This is meant to solve the "who actually did the damage in this fight?" problem in co-op runs.

## What It Looks Like

During combat, the mod adds a panel in the top-right of the screen that shows lines like:

```text
R1: Ironclad 18 | Silent 11
R2: Ironclad 12 | Silent 24

Combat: Ironclad 30 | Silent 35
Act: Ironclad 84 | Silent 91
Total: Ironclad 146 | Silent 152
```

## Installation

These instructions are for the current local mod workflow in Slay the Spire 2.

### 1. Find your STS2 mod folder

On macOS, the mod folder is:

```text
/Users/<your-user>/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/
```

Create this folder if it does not already exist:

```text
DamageByRoundOverlay
```

Final path:

```text
/Users/<your-user>/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/DamageByRoundOverlay
```

### 2. Build the mod

The project targets `net9.0`.

```bash
dotnet build -c Release
```

The built DLL will be here:

```text
bin/Release/net9.0/Sts2DamageByRound.dll
```

### 3. Copy the files into the mod folder

Copy:

- `bin/Release/net9.0/Sts2DamageByRound.dll`
- `mod_manifest.json`

Into the STS2 mod folder above.

Important:

- the DLL should be named `DamageByRoundOverlay.dll` inside the installed mod folder
- the folder name should match the mod id: `DamageByRoundOverlay`

So a working install looks like:

```text
mods/DamageByRoundOverlay/
  DamageByRoundOverlay.dll
  mod_manifest.json
```

If you want, you can also keep a second copy named `Sts2DamageByRound.dll`, but the important one for loading is `DamageByRoundOverlay.dll`.

### 4. Enable mods

Slay the Spire 2 stores mod settings in its settings save. Once the mod is enabled, launching the game normally through Steam should load it.

### 5. Launch through Steam

Launch Slay the Spire 2 from Steam so Steam initialization and your Steam save/profile path work normally.

## How It Works

The mod hooks into STS2 combat events and tracks player-owned damage dealt to enemies.

It keeps three buckets:

- `Combat`: resets each fight
- `Act`: keeps accumulating through the current Act
- `Total`: keeps accumulating through the run

The overlay is read-only UI. It does not change combat behavior, cards, relics, rewards, enemies, or progression.

## Caveats

- This is aimed at co-op contribution tracking.
- The mod tracks damage from the point it is loaded for that run.
- If you load into the middle of an already-progressed run, past fights are not reconstructed retroactively.
- STS2 modding and Workshop support are still evolving, so local installation is the most reliable path right now.

## Build Notes

The project file references the local game assemblies:

- `sts2.dll`
- `GodotSharp.dll`
- `0Harmony.dll`

So if your STS2 install is in a different location, you may need to update the paths in [Sts2DamageByRound.csproj](./Sts2DamageByRound.csproj).

## Repository Contents

- [DamageOverlayMod.cs](./DamageOverlayMod.cs): overlay UI and damage tracking logic
- [Sts2DamageByRound.csproj](./Sts2DamageByRound.csproj): build configuration
- [mod_manifest.json](./mod_manifest.json): STS2 mod metadata

## Status

This mod was built and tested against Slay the Spire 2 Early Access on macOS.
