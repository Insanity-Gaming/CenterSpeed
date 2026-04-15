# CenterSpeed

**CenterSpeed** is a ModSharp plugin for Counter-Strike 2 that displays the player’s current speed in the center of their screen using particles.

---

## Features

- Real-time speed display (2D velocity)
- Particle-based digit HUD
- Per-player customization
- Persistent settings (ClientPreferences support)
- Only visible to the owning player

---

## ConVars

| Name | Default | Description |
|------|--------|------------|
| ms_cspeed_particle | particles/digits_x/digits_x.vpcf | Particle used for digits |

---

## Commands

### Toggle HUD
!hudsettings toggle

### View Settings
!hudsettings info

### Set Digit Offset
!hudsettings offset <1-4> <-10 to 10>

### Set Scale
!hudsettings scale <0-10>

### Set Y Offset
!hudsettings yoffset <-10 to 10>

---

## Installation

1. Build for net10.0
2. Place in:
   game/sharp/modules/CenterSpeed/
3. Restart server or reload modules

---

## Notes

- HUD is only shown to the player
- Automatically spawns on player spawn
- Settings are saved per player (if ClientPreferences is installed)

---

## Authors

- Lethal  
- Retro  
