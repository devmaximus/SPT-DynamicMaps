# SPT-DynamicMaps

In-raid interactive map with real-time markers for bots, containers, extracts, and more. Fork of [acidphantasm/SPT-DynamicMaps](https://github.com/acidphantasm/SPT-DynamicMaps).

## Fork changes (devmaximus — implement/weapon-container-markers)

### Container gear markers
- Weapon (orange), armor (blue), and misc (magenta) loot pins on any `LootableContainer` with in-memory inventory
- Multi-type support: containers with both weapon + armor show offset paired markers
- Per-category F12 toggles: `Show Container Weapons/Armor/Misc In Raid`
- Stash/barrel XOR: hidden stash barrels show stash icon only, not gear pins

### Bot marker improvements
- PMC/scav/boss role hardening — handles late Role/Side assignment races
- Configurable marker colors: dark-orange PMC, gray scav, red boss, separate boss-support
- Reclassify markers while map is open (throttled refresh)
- Agro flash arrows — blink when AI is actively targeting you (F12 toggle)
- Corpse skull tint by type (PMC/scav/boss)

### Map viewport
- Fit full map to viewport on open (fixes tall maps like Ground Zero being cropped)
- Scroll-wheel zoom while peeking (was previously blocked during M-peek)

## Build

```powershell
dotnet build Plugin/DynamicMaps.csproj -c Release -p:TarkovDir="D:\Games\EscapeFromTarkov\"
```

PostBuild copies DLL + Resources + LICENSE to `BepInEx\plugins\DynamicMaps\`.

## Requirements

- SPT 4.0.13+
- BepInEx 5.x
