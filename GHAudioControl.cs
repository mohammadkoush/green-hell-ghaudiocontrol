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
// The panel is the game-window style: K opens it, two tabs, an icon and a radio switch per row.
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
        public const string Version = "1.0.0";

        private static GHAudioControlPlugin s_Self;

        // ---- config ------------------------------------------------------------------------------
        private ConfigEntry<KeyboardShortcut> _key;
        private ConfigEntry<string> _mutedAmbient;
        private ConfigEntry<string> _mutedAnimals;
        private ConfigEntry<bool>   _muteAllAmbient;
        private ConfigEntry<bool>   _muteAllAnimals;
        private ConfigEntry<bool>   _showNames;
        private ConfigEntry<float>  _nameSeconds;
        private ConfigEntry<string> _windowPos;

        private readonly HashSet<string> _mutedAmbientSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mutedAnimalSet  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
        private Rect _rect = new Rect(200f, 120f, 420f, 560f);
        private Vector2 _scroll;
        private GUIStyle _title, _row, _tabOn, _tabOff, _dim;
        private bool _styled;
        private Texture2D _radioOn, _radioOff, _pixel;
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
            _muteAllAmbient = Config.Bind("Mute", "AllAmbient", false,
                "Master switch for the whole ambient animal layer.");
            _muteAllAnimals = Config.Bind("Mute", "AllAnimalVoices", false,
                "Master switch for every creature's idle calls.");
            _showNames = Config.Bind("Panel", "ShowNamesOnScreen", true,
                "Name each sound on screen as it plays, so you can decide what to switch off. " +
                "Turn it off once you are done choosing.");
            _nameSeconds = Config.Bind("Panel", "ShowNamesSeconds", 4f,
                new ConfigDescription("How long each name stays on screen.",
                    new AcceptableValueRange<float>(1f, 15f)));
            _windowPos = Config.Bind("Panel", "WindowPosition", "",
                "Where the panel was last dragged. Written automatically.");

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
        }

        private void SaveLists()
        {
            _mutedAmbient.Value = string.Join(", ", new List<string>(_mutedAmbientSet).ToArray());
            _mutedAnimals.Value = string.Join(", ", new List<string>(_mutedAnimalSet).ToArray());
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
            if (s_Self._muteAllAmbient.Value) return true;
            return s_Self._mutedAmbientSet.Contains(clipName);
        }

        private static bool AnimalMuted(AIs.AI.AIID id)
        {
            if (s_Self == null) return false;
            if (s_Self._muteAllAnimals.Value) return true;
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

        private void Update()
        {
            try
            {
                DiscoverAmbient();
                if (_key.Value.IsDown()) _open = !_open;
                if (_open && Input.GetKeyDown(KeyCode.Escape)) _open = false;
            }
            catch (Exception) { }
        }

        private void OnGUI()
        {
            try
            {
                BuildStyles();

                if (_showNames.Value && Time.realtimeSinceStartup < s_NowPlayingUntil && s_NowPlaying.Length > 0)
                {
                    GUIContent c = new GUIContent(s_NowPlaying);
                    Vector2 sz = _row.CalcSize(c);
                    Rect r = new Rect((Screen.width - sz.x) * 0.5f - 10f, Screen.height * 0.82f, sz.x + 20f, sz.y + 8f);
                    GUI.DrawTexture(r, _pixel);
                    GUI.Label(new Rect(r.x + 10f, r.y + 4f, sz.x, sz.y), c, _row);
                }

                if (!_open) return;
                _rect = GUI.Window(0x6A0D10, _rect, DrawWindow, "");
            }
            catch (Exception ex) { Logger.LogWarning("panel: " + ex.Message); }
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
            GUILayout.EndHorizontal();

            // Master switch for the tab, then the show-names switch.
            GUILayout.Space(4f);
            if (_tab == 0)
            {
                bool all = GUILayout.Toggle(!_muteAllAmbient.Value, "  whole ambient layer");
                if (all == _muteAllAmbient.Value) _muteAllAmbient.Value = !all;
            }
            else
            {
                bool all = GUILayout.Toggle(!_muteAllAnimals.Value, "  every creature's idle calls");
                if (all == _muteAllAnimals.Value) _muteAllAnimals.Value = !all;
            }
            bool names = GUILayout.Toggle(_showNames.Value, "  name each sound on screen as it plays");
            if (names != _showNames.Value) _showNames.Value = names;
            GUILayout.Space(6f);

            _scroll = GUILayout.BeginScrollView(_scroll);
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
                    }
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Label(_key.Value.MainKey + " or Esc closes.  Drag the top edge to move.", _dim);
            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 28f));
            SaveWindowPos();
        }

        /// <summary>One row: icon, name, radio. Returns the new on/off.</summary>
        private bool Row(Texture2D icon, string label, bool on)
        {
            GUILayout.BeginHorizontal(GUILayout.Height(30f));
            Rect ir = GUILayoutUtility.GetRect(26f, 26f, GUILayout.Width(26f));
            if (icon != null) GUI.DrawTexture(ir, icon, ScaleMode.ScaleToFit, true);
            GUILayout.Space(6f);
            GUILayout.Label(label, on ? _row : _dim, GUILayout.ExpandWidth(true));
            Rect rr = GUILayoutUtility.GetRect(22f, 22f, GUILayout.Width(22f));
            GUI.DrawTexture(rr, on ? _radioOn : _radioOff, ScaleMode.ScaleToFit, true);
            GUILayout.EndHorizontal();

            // The whole row is the button - the radio is the state, not a small target.
            Rect whole = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseUp && whole.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
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

            _title = new GUIStyle(GUI.skin.label);
            _title.fontSize = 18; _title.fontStyle = FontStyle.Bold;
            _title.normal.textColor = new Color(0.96f, 0.97f, 1f);

            _row = new GUIStyle(GUI.skin.label);
            _row.fontSize = 15; _row.alignment = TextAnchor.MiddleLeft;
            _row.normal.textColor = new Color(0.92f, 0.93f, 0.95f);

            _dim = new GUIStyle(_row);
            _dim.normal.textColor = new Color(0.55f, 0.57f, 0.62f);

            _tabOn = new GUIStyle(GUI.skin.button);
            _tabOn.fontSize = 15; _tabOn.fontStyle = FontStyle.Bold;
            _tabOn.normal.textColor = new Color(0.55f, 0.75f, 1f);
            _tabOn.onNormal = _tabOn.normal;
            _tabOff = new GUIStyle(GUI.skin.button);
            _tabOff.fontSize = 15;
            _tabOff.normal.textColor = new Color(0.7f, 0.72f, 0.76f);
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
