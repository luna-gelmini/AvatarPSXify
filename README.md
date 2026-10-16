# ps1ify

Turns a VRChat avatar into a cute PS1-style one: chunky low-poly shapes, flat-shaded facets and
small dithered, pixel-crisp textures. It builds a Quest copy and a PC copy and never changes the
original.

It is built on [questkit](https://github.com/luna-gelmini/QuestKit), which does the mesh
reduction, atlasing, material conversion and animation fixes.

## Requirements

- Unity 2022.3 with the VRChat Avatars SDK 3.7 or newer
- [questkit](https://github.com/luna-gelmini/QuestKit) (VCC installs it automatically)

## Install

- **VRChat Creator Companion (recommended):** open https://vpm.ladyluh.dev/ and press
  **Add to VCC** (or add `https://vpm.ladyluh.dev/index.json` under Settings →
  Packages), then add **ps1ify** to your project. VCC keeps it updated. questkit comes along automatically.
- **.unitypackage:** download `QuestKit-<version>.unitypackage` and `PS1ify-<version>.unitypackage`
  from the two repos' Releases and drag both into Unity.
- **Git URL:** in Window → Package Manager → **+** → *Add package from git URL*, add
  `https://github.com/luna-gelmini/QuestKit.git` first, then
  `https://github.com/luna-gelmini/AvatarPSXify.git`.

## Use

1. Open **Tools > PS1-ify** and pick the avatar.
2. Set the triangle target (19k by default, Quest's Poor limit is 20k) and the facet strength.
3. Press **Build**. You get `<Name>_PS1` for Quest and `<Name>_PS1_PC` for PC. Upload both to the
   same blueprint ID.

To change individual choices (which PhysBones stay, which materials become what), set them in
questkit's **Quest Planner** on the same avatar first; ps1ify keeps them.

## What it does

- Reduces triangles harder than a normal Quest conversion. The face and every vertex a shape key
  moves are left alone, so expressions and visemes keep working, and some avatars stop above the
  target because of that.
- Bakes flat-shaded facets into the textures, lit from a fixed direction with warm shadows, so
  they show under any lighting. Normals stay smooth, which keeps colours from going grey. Faces
  and eyes get no facets.
- Packs materials into atlases, then turns every texture into 15-bit colour with a 4x4 Bayer
  dither, point-filtered without mipmaps. Quest textures are 256 per material (atlases grow with
  what they hold, faces get double); the PC copy gets twice that.
- Paints glow into the colour and gives faces the softest shading ramp.

## License

MIT
