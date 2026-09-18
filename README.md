# GHAudioControl

**Choose which animal sounds you hear, one animal at a time.**

The jungle never stops: howler monkeys, macaws, screeches, frogs, every creature near you calling on
its own timer, and no way in the game to turn any single one down. This mod gives each one a switch.

Press **K** in game. Two tabs:

- **Ambient** — the background animal layer. No animal is there; the game plays a random clip from a
  list, somewhere in the trees, on a timer. One row per clip, by its name.
- **True animal voice** — the idle calls of real creatures near you. One row per species, with its
  icon. Attack, panic and death sounds always play: those are warnings.

Each row is an icon, a name and a radio switch. Click the row to flip it. A master switch at the top
of each tab silences the whole layer.

Nothing in the game names these sounds for the player, so while you decide, a small line on screen
names each one as it plays — *Ambient: howler_monkey_03*, *Tapir: idle call*. Switch it off in the
panel once you are done choosing.

## Install

Needs [BepInEx 5](https://github.com/BepInEx/BepInEx) (x64). Unzip and put the `BepInEx` folder over
your game's. The `icons` folder must stay beside the DLL.

## Settings

`BepInEx/config/com.mohammadkoush.ghaudiocontrol.cfg`, written on first run. The panel writes it; you
can edit it by hand too.

| | |
|---|---|
| `Panel.OpenKey` | K by default |
| `Mute.AmbientClips` | clip names switched off |
| `Mute.AnimalVoices` | species switched off |
| `Mute.AllAmbient` / `Mute.AllAnimalVoices` | the master switches |
| `Panel.ShowNamesOnScreen` / `ShowNamesSeconds` | the name-as-it-plays line |

The ambient clip names are read from the game at load and listed once in `BepInEx/LogOutput.log`.

## Build

`powershell -ExecutionPolicy Bypass -File build.ps1` — stock .NET Framework `csc.exe`, references
from the game install. `-NoDeploy` builds without installing.

Icons are from [Field Notes](https://github.com/mohammadkoush/green-hell-field-notes), the same set,
so a tapir is the same tapir in both. MIT licensed.
