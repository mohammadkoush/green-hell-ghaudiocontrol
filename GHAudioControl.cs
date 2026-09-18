// GHAudioControl - choose which animal sounds you hear, one animal at a time.
//
// THE PROBLEM. The jungle never stops: howler monkeys, macaws, screeches, frogs, every animal near
// you calling on its own timer. There is no way in the game to turn any single one down. It gets
// overwhelming.
//
// TWO SOURCES, read out of the game's code rather than assumed, because they need different switches:
//
//   AMBIENT - AmbientAudioSystem.UpdateAnimalsSound(). No animal exists. On a timer it picks an
//             AmbientSounds.AmbientDefinition (day or night list), sets one AudioSource to that
//             definition's clip, places it somewhere in the trees, and plays. The only name such a
//             sound has is its clip name. Muted here by a postfix on SelectAmbientDefinition that
//             hands back null for a muted clip - the caller null-checks and skips the play.
//
//   TRUE ANIMAL VOICE - AIs.AISoundModule.PlayIdleSound(). A real creature near you, calling on
//             m_IdleSoundNextTime. Muted per species by a prefix that returns false when the
//             module's AI id is switched off. Attack, panic and death sounds are left alone: those
//             are warnings, and a silent jaguar is not quality of life.
//
// Nothing in the game names these sounds for the player, so while you decide, a small line on the
// screen names each one as it plays: "Ambient: howler_monkey_03", "Tapir: idle". Off when you are done.
//
// JUNGLE - everything that is not an animal: the rainforest bed (one multi-sample the game mixes
//          from named layers - rain, wind, water, the day and night beds), the rain manager's own
//          source with its thunder, and the loose emitters placed in the world (rivers, waterfalls,
//          a PlayRadomSound each - the game's spelling). Muted by AudioSource.mute, which is
//          reversible and leaves the game's own volume curves untouched. Rows are discovered from
//          the running game, so the list is whatever this version of the game actually plays.
//
// The panel is the game-window style: K opens it, three tabs, an icon and a radio switch per row.
// Icons are the same set Field Notes draws its map with, so a tapir looks the same in both mods.
//
// Language level is C# 5 (stock Framework csc.exe) - no ?., no $"", no ??=.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GHAudioControl
{
    [BepInPlugin(Guid, Name, Version)]
    public class GHAudioControlPlugin : BaseUnityPlugin
    {
        public const string Guid    = "com.mohammadkoush.ghaudiocontrol";
        public const string Name    = "GHAudioControl";
        public const string Version = "1.8.0";

        private static GHAudioControlPlugin s_Self;

        // ---- config ------------------------------------------------------------------------------
        private ConfigEntry<KeyboardShortcut> _key;
        private ConfigEntry<string> _mutedAmbient;
        private ConfigEntry<string> _mutedAnimals;
        private ConfigEntry<string> _mutedJungle;
        private ConfigEntry<bool>   _showNames;
        private ConfigEntry<float>  _nameSeconds;
        private ConfigEntry<string> _windowPos;
        private ConfigEntry<string> _namePos;
        private Rect _nameRect = new Rect(-1f, -1f, 320f, 34f);

        private readonly HashSet<string> _mutedAmbientSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mutedAnimalSet  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mutedJungleSet  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Jungle rows: a name -> the sources that play it. Rebuilt on a slow timer, because emitters
        // stream in and out with the world.
        private readonly Dictionary<string, List<AudioSource>> _jungle = new Dictionary<string, List<AudioSource>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _jungleNames = new List<string>();
        private readonly HashSet<AudioSource> _weMuted = new HashSet<AudioSource>();
        private float _jungleAt;

        // ---- what exists -------------------------------------------------------------------------
        private readonly List<string> _ambientNames = new List<string>();   // clip names, discovered
        private readonly List<string> _animalNames  = new List<string>();   // AIID names, curated
        private bool _ambientDiscovered;

        // ---- now playing -------------------------------------------------------------------------
        private static string s_NowPlaying = "";
        private static float  s_NowPlayingUntil;

        // ---- window ------------------------------------------------------------------------------
        private bool _open;
        private int  _tab;                       // 0 Ambient, 1 True animal voice
        private Rect _rect = new Rect(200f, 120f, 560f, 620f);
        private Vector2 _scroll;
        private GUIStyle _title, _row, _tabOn, _tabOff, _dim, _rowBtn, _rowBtnDim, _nameStyle;
        private bool _styled;
        private Texture2D _radioOn, _radioOff, _pixel, _cross;
        private readonly Dictionary<string, Texture2D> _icons = new Dictionary<string, Texture2D>();
        private string _iconDir;

        private void Awake()
        {
            s_Self = this;

            _key = Config.Bind("Panel", "OpenKey", new KeyboardShortcut(KeyCode.K),
                "Opens and closes the sound panel. No other mod's config on this machine binds K; " +
                "a mod with hardcoded keys cannot be seen, so change it here if one collides.");
            _mutedAmbient = Config.Bind("Mute", "AmbientClips", "",
                "Ambient clips switched OFF, by clip name, comma separated. Written by the panel; " +
                "edit by hand if you like. Names are discovered from the game at load and listed " +
                "in the log.");
            _mutedAnimals = Config.Bind("Mute", "AnimalVoices", "",
                "Species whose idle calls are switched OFF, by AI name (Tapir, Capybara, " +
                "GoldenLionTamarin...), comma separated. Attack, panic and death sounds always play.");
            _mutedJungle = Config.Bind("Mute", "Jungle", "",
                "Jungle layers switched OFF, by name, comma separated: the rainforest bed's layers " +
                "(rain, wind, water, day, night...), 'Rain and thunder', and world emitters by clip " +
                "name (rivers, waterfalls). Written by the panel.");
            _showNames = Config.Bind("Panel", "ShowNamesOnScreen", true,
                "Name each sound on screen as it plays, so you can decide what to switch off. " +
                "Turn it off once you are done choosing.");
            _nameSeconds = Config.Bind("Panel", "ShowNamesSeconds", 4f,
                new ConfigDescription("How long each name stays on screen.",
                    new AcceptableValueRange<float>(1f, 15f)));
            _windowPos = Config.Bind("Panel", "WindowPosition", "",
                "Where the panel was last dragged. Written automatically.");
            _fix2D = Config.Bind("Voices", "Make2DVoices3D", true,
                "A creature whose voice is set up as 2D plays at full volume wherever it is - the " +
                "centipede that seems to follow you. On: such a voice is made 3D so it fades with " +
                "distance. The log names each species and its settings the first time it calls.");
            _critterRange = Config.Bind("Voices", "CritterRangeMetres", 8f,
                new ConfigDescription("How far a critter - centipede, scorpion, spider, beetle, frog, " +
                    "mouse, crab - can be heard. His words: \"this is a critter, not a bat.\" A " +
                    "slider in the K panel, True animal voice tab.",
                    new AcceptableValueRange<float>(2f, 60f)));
            _critterVolume = Config.Bind("Voices", "CritterVolumePercent", 50f,
                new ConfigDescription("How loud critters are, as a share of the game's own level. " +
                    "A slider in the K panel, True animal voice tab.",
                    new AcceptableValueRange<float>(0f, 100f)));
            _critters = Config.Bind("Voices", "CritterSpecies",
                "Centipede, Scorpion, GoliathBirdEater, BrasilianWanderingSpider, Caterpillar, Beetle, " +
                "Mouse, PoisonDartFrog, CaneToad, Crab, Prawn",
                "Which species count as critters for the short range. AI names, comma separated.");
            _voiceRange = Config.Bind("Voices", "VoiceRangeMetres", 20f,
                new ConfigDescription("How far a repaired voice carries before it is silent.",
                    new AcceptableValueRange<float>(5f, 200f)));
            _logPlaying = Config.Bind("Diagnostics", "LogEveryPlayingClip", true,
                "Write one log line the first time each clip is heard playing - the object it is " +
                "on, its parent, its volume. This is how a sound that will not mute names itself.");
            _namePos = Config.Bind("Panel", "NamePosition", "",
                "Where the name-of-the-sound line was last dragged. Drag it anywhere. Written " +
                "automatically.");

            ParseLists();
            LoadWindowPos();
            BuildAnimalList();

            _iconDir = Path.Combine(Path.GetDirectoryName(Info.Location), "icons");

            try
            {
                Harmony h = new Harmony(Guid);
                h.PatchAll(typeof(GHAudioControlPlugin).Assembly);
                Logger.LogInfo(Name + " " + Version + " loaded. " + _key.Value.MainKey + " opens the panel.");
            }
            catch (Exception ex)
            {
                Logger.LogError("could not patch the game's sound code: " + ex.Message);
            }
        }

        // -----------------------------------------------------------------------------------------
        // Lists
        // -----------------------------------------------------------------------------------------

        private void ParseLists()
        {
            _mutedAmbientSet.Clear();
            foreach (string s in (_mutedAmbient.Value ?? "").Split(','))
                if (s.Trim().Length > 0) _mutedAmbientSet.Add(s.Trim());
            _mutedAnimalSet.Clear();
            foreach (string s in (_mutedAnimals.Value ?? "").Split(','))
                if (s.Trim().Length > 0) _mutedAnimalSet.Add(s.Trim());
            _mutedJungleSet.Clear();
            foreach (string s in (_mutedJungle.Value ?? "").Split(','))
            {
                string k = s.Trim();
                // 1.1/1.2 keyed these as "Bed: name" and "World: name"; rows are clip names now.
                if (k.StartsWith("Bed: ", StringComparison.OrdinalIgnoreCase)) k = k.Substring(5);
                else if (k.StartsWith("World: ", StringComparison.OrdinalIgnoreCase)) k = k.Substring(7);
                if (k.Length > 0) _mutedJungleSet.Add(k);
            }
        }

        private void SaveLists()
        {
            _mutedAmbient.Value = string.Join(", ", new List<string>(_mutedAmbientSet).ToArray());
            _mutedAnimals.Value = string.Join(", ", new List<string>(_mutedAnimalSet).ToArray());
            _mutedJungle.Value  = string.Join(", ", new List<string>(_mutedJungleSet).ToArray());
        }

        // -----------------------------------------------------------------------------------------
        // Jungle: discover, then mute by name
        // -----------------------------------------------------------------------------------------

        private static FieldInfo s_AmbientMS, s_SampleSource, s_RainSource;

        // EVIDENCE FROM HIS SECOND SESSION: every row flipped, the config filled up, the log wrote
        // "jungle: muted 'Bed: amb_wind_light_01' (MS_amb_wind_light_01)" for eleven layers and
        // "Rain and thunder" - and nothing went quiet. So the sources this was muting are not the
        // ones he hears. The named multi-sample objects may be templates the game copies from, or
        // the bed may play through sources this never found. Either way: stop assuming which
        // object plays and go by what IS playing. Every AudioSource in the scene that is playing a
        // clip right now is a row, by clip name, and muting a row mutes every source playing that
        // clip, every tick. What he hears is by definition on that list.
        private readonly HashSet<string> _seenPlaying = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ConfigEntry<bool> _logPlaying;

        private void JungleTick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _jungleAt < 2f) return;
            _jungleAt = now;

            try
            {
                _jungle.Clear();

                // 0. Everything that is playing right now, by clip. AI voices and the ambient
                //    animal source have their own tabs and are left out of this one.
                AudioSource[] live = UnityEngine.Object.FindObjectsOfType<AudioSource>();
                AudioSource animalSrc = AnimalSource();
                for (int i = 0; i < live.Length; i++)
                {
                    AudioSource a = live[i];
                    if (a == null || a.clip == null) continue;
                    if (!a.isPlaying && !_weMuted.Contains(a)) continue;      // a muted one stops "playing"
                    if (a == animalSrc) continue;
                    AIs.AI owner = a.GetComponentInParent<AIs.AI>();
                    if (owner != null) { CheckCreatureSource(owner.m_ID.ToString(), a); continue; }

                    // A CREATURE BY NAME. The centipede's crawl loop sits on "Centipede(Clone)" with
                    // no AI component above it - the game cannot even load that creature's sound
                    // script - so the component test skipped it twice. An object named for a species
                    // is a creature whatever it is made of.
                    string species = SpeciesFromName(a.gameObject.name);
                    if (species == null && a.transform.parent != null) species = SpeciesFromName(a.transform.parent.name);
                    if (species != null) { CheckCreatureSource(species, a); continue; }

                    if (a.GetComponentInParent<Player>() != null) continue;    // his own footsteps and breath
                    string nm = a.clip.name;
                    Add(nm, a);
                    if (_seenPlaying.Add(nm) && _logPlaying != null && _logPlaying.Value)
                        Logger.LogInfo("playing: '" + nm + "' on " + a.gameObject.name
                            + (a.transform.parent != null ? " under " + a.transform.parent.name : "")
                            + " vol=" + a.volume.ToString("F2") + " loop=" + a.loop
                            + " blend=" + a.spatialBlend.ToString("F2") + " max=" + a.maxDistance.ToString("F0"));
                }

                // 1. The rainforest bed: one multi-sample, its layers named by wav.
                AmbientAudioSystem sys = AmbientAudioSystem.Instance;
                if (sys != null)
                {
                    if (s_AmbientMS == null) s_AmbientMS = AccessTools.Field(typeof(AmbientAudioSystem), "m_AmbientMS");
                    MSMultiSample ms = (s_AmbientMS != null) ? s_AmbientMS.GetValue(sys) as MSMultiSample : null;
                    if (ms != null && ms.m_Samples != null)
                    {
                        if (s_SampleSource == null) s_SampleSource = AccessTools.Field(typeof(MSSample), "m_AudioSource");
                        for (int i = 0; i < ms.m_Samples.Count; i++)
                        {
                            MSSample smp = ms.m_Samples[i];
                            if (smp == null || s_SampleSource == null) continue;
                            AudioSource src = s_SampleSource.GetValue(smp) as AudioSource;
                            if (src == null) continue;
                            // Same key as the live census uses - the clip name - so a bed layer
                            // that is playing and its template are one row, not two.
                            string nm = (src.clip != null) ? src.clip.name
                                      : (string.IsNullOrEmpty(smp.m_WavName) ? ("bed layer " + i) : smp.m_WavName);
                            Add(nm, src);
                        }
                    }
                }

                // 2. Rain and thunder: the rain manager's own source.
                RainManager rm = RainManager.Get();
                if (rm != null)
                {
                    if (s_RainSource == null) s_RainSource = AccessTools.Field(typeof(RainManager), "m_AudioSource");
                    AudioSource src = (s_RainSource != null) ? s_RainSource.GetValue(rm) as AudioSource : null;
                    if (src != null) Add(src.clip != null ? src.clip.name : "Rain and thunder", src);
                }

                // 3. Emitters placed in the world - rivers, waterfalls, whatever the level author put
                //    down. Named by their first clip, which is what they actually sound like.
                PlayRadomSound[] all = UnityEngine.Object.FindObjectsOfType<PlayRadomSound>();
                for (int i = 0; i < all.Length; i++)
                {
                    PlayRadomSound e = all[i];
                    if (e == null || e.m_AudioSource == null) continue;
                    string nm = null;
                    if (e.m_Clips != null && e.m_Clips.Count > 0 && e.m_Clips[0] != null) nm = e.m_Clips[0].name;
                    if (nm == null && e.m_AudioSource.clip != null) nm = e.m_AudioSource.clip.name;
                    if (nm == null) nm = e.gameObject.name;
                    Add(nm, e.m_AudioSource);
                }

                ReapplyRanges();
                ReapplyVolumes();

                _jungleNames.Clear();
                _jungleNames.AddRange(_jungle.Keys);
                _jungleNames.Sort(StringComparer.OrdinalIgnoreCase);

                // Apply. Only sources this mod muted are ever un-muted, so a source the game itself
                // keeps silent stays the game's business.
                foreach (KeyValuePair<string, List<AudioSource>> kv in _jungle)
                {
                    bool mute = _mutedJungleSet.Contains(kv.Key);
                    for (int i = 0; i < kv.Value.Count; i++)
                    {
                        AudioSource src = kv.Value[i];
                        if (src == null) continue;
                        if (mute)
                        {
                            if (!src.mute)
                            {
                                src.mute = true;
                                if (!src.loop && src.isPlaying) src.Stop();   // a one-shot ends now, not at its tail
                                _weMuted.Add(src);
                                float took = (_clickAt > 0f) ? (Time.realtimeSinceStartup - _clickAt) : -1f;
                                Logger.LogInfo("jungle: muted '" + kv.Key + "' (" + src.gameObject.name + ")"
                                    + (took >= 0f ? "  " + took.ToString("F2") + "s after the click" : ""));
                            }
                        }
                        else if (_weMuted.Contains(src)) { src.mute = false; _weMuted.Remove(src); Logger.LogInfo("jungle: un-muted '" + kv.Key + "'"); }
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning("jungle: " + ex.Message); }
        }

        private static FieldInfo s_AnimalSrcFI;
        private static AudioSource AnimalSource()
        {
            try
            {
                AmbientAudioSystem sys = AmbientAudioSystem.Instance;
                if (sys == null) return null;
                if (s_AnimalSrcFI == null) s_AnimalSrcFI = AccessTools.Field(typeof(AmbientAudioSystem), "m_AnimalSoundsAudioSource");
                return (s_AnimalSrcFI != null) ? s_AnimalSrcFI.GetValue(sys) as AudioSource : null;
            }
            catch (Exception) { return null; }
        }

        private void Add(string name, AudioSource src)
        {
            List<AudioSource> l;
            if (!_jungle.TryGetValue(name, out l)) { l = new List<AudioSource>(); _jungle[name] = l; }
            l.Add(src);
        }

        /// <summary>
        /// Every real animal with a voice, from the game's own AIID enum - minus the arena copies,
        /// the quest one, the tribesmen (they use a different sound module and are not animals) and
        /// the bookkeeping values. Read from the enum at runtime, so a species the game adds later
        /// appears by itself.
        /// </summary>
        private void BuildAnimalList()
        {
            _animalNames.Clear();
            foreach (string n in Enum.GetNames(typeof(AIs.AI.AIID)))
            {
                if (n == "None" || n == "Count") continue;
                if (n.IndexOf("Arena", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (n.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase)) continue;
                if (n == "Regular" || n == "Hunter" || n == "Spearman" || n == "Thug" || n == "Savage"
                    || n == "Kid" || n == "KidRunner") continue;
                _animalNames.Add(n);
            }
            _animalNames.Sort(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The ambient clip names, read from the game's own lists the first time they exist.</summary>
        private void DiscoverAmbient()
        {
            if (_ambientDiscovered) return;
            try
            {
                AmbientAudioSystem sys = AmbientAudioSystem.Instance;
                if (sys == null) return;
                FieldInfo fi = AccessTools.Field(typeof(AmbientAudioSystem), "m_AmbientSounds");
                AmbientSounds snd = (fi != null) ? fi.GetValue(sys) as AmbientSounds : null;
                if (snd == null) return;

                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddClipNames(snd.m_AmbientDefinitionsDay, seen);
                AddClipNames(snd.m_AmbientDefinitionsNight, seen);
                _ambientNames.Clear();
                _ambientNames.AddRange(seen);
                _ambientNames.Sort(StringComparer.OrdinalIgnoreCase);
                _ambientDiscovered = true;
                Logger.LogInfo("ambient layer: " + _ambientNames.Count + " clip(s): "
                               + string.Join(", ", _ambientNames.ToArray()));
            }
            catch (Exception ex) { Logger.LogWarning("could not read the ambient list: " + ex.Message); }
        }

        private static void AddClipNames(List<AmbientSounds.AmbientDefinition> defs, HashSet<string> into)
        {
            if (defs == null) return;
            for (int i = 0; i < defs.Count; i++)
                if (defs[i] != null && defs[i].m_Clip != null) into.Add(defs[i].m_Clip.name);
        }

        // -----------------------------------------------------------------------------------------
        // The two decisions
        // -----------------------------------------------------------------------------------------

        private static bool AmbientMuted(string clipName)
        {
            if (s_Self == null || clipName == null) return false;
            return s_Self._mutedAmbientSet.Contains(clipName);
        }

        private static bool AnimalMuted(AIs.AI.AIID id)
        {
            if (s_Self == null) return false;
            return s_Self._mutedAnimalSet.Contains(id.ToString());
        }

        private static void NowPlaying(string text)
        {
            if (s_Self == null || !s_Self._showNames.Value) return;
            s_NowPlaying = text;
            s_NowPlayingUntil = Time.realtimeSinceStartup + s_Self._nameSeconds.Value;
        }

        // The ambient layer: a muted clip is handed back as "nothing to play", which the caller
        // already handles - it re-arms its timer and waits for the next pick.
        [HarmonyPatch(typeof(AmbientAudioSystem), "SelectAmbientDefinition")]
        private static class Patch_Ambient
        {
            private static void Postfix(ref AmbientSounds.AmbientDefinition __result)
            {
                try
                {
                    if (__result == null || __result.m_Clip == null) return;
                    string n = __result.m_Clip.name;
                    if (AmbientMuted(n)) { __result = null; return; }
                    NowPlaying("Ambient: " + n);
                }
                catch (Exception) { }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Voices that do not fade with distance
        // -----------------------------------------------------------------------------------------
        //
        // HIS REPORT: "The centipede sound does not fade away when we move away from that creature.
        // The audio stays at 100%, like the centipede moving with you. But I can see it far away.
        // So the audio is not calculating the distance."
        //
        // A Unity AudioSource only attenuates when its spatialBlend is 1 (3D). A source left at 0
        // plays in both ears at full volume wherever it is - which is exactly a centipede that
        // follows you. Whether that is the fault is READ, not assumed: the first time each species
        // plays, its source's spatialBlend, min and max distance and rolloff are written to the
        // log. Then, if the setting is on, any voice with spatialBlend below 1 is made 3D with a
        // sane range, and the log says so. The species stays in the log either way, so the report
        // that comes back names the numbers.
        private ConfigEntry<bool>  _fix2D;
        private ConfigEntry<float> _voiceRange;
        private ConfigEntry<float> _critterRange;
        private ConfigEntry<float> _critterVolume;
        // Every creature source seen, with the volume the game gave it - so a slider scales the
        // game's own level rather than replacing it, and 100% is exactly what shipped.
        private readonly Dictionary<AudioSource, float> _baseVolume = new Dictionary<AudioSource, float>();
        private readonly Dictionary<AudioSource, string> _creatureOf = new Dictionary<AudioSource, string>();

        private void NoteCreatureSource(string species, AudioSource src)
        {
            if (src == null) return;
            if (!_baseVolume.ContainsKey(src)) _baseVolume[src] = src.volume;
            _creatureOf[src] = species;
        }

        /// <summary>Critter volume, live: every critter source is held at game volume times the slider.</summary>
        private void ReapplyVolumes()
        {
            float pct = _critterVolume.Value / 100f;
            List<AudioSource> gone = null;
            foreach (KeyValuePair<AudioSource, string> kv in _creatureOf)
            {
                AudioSource a = kv.Key;
                if (a == null) { if (gone == null) gone = new List<AudioSource>(); gone.Add(a); continue; }
                if (!IsCritter(kv.Value)) continue;
                float baseV;
                if (!_baseVolume.TryGetValue(a, out baseV)) continue;
                float want = baseV * pct;
                if (Mathf.Abs(a.volume - want) > 0.005f) a.volume = want;
            }
            if (gone != null) foreach (AudioSource a in gone) { _creatureOf.Remove(a); _baseVolume.Remove(a); }
        }
        private ConfigEntry<string> _critters;
        private readonly Dictionary<AudioSource, string> _repaired = new Dictionary<AudioSource, string>();

        private bool IsCritter(string species)
        {
            foreach (string c in (_critters.Value ?? "").Split(','))
                if (string.Equals(c.Trim(), species, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private float RangeFor(string species)
        {
            return IsCritter(species) ? _critterRange.Value : _voiceRange.Value;
        }

        /// <summary>Sliders move live: every source this mod has repaired is re-ranged each sweep.</summary>
        private void ReapplyRanges()
        {
            List<AudioSource> gone = null;
            foreach (KeyValuePair<AudioSource, string> kv in _repaired)
            {
                if (kv.Key == null) { if (gone == null) gone = new List<AudioSource>(); gone.Add(kv.Key); continue; }
                float want = RangeFor(kv.Value);
                if (Mathf.Abs(kv.Key.maxDistance - want) > 0.01f) kv.Key.maxDistance = want;
            }
            if (gone != null) foreach (AudioSource a in gone) _repaired.Remove(a);
        }
        private static FieldInfo s_ModuleSource;
        private readonly HashSet<string> _spatialSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Any sound source on a creature, not only its voice. His log: the centipede has no voice
        /// module at all (the game cannot load its sound script) and what he hears is a looping
        /// crawl clip on a source on the Centipede object - which the voice check never saw. So
        /// every source found under a creature by the census is read and, if 2D, made 3D.
        /// </summary>
        private static string[] s_SpeciesNames;
        private static string SpeciesFromName(string objName)
        {
            if (string.IsNullOrEmpty(objName)) return null;
            if (s_SpeciesNames == null) s_SpeciesNames = Enum.GetNames(typeof(AIs.AI.AIID));
            for (int i = 0; i < s_SpeciesNames.Length; i++)
            {
                string n = s_SpeciesNames[i];
                if (n == "None" || n == "Count" || n.Length < 4) continue;
                if (objName.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return n;
            }
            return null;
        }

        private void CheckCreatureSource(string species, AudioSource src)
        {
            try
            {
                if (species == null || src == null) return;
                NoteCreatureSource(species, src);
                string key = species + ":" + (src.clip != null ? src.clip.name : src.gameObject.name);
                // Two ways to be heard from far away: 2D (no distance at all), or 3D with a range
                // longer than the voices the game itself sets (12 m). Both are corrected; the log
                // names which it was.
                float range = RangeFor(species);
                bool flat = src.spatialBlend < 0.99f;
                bool far  = !flat && src.maxDistance > range + 0.01f;
                if (_spatialSeen.Add(key))
                    Logger.LogInfo("creature source: " + key + " spatialBlend=" + src.spatialBlend.ToString("F2")
                        + " min=" + src.minDistance.ToString("F1") + " max=" + src.maxDistance.ToString("F1")
                        + " rolloff=" + src.rolloffMode + " loop=" + src.loop
                        + (flat ? "  <- 2D, does not fade with distance" : far ? "  <- 3D but carries too far" : ""));
                if ((flat || far) && _fix2D.Value)
                {
                    src.spatialBlend = 1f;
                    src.rolloffMode = AudioRolloffMode.Linear;      // reaches true silence at max, unlike Logarithmic
                    src.minDistance = 1f;
                    src.maxDistance = range;
                    _repaired[src] = species;
                    if (_spatialSeen.Add("fixed:" + key))
                        Logger.LogInfo("creature source: " + key + (flat ? " made 3D" : " range clamped") + ", "
                                       + range + "m linear" + (IsCritter(species) ? " (critter)" : ""));
                }
            }
            catch (Exception) { }
        }

        private static void CheckSpatial(AIs.AISoundModule module, AIs.AI ai)
        {
            try
            {
                if (s_Self == null || ai == null) return;
                if (s_ModuleSource == null) s_ModuleSource = AccessTools.Field(typeof(AIs.AISoundModule), "m_AudioSource");
                AudioSource src = (s_ModuleSource != null) ? s_ModuleSource.GetValue(module) as AudioSource : null;
                if (src == null) return;

                string id = ai.m_ID.ToString();
                s_Self.NoteCreatureSource(id, src);
                bool flat = src.spatialBlend < 0.99f;
                if (s_Self._spatialSeen.Add(id))
                    s_Self.Logger.LogInfo("voice: " + id + " spatialBlend=" + src.spatialBlend.ToString("F2")
                        + " min=" + src.minDistance.ToString("F1") + " max=" + src.maxDistance.ToString("F1")
                        + " rolloff=" + src.rolloffMode + (flat ? "  <- 2D, does not fade with distance" : ""));

                if (flat && s_Self._fix2D.Value)
                {
                    float range = s_Self.RangeFor(id);
                    src.spatialBlend = 1f;
                    src.rolloffMode = AudioRolloffMode.Linear;
                    src.minDistance = 1f;
                    src.maxDistance = range;
                    s_Self._repaired[src] = id;
                    if (s_Self._spatialSeen.Add("fixed:" + id))
                        s_Self.Logger.LogInfo("voice: " + id + " made 3D, range " + range + "m");
                }
            }
            catch (Exception) { }
        }

        // A real creature's idle call. Returning false skips the whole method - the Stop(), the
        // PlayOneShot and the multiplayer echo - which is exactly "this animal says nothing now".
        private static FieldInfo s_ModuleAI;

        [HarmonyPatch(typeof(AIs.AISoundModule), "PlayIdleSound")]
        private static class Patch_Idle
        {
            private static bool Prefix(AIs.AISoundModule __instance)
            {
                try
                {
                    if (s_ModuleAI == null) s_ModuleAI = AccessTools.Field(typeof(AIs.AIModule), "m_AI");
                    AIs.AI ai = (s_ModuleAI != null) ? s_ModuleAI.GetValue(__instance) as AIs.AI : null;
                    if (ai == null) return true;
                    CheckSpatial(__instance, ai);
                    if (AnimalMuted(ai.m_ID)) return false;
                    NowPlaying(Pretty(ai.m_ID.ToString()) + ": idle call");
                }
                catch (Exception) { }
                return true;
            }
        }

        // -----------------------------------------------------------------------------------------
        // Update / window
        // -----------------------------------------------------------------------------------------

        private float _animalStopAt;
        private void AnimalSourceTick()
        {
            // The postfix should make a muted clip never start. If one is playing anyway - a path
            // this mod did not see - it is stopped here, and the log says so once per clip.
            float now = Time.realtimeSinceStartup;
            if (now - _animalStopAt < 0.5f) return;
            _animalStopAt = now;
            try
            {
                AudioSource a = AnimalSource();
                if (a == null || !a.isPlaying || a.clip == null) return;
                if (!AmbientMuted(a.clip.name)) return;
                a.Stop();
                if (_seenPlaying.Add("stopped:" + a.clip.name))
                    Logger.LogInfo("ambient: '" + a.clip.name + "' was playing while muted - stopped it");
            }
            catch (Exception) { }
        }

        // -----------------------------------------------------------------------------------------
        // Not before the game is playable
        // -----------------------------------------------------------------------------------------
        //
        // "The name of the sound starts displaying before the game loads. We dealt with that with
        // the minimap." The same gate Field Notes settled on after two wrong versions, copied whole:
        // m_LoadGameState == None is the RESTING state (FullLoadCompleted is a moment that lasts a
        // few frames during loading and is false for all of play); a level exists and has started;
        // the loading screen is down; a player exists; then a short settle. No streamer clause - it
        // never settles. The mute patches stay live regardless: silencing the menu's ambience harms
        // nothing. Only what is drawn and swept waits.
        private float _agreedAt = -1f;
        private bool _playable;

        private bool GameIsPlayable()
        {
            try
            {
                GreenHellGame game = GreenHellGame.Instance;
                if (game == null || game.m_LoadGameState != LoadGameState.None) return NotYet();
                LoadingScreen screen = LoadingScreen.Get();
                if (screen != null && screen.m_Active) return NotYet();
                MainLevel level = MainLevel.Instance;
                if (level == null || !level.m_LevelStarted) return NotYet();
                if (Player.Get() == null) return NotYet();
                if (_agreedAt < 0f) _agreedAt = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _agreedAt < 1f) return false;
                if (!_playable) { _playable = true; Logger.LogInfo("game is playable - panel, name line and census are live"); }
                return true;
            }
            catch (Exception) { return true; }     // a fault in the gate must never take the panel away
        }

        private bool NotYet()
        {
            _agreedAt = -1f;
            if (_playable) { _playable = false; if (_open) SetOpen(false); }
            return false;
        }

        private void Update()
        {
            try
            {
                if (!GameIsPlayable()) return;
                DiscoverAmbient();
                JungleTick();
                AnimalSourceTick();
                if (_key.Value.IsDown()) SetOpen(!_open);
                if (_open && Input.GetKeyDown(KeyCode.Escape)) SetOpen(false);
            }
            catch (Exception) { }
        }

        // -----------------------------------------------------------------------------------------
        // Open / close - the Pickup Doctor recipe, copied because it is the one that works
        // -----------------------------------------------------------------------------------------
        //
        // Escape is read by the game through legacy Input polling in its own Update; Event.Use() in
        // OnGUI only consumes it for IMGUI. So "Escape closes the panel" was also "Escape opens the
        // pause menu". The fix is to refuse the menu itself while the panel is open, plus a short
        // grace after closing because the game may poll the same press later in the same frame.
        // Time-boxed, so a fault can never leave the pause menu permanently unopenable.
        //
        // And while open: the level's own reference-counted pause, and the cursor freed through the
        // game's CursorManager - without that the mouse stays locked under the panel, which is a
        // large part of why clicking rows felt unreliable at first.
        private static bool  s_BlockMenu;
        private static float s_BlockMenuUntil;
        private bool _pausedByUs;
        private bool _inputBlocked;

        private void SetOpen(bool open)
        {
            if (open == _open) return;
            _open = open;
            try
            {
                MainLevel lvl = MainLevel.Instance;
                if (open)
                {
                    s_BlockMenu = true;
                    if (lvl != null) { lvl.Pause(true); _pausedByUs = true; }
                    // "The actual game is frozen. It's the mouse that keeps moving the screen." The
                    // pause took; the look did not stop. Both calls are reference-counted by the
                    // game, so they nest with anything else and must be released symmetrically.
                    Player pl = Player.Get();
                    if (pl != null && !_inputBlocked) { pl.BlockRotation(); pl.BlockMoves(); _inputBlocked = true; }
                    CursorManager cm = CursorManager.Get();
                    if (cm != null) { cm.SetCursorLockState(CursorLockMode.None); cm.ShowCursor(true, false); }
                    Logger.LogInfo("panel opened - paused, look and moves blocked, cursor free (timeScale="
                                   + Time.timeScale.ToString("F2") + ")");
                }
                else
                {
                    s_BlockMenu = false;
                    s_BlockMenuUntil = Time.realtimeSinceStartup + 0.25f;
                    if (_pausedByUs && lvl != null) { lvl.Pause(false); _pausedByUs = false; }
                    Player pl = Player.Get();
                    if (pl != null && _inputBlocked) { pl.UnblockRotation(); pl.UnblockMoves(); }
                    _inputBlocked = false;
                    CursorManager cm = CursorManager.Get();
                    if (cm != null) { cm.ShowCursor(false, false); cm.SetCursorLockState(CursorLockMode.Locked); }
                }
            }
            catch (Exception ex) { Logger.LogWarning("open/close: " + ex.Message); }
        }

        [HarmonyPatch(typeof(MenuInGameManager), "ShowScreen")]
        private static class Patch_BlockGameMenu
        {
            private static bool Prefix()
            {
                return !(s_BlockMenu || Time.realtimeSinceStartup < s_BlockMenuUntil);
            }
        }

        private void OnGUI()
        {
            try
            {
                BuildStyles();

                // The name line is its own small window so it can be dragged anywhere - his ask,
                // "I wish I could move that name freely". It stays where it was put. While the
                // panel is open it is always shown, so there is something to grab.
                bool showName = _playable && _showNames.Value
                    && (_open || (Time.realtimeSinceStartup < s_NowPlayingUntil && s_NowPlaying.Length > 0));
                if (showName)
                {
                    if (_nameRect.x < 0f)
                    {
                        _nameRect.x = (Screen.width - _nameRect.width) * 0.5f;
                        _nameRect.y = Screen.height * 0.82f;
                        LoadNamePos();
                    }
                    _nameRect = GUI.Window(0x6A0D11, _nameRect, DrawNameWindow, "", GUIStyle.none);
                }

                if (!_open || !_playable) return;
                _rect = GUI.Window(0x6A0D10, _rect, DrawWindow, "");
            }
            catch (Exception ex) { Logger.LogWarning("panel: " + ex.Message); }
        }

        private void DrawNameWindow(int id)
        {
            GUI.DrawTexture(new Rect(0f, 0f, _nameRect.width, _nameRect.height), _pixel);
            string text = (s_NowPlaying.Length > 0 && Time.realtimeSinceStartup < s_NowPlayingUntil)
                          ? s_NowPlaying : (_open ? "(sound names appear here - drag me)" : "");
            GUI.Label(new Rect(0f, 0f, _nameRect.width, _nameRect.height), text, _nameStyle);
            GUI.DragWindow(new Rect(0f, 0f, _nameRect.width, _nameRect.height));
            string v = Mathf.RoundToInt(_nameRect.x) + "," + Mathf.RoundToInt(_nameRect.y);
            if (v != _namePos.Value) _namePos.Value = v;
        }

        private void LoadNamePos()
        {
            try
            {
                string[] p = (_namePos.Value ?? "").Split(',');
                if (p.Length == 2)
                {
                    float x, y;
                    if (float.TryParse(p[0], out x) && float.TryParse(p[1], out y)) { _nameRect.x = x; _nameRect.y = y; }
                }
            }
            catch (Exception) { }
        }

        private void DrawWindow(int id)
        {
            GUI.DrawTexture(new Rect(0f, 0f, _rect.width, _rect.height), _pixel);
            GUILayout.BeginVertical();

            GUILayout.Label(Name, _title);

            // Tabs, in his words.
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_tab == 0, "Ambient", _tab == 0 ? _tabOn : _tabOff)) _tab = 0;
            if (GUILayout.Toggle(_tab == 1, "True animal voice", _tab == 1 ? _tabOn : _tabOff)) _tab = 1;
            if (GUILayout.Toggle(_tab == 2, "Jungle (everything playing)", _tab == 2 ? _tabOn : _tabOff)) _tab = 2;
            GUILayout.EndHorizontal();

            // ONE STYLE. His rule, with a screenshot: the reset button and the master checkbox did the
            // same job in opposite directions in two different clothes. Every line on this panel is
            // now the same row - circle on the left, icon, name - and the master line is the rows'
            // own state: filled when nothing on the tab is muted, empty when everything is, and
            // clicking it sets every row. The rows are the only state, so the two can never disagree.
            GUILayout.Space(4f);
            int mutedHere = (_tab == 0) ? _mutedAmbientSet.Count : (_tab == 1) ? _mutedAnimalSet.Count : _mutedJungleSet.Count;
            string masterLabel = (_tab == 0) ? "Whole ambient layer" : (_tab == 1) ? "Every creature's idle calls" : "Every jungle layer";
            bool masterOn = (mutedHere == 0);
            if (Row(null, masterLabel, masterOn) != masterOn)
            {
                if (masterOn) MuteAllOnTab(_tab); else UnmuteAllOnTab(_tab);
                SaveLists();
                ApplyNow();
            }
            bool names = _showNames.Value;
            if (Row(null, "Name each sound on screen as it plays", names) != names) _showNames.Value = !names;

            // HIS ASK: "sliders inside the K menu for the distance that I can hear the critters and
            // small creatures." Two ranges, live - a moved slider re-ranges every repaired source on
            // the next sweep.
            if (_tab == 1)
            {
                GUILayout.Space(4f);
                _critterRange.Value  = Slider("Critters: how far", _critterRange.Value, 2f, 60f, " m");
                _critterVolume.Value = Slider("Critters: how loud", _critterVolume.Value, 0f, 100f, " %");
                _voiceRange.Value    = Slider("Other creatures: how far", _voiceRange.Value, 5f, 200f, " m");
            }
            GUILayout.Space(6f);

            _scroll = GUILayout.BeginScrollView(_scroll, false, true);
            if (_tab == 0)
            {
                if (_ambientNames.Count == 0)
                    GUILayout.Label("No ambient clips read yet - they appear once a level is loaded.", _dim);
                for (int i = 0; i < _ambientNames.Count; i++)
                {
                    string n = _ambientNames[i];
                    bool on = !_mutedAmbientSet.Contains(n);
                    if (Row(IconForClip(n), n, on) != on)
                    {
                        if (on) _mutedAmbientSet.Add(n); else _mutedAmbientSet.Remove(n);
                        SaveLists();
                        ApplyNow();
                    }
                }
            }
            else if (_tab == 2)
            {
                if (_jungleNames.Count == 0)
                    GUILayout.Label("No jungle layers found yet - they appear once a level is loaded.", _dim);
                for (int i = 0; i < _jungleNames.Count; i++)
                {
                    string n = _jungleNames[i];
                    bool on = !_mutedJungleSet.Contains(n);
                    if (Row(IconForJungle(n), n, on) != on)
                    {
                        if (on) _mutedJungleSet.Add(n); else _mutedJungleSet.Remove(n);
                        SaveLists();
                        ApplyNow();
                    }
                }
            }
            else
            {
                for (int i = 0; i < _animalNames.Count; i++)
                {
                    string n = _animalNames[i];
                    bool on = !_mutedAnimalSet.Contains(n);
                    if (Row(IconForAnimal(n), Pretty(n), on) != on)
                    {
                        if (on) _mutedAnimalSet.Add(n); else _mutedAnimalSet.Remove(n);
                        SaveLists();
                        ApplyNow();
                    }
                }
            }
            GUILayout.EndScrollView();

            // Close is a row too - the same shape as everything else, an x where the circle goes.
            GUILayout.Space(4f);
            if (ActionRow("Close   (" + _key.Value.MainKey + " or Esc)")) SetOpen(false);
            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 28f));
            SaveWindowPos();
        }

        private void MuteAllOnTab(int tab)
        {
            if (tab == 0)      { _mutedAmbientSet.Clear(); foreach (string n in _ambientNames) _mutedAmbientSet.Add(n); }
            else if (tab == 1) { _mutedAnimalSet.Clear();  foreach (string n in _animalNames)  _mutedAnimalSet.Add(n); }
            else               { _mutedJungleSet.Clear();  foreach (string n in _jungleNames)  _mutedJungleSet.Add(n); }
            Logger.LogInfo("panel: tab " + tab + " - everything OFF");
        }

        private void UnmuteAllOnTab(int tab)
        {
            if (tab == 0) _mutedAmbientSet.Clear(); else if (tab == 1) _mutedAnimalSet.Clear(); else _mutedJungleSet.Clear();
            Logger.LogInfo("panel: tab " + tab + " - everything ON");
        }

        /// <summary>
        /// The mute lands in the same frame as the click - no waiting for the two-second sweep.
        /// His report: "it takes between 3 to 5 seconds for it to take effect". The sweep timer is
        /// reset so the next Update rebuilds and applies; the click's time is kept so the log can
        /// say how long the silence actually took, measured, next time it is asked.
        /// </summary>
        private float _clickAt;
        private void ApplyNow()
        {
            _jungleAt = 0f;
            _animalStopAt = 0f;
            _clickAt = Time.realtimeSinceStartup;
        }

        /// <summary>A labelled slider in metres, laid out like a row: name left, value right, bar under.</summary>
        private float Slider(string label, float value, float lo, float hi, string unit)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("        " + label, _row, GUILayout.ExpandWidth(true));
            GUILayout.Label(Mathf.RoundToInt(value) + unit, _row, GUILayout.Width(60f));
            GUILayout.EndHorizontal();
            float v = GUILayout.HorizontalSlider(value, lo, hi);
            GUILayout.Space(4f);
            if (Mathf.Abs(v - value) > 0.01f) _jungleAt = 0f;      // apply on the next Update
            return v;
        }

        /// <summary>A row that does something rather than holding a state: an x in the circle's slot.</summary>
        private bool ActionRow(string label)
        {
            GUIContent c = new GUIContent("    " + label);
            bool clicked = GUILayout.Button(c, _rowBtn, GUILayout.Height(32f));
            if (Event.current.type == EventType.Repaint)
            {
                Rect r = GUILayoutUtility.GetLastRect();
                Rect rr = new Rect(r.x + 8f, r.y + (r.height - 22f) * 0.5f, 22f, 22f);
                GUI.DrawTexture(rr, _cross, ScaleMode.ScaleToFit, true);
            }
            return clicked;
        }

        /// <summary>
        /// One row: icon, name, radio. Returns the new on/off.
        ///
        /// A REAL BUTTON NOW. The first build read the mouse itself against GetLastRect after the
        /// row's layout group, and his config shows what that was worth: one jungle row registered
        /// and every ambient and animal row he clicked did not - "nothing I select seems to be
        /// turned off". IMGUI's own Button has handled clicks inside scroll views and windows for
        /// fifteen years; the radio is drawn over it as the state, and the button is the target.
        /// </summary>
        private bool Row(Texture2D icon, string label, bool on)
        {
            // Circle first, his ask: "place all of the clickable radial circles to the left of the
            // options" - on the right they sat half under the scrollbar. The button's own padding
            // leaves the slot; the circle is drawn into it on Repaint.
            GUIContent c = new GUIContent("    " + label, icon);
            bool clicked = GUILayout.Button(c, on ? _rowBtn : _rowBtnDim, GUILayout.Height(32f));
            if (Event.current.type == EventType.Repaint)
            {
                Rect r = GUILayoutUtility.GetLastRect();
                Rect rr = new Rect(r.x + 8f, r.y + (r.height - 22f) * 0.5f, 22f, 22f);
                GUI.DrawTexture(rr, on ? _radioOn : _radioOff, ScaleMode.ScaleToFit, true);
            }
            if (clicked)
            {
                Logger.LogInfo("panel: " + label + " -> " + (on ? "OFF" : "ON"));
                return !on;
            }
            return on;
        }

        // -----------------------------------------------------------------------------------------
        // Icons - the Field Notes set, so a tapir is the same tapir
        // -----------------------------------------------------------------------------------------

        private Texture2D Icon(string file)
        {
            Texture2D t;
            if (_icons.TryGetValue(file, out t)) return t;
            t = null;
            try
            {
                string path = Path.Combine(_iconDir, file + ".png");
                if (File.Exists(path))
                {
                    t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                    if (!ImageConversion.LoadImage(t, File.ReadAllBytes(path))) t = null;
                }
            }
            catch (Exception) { t = null; }
            _icons[file] = t;
            return t;
        }

        private Texture2D IconForAnimal(string aiid)
        {
            string n = aiid.ToLowerInvariant();
            string f = "animal";
            if (n.Contains("puma") || n.Contains("jaguar") || n.Contains("panther") || n.Contains("stalker")) f = "predator";
            else if (n.Contains("caiman") && !n.Contains("lizard")) f = "lizard";
            else if (n.Contains("anaconda") || n.Contains("boa") || n.Contains("rattlesnake")) f = "snake";
            else if (n.Contains("spider") || n.Contains("birdeater")) f = "spider";
            else if (n.Contains("scorpion")) f = "scorpion";
            else if (n.Contains("mouse")) f = "mouse";
            else if (n.Contains("agouti")) f = "agouti";
            else if (n.Contains("peccary")) f = "peccary";
            else if (n.Contains("capybara")) f = "capybara";
            else if (n.Contains("tapir")) f = "tapir";
            else if (n.Contains("armadillo")) f = "armadillo";
            else if (n.Contains("tamarin") || n.Contains("atelinae")) f = "monkey";
            else if (n.Contains("prawn") || n.Contains("crab")) f = "crab";
            else if (n.Contains("stingray")) f = "stingray";
            else if (n.Contains("piranha") || n.Contains("arowana") || n.Contains("bass") || n.Contains("fish")) f = "fish";
            else if (n.Contains("frog") || n.Contains("toad")) f = "frog";
            else if (n.Contains("turtle") || n.Contains("tortoise")) f = "turtle";
            else if (n.Contains("iguana") || n.Contains("lizard")) f = "lizard";
            else if (n.Contains("centipede") || n.Contains("caterpillar") || n.Contains("beetle")) f = "bug";
            else if (n.Contains("anteater")) f = "anteater";
            return Icon(f);
        }

        private Texture2D IconForClip(string clip)
        {
            string n = clip.ToLowerInvariant();
            string f = "animal";
            if (n.Contains("monkey") || n.Contains("howler") || n.Contains("tamarin") || n.Contains("ape")) f = "monkey";
            else if (n.Contains("parrot") || n.Contains("macaw")) f = "parrot";
            else if (n.Contains("toucan")) f = "toucan";
            else if (n.Contains("bird")) f = "parrot";
            else if (n.Contains("frog") || n.Contains("toad")) f = "frog";
            else if (n.Contains("bat")) f = "bat";
            else if (n.Contains("insect") || n.Contains("cicada") || n.Contains("cricket") || n.Contains("bug")) f = "bug";
            else if (n.Contains("jaguar") || n.Contains("puma") || n.Contains("cat") || n.Contains("roar")) f = "predator";
            else if (n.Contains("snake")) f = "snake";
            else if (n.Contains("tapir")) f = "tapir";
            else if (n.Contains("capybara")) f = "capybara";
            return Icon(f);
        }

        private Texture2D IconForJungle(string name)
        {
            string n = name.ToLowerInvariant();
            string f = "animal";
            if (n.Contains("rain") || n.Contains("thunder") || n.Contains("storm")) f = "rain";
            else if (n.Contains("water") || n.Contains("river") || n.Contains("waterfall") || n.Contains("stream")) f = "water";
            else if (n.Contains("wind")) f = "wind";
            else if (n.Contains("night")) f = "night";
            else if (n.Contains("day") || n.Contains("forest") || n.Contains("jungle") || n.Contains("bed")) f = "leaf";
            else if (n.Contains("insect") || n.Contains("cicada") || n.Contains("cricket")) f = "bug";
            else if (n.Contains("frog")) f = "frog";
            else if (n.Contains("bird")) f = "parrot";
            return Icon(f);
        }

        private static string Pretty(string aiid)
        {
            // GoldenLionTamarin -> Golden Lion Tamarin; Tapir_baby -> Tapir baby
            string s = aiid.Replace('_', ' ');
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c) && s[i - 1] != ' ' && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // -----------------------------------------------------------------------------------------
        // Styles, window position
        // -----------------------------------------------------------------------------------------

        private void BuildStyles()
        {
            if (_styled) return;
            _styled = true;

            _pixel = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _pixel.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.10f, 0.92f));
            _pixel.Apply();

            _radioOn  = Radio(true);
            _radioOff = Radio(false);
            _cross    = Cross();

            _title = new GUIStyle(GUI.skin.label);
            _title.fontSize = 18; _title.fontStyle = FontStyle.Bold;
            _title.normal.textColor = new Color(0.96f, 0.97f, 1f);

            _row = new GUIStyle(GUI.skin.label);
            _row.fontSize = 15; _row.alignment = TextAnchor.MiddleLeft;
            _row.normal.textColor = new Color(0.92f, 0.93f, 0.95f);

            _dim = new GUIStyle(_row);
            _dim.normal.textColor = new Color(0.55f, 0.57f, 0.62f);

            // A button that looks like a row: no box, icon on the left, text beside it.
            _rowBtn = new GUIStyle(GUI.skin.button);
            _rowBtn.fontSize = 15; _rowBtn.alignment = TextAnchor.MiddleLeft;
            _rowBtn.imagePosition = ImagePosition.ImageLeft;
            _rowBtn.normal.background = null; _rowBtn.active.background = null;
            _rowBtn.focused.background = null;
            _rowBtn.hover.background = HoverTex();
            _rowBtn.normal.textColor = _row.normal.textColor;
            _rowBtn.hover.textColor = Color.white; _rowBtn.active.textColor = Color.white;
            _rowBtn.padding = new RectOffset(38, 6, 3, 3);       // room for the circle on the left
            _rowBtnDim = new GUIStyle(_rowBtn);
            _rowBtnDim.normal.textColor = _dim.normal.textColor;

            _nameStyle = new GUIStyle(_row);
            _nameStyle.alignment = TextAnchor.MiddleCenter;

            _tabOn = new GUIStyle(GUI.skin.button);
            _tabOn.fontSize = 15; _tabOn.fontStyle = FontStyle.Bold;
            _tabOn.normal.textColor = new Color(0.55f, 0.75f, 1f);
            _tabOn.onNormal = _tabOn.normal;
            _tabOff = new GUIStyle(GUI.skin.button);
            _tabOff.fontSize = 15;
            _tabOff.normal.textColor = new Color(0.7f, 0.72f, 0.76f);
        }

        private static Texture2D HoverTex()
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            t.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.08f));
            t.Apply();
            return t;
        }

        /// <summary>An x, for the one row that acts instead of holding a state.</summary>
        private static Texture2D Cross()
        {
            const int S = 32;
            Texture2D t = new Texture2D(S, S, TextureFormat.ARGB32, false);
            Color ink = new Color(0.85f, 0.87f, 0.92f, 1f);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    bool d1 = Mathf.Abs(x - y) <= 2 && x >= 8 && x <= 23;
                    bool d2 = Mathf.Abs(x - (S - 1 - y)) <= 2 && x >= 8 && x <= 23;
                    t.SetPixel(x, y, (d1 || d2) ? ink : new Color(0f, 0f, 0f, 0f));
                }
            t.Apply();
            return t;
        }

        /// <summary>A circle, and a circle with a dot in the middle. Drawn, not a font glyph.</summary>
        private static Texture2D Radio(bool on)
        {
            const int S = 32;
            Texture2D t = new Texture2D(S, S, TextureFormat.ARGB32, false);
            Color ring = new Color(0.85f, 0.87f, 0.92f, 1f);
            Color dot  = new Color(0.55f, 0.85f, 1f, 1f);
            float c = (S - 1) * 0.5f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    Color px = new Color(0f, 0f, 0f, 0f);
                    if (d >= 11.5f && d <= 14f) px = ring;
                    else if (on && d <= 7f) px = dot;
                    t.SetPixel(x, y, px);
                }
            t.Apply();
            return t;
        }

        private void LoadWindowPos()
        {
            try
            {
                string[] p = (_windowPos.Value ?? "").Split(',');
                if (p.Length == 2)
                {
                    float x, y;
                    if (float.TryParse(p[0], out x) && float.TryParse(p[1], out y)) { _rect.x = x; _rect.y = y; }
                }
            }
            catch (Exception) { }
        }

        private void SaveWindowPos()
        {
            string v = Mathf.RoundToInt(_rect.x) + "," + Mathf.RoundToInt(_rect.y);
            if (v != _windowPos.Value) _windowPos.Value = v;
        }
    }
}
