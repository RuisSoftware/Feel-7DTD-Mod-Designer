# Feel 7DTD Mod Designer
A Unity editor toolkit to **create, inspect, and ship** mods for *7 Days to Die (7DTD)*.  
> This project is under active development. **Always back up your mods** before editing.

---

## What it does
- **All-in-one editor for XML entries**  
  View and edit icons, translations, and XML properties for items/blocks in a single place.
- **Recipe validation**  
  Highlights `recipes.xml` entries that reference **non-existing items**.
- **One-click asset bundling**  
  Automatically rebuilds your mod’s prefabs into a new `.unity3d` AssetBundle.
- **Conflict-aware merging**  
  See when multiple mods edit the same entry and pick which change should win.
- **Quick prefab icon screenshots**  
  Generate **transparent PNG** icons for your custom prefabs.
- **7DTD libraries importer**  
  Adds the correct *7 Days to Die* references to your Unity project automatically.

---

## Screenshots from version alpha 0.1
**Mod Designer (Patches)**
  
![Mod Designer – Patches](https://github.com/RuisSoftware/Feel-7DTD-Mod-Designer/blob/main/Screenshots/Mod%20Designer%20Patches.png?raw=true)

**Mod Designer (Append)**

![Mod Designer – Append](https://github.com/RuisSoftware/Feel-7DTD-Mod-Designer/blob/main/Screenshots/Mod%20Designer%20Append.png?raw=true)

**Mod Merger**

![Mod Merger](https://github.com/RuisSoftware/Feel-7DTD-Mod-Designer/blob/main/Screenshots/Mod%20Merger.png?raw=true)

**Prefab Screenshotter**

![Prefab Screenshotter](https://github.com/RuisSoftware/Feel-7DTD-Mod-Designer/blob/main/Screenshots/Prefab%20Screenshotter.png?raw=true)

**7DTD Libs Importer**

![7DTD Libs Importer](https://github.com/RuisSoftware/Feel-7DTD-Mod-Designer/blob/main/Screenshots/7DTD%20Libs%20Importer.png?raw=true)

---

## Requirements
- **Unity Editor** (use the *same major/minor version* as your installed 7DTD)
- *7 Days to Die* installed (you’ll point the tool at your `Data/Config` folder)

> **Which Unity version do I need?**  
> Right-click `7DaysToDie.exe` → **Properties** → **Details** → *Product version* (e.g. `2022.3.62f2`).  
> Install **Unity 2022.3.x** that matches your game’s number.

---

## Quick Start
1. **Clone** this repository and **open the folder** as a Unity project.
2. In Unity: **Tools → Feel 7DTD → Mod Designer**.
3. Set:
   - **Game Config Folder** → your `…/7 Days To Die/Data/Config`
   - **Root Mods Folder** → the folder where your mods live
   - *(Optional)* **Export Mods Folder** → where exports should be copied
4. Start editing. Use **Save** / **Save All** to persist, and **Export** to package.

![How to use](https://github.com/RuisSoftware/Feel-7DTD-Mod-Designer/blob/main/Screenshots/How%20to%20use.png?raw=true)

---

## Modules (overview)
- **Mod Designer**
  - Central place to edit items/blocks, properties, groups, and translations
  - Preview or auto-generate icons for entries
  - Auto-bundle prefabs into `.unity3d`
- **Mod Merger**
  - Detects conflicting edits (e.g., two mods editing `block name="concreteWall"`)
  - Lets you choose the winning change
- **Prefab Screenshotter**
  - Generates **transparent** PNGs for your prefabs (great for item atlases)
- **7DTD Libs Importer**
  - Adds the required 7DTD assemblies to your project

---

## Tips & Notes
- Icon PNGs are saved to your mod at: `XML/UIAtlases/ItemIconAtlas/`
- When in doubt, **back up** your mod folder before large edits or merges.
- If the Unity version doesn’t match your game, references and bundles may fail.

---

## To-do
- Version control for mods you create. (local or with Github API?)
- Support for in-scene prefab creating with automatic XML generation.
- Support for animations and audio on custom prefabs
- Support for Mac and Linux, currently only tested on Windows
- Create a wiki and add more intuïtive tooltips
- Expand the support for specific Patches XML properties

---

## Known Issues
- This is still in development. If you hit crashes or odd behavior, please open an issue.
- Include logs, Unity version, game version, and steps to reproduce.

**Report issues:** use the GitHub “Issues” tab with a clear title & repro steps.

---

## Contributing
PRs and suggestions are welcome!
Spread the word! Help more modders and server owners find this tool—**share the repo** with your community!
