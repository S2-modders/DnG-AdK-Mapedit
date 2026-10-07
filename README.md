# The Settlers - Rise of Cultures map creation tool

Special thanks to [J4n1X](https://github.com/J4n1X) for help with adding harbour support.

This tool allows to create maps for The Settlers - Rise of Cultures using the map editor from The Settlers II - 10th Anniversary (It can't edit Rise of Cultures maps).
Maps can be published on the discord server: https://discord.gg/UAXH3VS9Qy

Multiplayer maps pack can be found here: https://www.moddb.com/games/the-settlers-rise-of-cultures/addons/sadk-multiplayer-maps-pack

## Most important changes compared to the old map converter from 2009:
- Maps no longer crash randomly during gameplay.
- Support for maps with odd player counts was added.
- Harbour support was added.
- Caves section now works properly.
- Swapping was added to allow using new assets.
- Whole map preset can now be saved not requiring inputting values manually with each map edit.

## How to use:
### Map info tab
<img width="1266" height="647" alt="Zrzut ekranu_20260926_141818" src="https://github.com/user-attachments/assets/1d2ebd1b-d34f-44e4-bd88-f3e64e675b1e" />
Displays information about the map, most importantly the resources section.

- Clicking the "share" text reveals the recommended proportions of resources present on the map.
- Amount of salt present on the map must be larger than gold.
- "Map name" is used when generating save-game names and loading the environment files. It can be changed by clicking it.
- Preview render (`.bmp` file) must be present in the same directory as the exported map and have the same name otherwise crashing the game.

### Resources tab
<img width="1266" height="647" alt="Zrzut ekranu_20260926_141850" src="https://github.com/user-attachments/assets/7fdce28b-8a52-4d1b-bc18-41b300948eb0" />
Here you can add salt and gemstones to the map.

- Select one resource in every list and click "swap".
- Following that the map has to be saved by clicking "Continue editing" and loaded in the map editor to add missing resources.
- Resources only present in Rise of Cultures will not be visible. To prevent accidental overwrites disable "Enable overwrites".

### Swapping

Swapping allows to use new assets by replacing ones accessible in the 10th Anniversary map editor.

[This file](https://www.moddb.com/games/the-settlers-ii-10th-anniversary/downloads/hidden-in-editor) unlocks textures and objects that may be useful for swapping and map creation.

- Like in the resources tab select one object in every list on the specific tab and click "swap".
- Non-blocking doodads lay on a separate, denser grid than the rest of the objects. That's why they are listed separately.
- Replacing specific tree types or mine-able stones with caves spawning animals is an alternative option for adding them to inputting coordinates manually. They are on the bottom on the list and contain the word "spawn".
- Swaps will be executed from top to the bottom of the list.
- If a map is meant to mostly use highland or snow textures "highland" water shader can be applied.

### Harbour (currently disabled) and animal caves tabs
<img width="1266" height="647" alt="Zrzut ekranu_20260926_141932" src="https://github.com/user-attachments/assets/7711a335-da03-435e-af9d-4becf07d35f5" />

- Clicking the remove button in both harbours and animal caves will remove the currently displayed item in the list.
- All fields allowing to enter coordinates use the logical grid. Map editor displays the detailed grid coordinates by default. To convert from detailed to logical coordinates divide them by 4 and remove the decimal component or switch the status-bar to show logical coordinates (`Tools` -> `Statusbar` -> `Logical`)
- Harbours can be connected in an infinite chain like in mission 10 or form any polygon like in mission 11. Double-ship connections are also possible doubling the capacity and allowing for 2 sea attacks instead of just 1.

### Environment tab
<img width="1266" height="647" alt="Zrzut ekranu_20260926_142005" src="https://github.com/user-attachments/assets/88fc00e1-82bb-4ca6-b0ff-346beff0692c" />

- Global preset dictates sky texture and sun or moon placement.
- Sky textures have a very high impact on ice and water colours.
- To ensure a compatibility with different aspect ratios fog start distance should be set to 200 and full fog distance to 300.
- Manually created presets may run into oversaturation problems. If they target a grassland environment green channel value should be lowered.
- It's recommended to use a combination of included presets.

Maps using custom environment files have to be placed in `game folder/game/data/maps/freegamemaps` directory.

### Players tab

Default colours were designed for multiplayer maps with clockwise or counter-clockwise start positions placement. In case of 2 and 4-player maps start positions placement does not matter.

### Sacrifices tab

Most maps should use one of the included presets in the export section

### Export tab
<img width="1266" height="647" alt="Zrzut ekranu_20260926_142035" src="https://github.com/user-attachments/assets/1cc1a0cb-4371-473c-ad42-2b4a1ef5b8d5" />

- If "Use an included preset" checkbox is checked the "load" button will load the currently selected sacrifice preset.
- "Map presets" create a backup of export settings except resource swapping.
- If the map preview is not present in the same directory under the same name as the map file the game will crash.
- Multiplayer maps have to be placed in `game folder/game/data/maps/freegamemaps` directory and have it's file name start with the `MP_` prefix.
- Scenarios can be created by adding `.lua` files.
