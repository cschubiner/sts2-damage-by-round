# Damage By Round Overlay

Small Slay the Spire 2 overlay mod that shows per-player damage totals for each combat round.

## Build

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_ROOT_ARM64="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
dotnet build /Users/canal/.t3/worktrees/Playground/t3code-b7649c27/sts2-damage-by-round/Sts2DamageByRound.csproj -c Release
```

## Install on macOS

Copy these into:

`/Users/canal/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/DamageByRoundOverlay/`

- `bin/Release/net8.0/Sts2DamageByRound.dll`
- `mod_manifest.json`
