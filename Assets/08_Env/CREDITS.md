# 08_Env Asset Credits

All assets below are **CC0 (Creative Commons Zero / Public Domain)**. Attribution is
not legally required, but sources are recorded here for provenance. Downloaded
2026-07-10 for the Lost Ark-style boss raid arena environment.

---

## 1. Floor Texture — ambientCG

- **Pack:** PavingStones131 (2K, JPG)
- **Source:** https://ambientcg.com/view?id=PavingStones131
- **Direct link:** https://ambientcg.com/get?file=PavingStones131_2K-JPG.zip
- **License:** CC0 1.0 — https://docs.ambientcg.com/books/website/page/the-cc0-license
- **Location:** `Textures/Floor/`
- **Maps extracted:** Color, Normal (OpenGL), Roughness, Ambient Occlusion
  (Displacement, DX normal, USD/blend/preview files were discarded.)

## 2. Environment Models — Kenney

- **Pack:** Graveyard Kit (version 5.0)
- **Source:** https://kenney.nl/assets/graveyard-kit
- **Direct link:** https://kenney.nl/media/pages/assets/graveyard-kit/ba8d4b4517-1760691807/kenney_graveyard-kit_5.0.zip
- **License:** CC0 1.0 — http://creativecommons.org/publicdomain/zero/1.0/
- **Location:** `Models/` (FBX format) + shared texture `Models/Textures/colormap.png`
- **Selected FBX (14):** pillar-large, pillar-small, pillar-square, pillar-obelisk,
  column-large, stone-wall, stone-wall-column, stone-wall-damaged, brick-wall,
  rocks, rocks-tall, debris, altar-stone, fire-basket
  (Full 90-asset pack trimmed to raid-hall-relevant pillars/columns/walls/rocks/props.
  All FBX share the single low-res `colormap.png` palette texture.)

## 3. Sky HDRI — Poly Haven

- **Pack:** Moonless Golf (2K HDRI)
- **Source:** https://polyhaven.com/a/moonless_golf
- **Direct link:** https://dl.polyhaven.org/file/ph-assets/HDRIs/hdr/2k/moonless_golf_2k.hdr
- **License:** CC0 1.0 — https://polyhaven.com/license
- **Location:** `Sky/moonless_golf_2k.hdr`
- **Note:** Dark, moody night sky suited to a dim raid hall.

---

# 09_Characters Asset Credits

Rigged + animated FBX characters/monster for the boss-raid roles (boss / dealer /
tank / healer / supporter). All **CC0 1.0**. Downloaded 2026-07-10. Both packs by
**@Quaternius** (https://quaternius.com), pulled from the official Google Drive
distribution folders. Every FBX is a binary Kaydara FBX with embedded animation
takes on a shared `CharacterArmature` humanoid rig — no separate animation library
needed. Textures are **external PNGs**: each party FBX carries materials *named
identically* to its PNG (e.g. material `Warrior_Texture` ↔ `Warrior_Texture.png`),
so Unity imports untextured materials and the matching PNG (placed alongside) must
be assigned to the material's Base Map. The boss FBX references `Atlas_Monsters.png`
by relative filename.

## 4. Boss Monster — Quaternius "Ultimate Monsters"

- **Pack:** Ultimate Monsters (Oct 2022)
- **Source:** https://quaternius.com/packs/ultimatemonsters.html
- **License:** CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/
- **Location:** `Assets/09_Characters/Boss/`
- **Selected FBX (1):** `Demon.fbx` (from pack's `Big/FBX/`) — large demon/beast,
  used as boss "혈월의 마수 군주" (Valtan-style beast lord).
- **Animation takes (14, AnimationStack-verified):** Idle, Walk, Run, Punch (attack),
  Death, HitReact, Jump, Jump_Idle, Jump_Land, Duck, Wave, Weapon, Yes, No.
- **Texture:** shared low-res palette atlas `Atlas_Monsters.png` (external, referenced
  by the FBX's single `Atlas` material). Other monster meshes / other formats discarded.

## 5. Party Characters — Quaternius "RPG Characters"

- **Pack:** RPG Characters
- **Source:** https://quaternius.com/packs/rpgcharacters.html
- **License:** CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/
- **Location:** `Assets/09_Characters/Party/`
- **Selected FBX (4 of 6; Ranger + Monk unused):**
  - `Warrior.fbx` → **Tank (기사/방패 역할)** — armored swordsman. Takes (14): Idle,
    Walk, Run, Sword_Attack, Sword_AttackFast, Death, RecieveHit ×2, Roll, PickUp,
    Punch, Idle_Attacking, Idle_Weapon, Run_Weapon.
  - `Rogue.fbx` → **Dealer (전사/로그 역할)** — dagger DPS. Takes (12): Idle, Walk,
    Run, Dagger_Attack, Dagger_Attack2, Death, RecieveHit ×2, Roll, PickUp, Punch,
    Attacking_Idle.
  - `Cleric.fbx` → **Healer (사제 역할)** — staff caster. Takes (15): Idle, Walk, Run,
    Staff_Attack, Spell1, Spell2 (cast), Death, RecieveHit ×2, Roll, PickUp, Punch,
    Idle_Weapon, Run_Weapon, Attack_Idle.
  - `Wizard.fbx` → **Supporter (마법사 역할)** — staff caster. Takes (15): Idle, Walk,
    Run, Staff_Attack, Spell1, Spell2 (cast), Death, RecieveHit ×2, Roll, PickUp,
    Punch, Idle_Attacking, Idle_Weapon, Run_Weapon.
- **Textures (external PNG, 2 per character — body + weapon):**
  `Warrior_Texture.png` + `Warrior_Sword_Texture.png`,
  `Rogue_Texture.png` + `Rogue_Dagger_Texture.png`,
  `Cleric_Texture.png` + `Cleric_Staff_Texture.png`,
  `Wizard_Texture.png` + `Wizard_Staff_Texture.png`.
- **Note:** All 4 share the `CharacterArmature` rig so a single Humanoid avatar mask /
  animator setup can be reused across the party. Kenney animated-character packs were
  evaluated but their asset pages returned errors; the modular Quaternius Knight pack
  was skipped (separate helmet/weapon pieces, no shield mesh) in favour of the
  self-contained, art-consistent RPG Characters set.
