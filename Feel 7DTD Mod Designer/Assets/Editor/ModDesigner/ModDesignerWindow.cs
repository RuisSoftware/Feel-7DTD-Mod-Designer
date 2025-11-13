// Assets/Editor/ModDesignerWindow.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using static UnityEngine.GUILayout;

public class ModDesignerWindow : EditorWindow
{
    [MenuItem("Tools/Feel 7DTD/Mod Designer")]
    public static void ShowWindow() => GetWindow<ModDesignerWindow>("Feel - Mod Designer");

    string gameConfigPath = "Path/To/7DaysToDie/Data/Config";
    string rootModsFolder = "";
    string exportModsFolder = "";

    class ModEntry
    {
        public string Name = "";
        public string ModFolder = "";
        public string ConfigFolder = "";
    }
    List<ModEntry> mods = new();
    int selectedModIndex = -1;

    readonly List<IConfigModule> modules = new();
    int selectedModule = 0;

    float leftWidth = 260f;
    Vector2 modsScroll;
    Vector2 moduleScroll;

    Dictionary<string, bool> moduleAvailability = new();

    // ModDesignerWindow.cs - add these fields to class definition
    private bool draggingLeftSplitter = false;
    private float initialLeftWidth = 0f;
    private float initialMouseX = 0f;

    // --- Modules list height (resizable) ---
    private float modulesListHeight = 220f;
    private bool draggingModulesHeight = false;
    private float modulesHeightStart = 220f;
    private float modulesMouseStartY = 0f;

    // --- Versioning state ---
    ModManifest currentManifest = null;
    string selectedGameVersion = "";      // huidig gekozen gameversie voor geselecteerde mod

    // persistent per mod (laatste selectie)
    Dictionary<string, string> lastSelectedGvPerMod = new();

    // voorkomt dat we meerdere Apply-calls in de queue stapelen
    private bool _applyGvScheduled = false;

    // kleine cache
    string VersionRootFor(ModEntry m) => Path.Combine(m.ModFolder, "XML");
    string ConfigPathFor(ModEntry m, string gv)
        => string.IsNullOrEmpty(gv) ? ResolveConfigFolder(m.ModFolder) : Path.Combine(VersionRootFor(m), gv, "Config");

    // --- Mod version edit buffer (UI) ---
    string modVersionEditBuffer = "";
    string modVersionBufferForGv = "";


    public static GUIContent s_EyeBtn;
    public static GUIContent GetEyeButtonContent()
    {
        if (s_EyeBtn != null) return s_EyeBtn;

        // Try a few built-in icons that exist across Unity versions
        string[] candidates = {
        "d_scenevis_visible_hover", "scenevis_visible_hover",
        "d_scenevis_visible", "scenevis_visible",
        "d_ViewToolOrbit", "ViewToolOrbit",
        "d_ViewToolZoom", "ViewToolZoom",
        "Search Icon", "d_Search Icon"
    };

        foreach (var name in candidates)
        {
            var c = EditorGUIUtility.IconContent(name);
            if (c != null && c.image != null)
            {
                c.tooltip = "Select & ping model prefab";
                s_EyeBtn = c;
                return s_EyeBtn;
            }
        }

        // Fallback when no icon is available on this Unity version/skin
        s_EyeBtn = new GUIContent("View", "Select & ping model prefab");
        return s_EyeBtn;
    }

    public static bool IsModelLikeProperty(string propName)
    {
        return !string.IsNullOrEmpty(propName) &&
               (propName.Equals("Model", StringComparison.OrdinalIgnoreCase) ||
                propName.Equals("Meshfile", StringComparison.OrdinalIgnoreCase));
    }

    void BuildModulesList()
    {
        modules.Clear();
        // Always include ModInfo first
        modules.Add(new ModInfoModule());
        if (!Directory.Exists(gameConfigPath))
        {
            // Base config folder not set or not found – fallback to core config modules
            modules.Add(new EntryXmlModule("Blocks", "blocks.xml", "block", "blocks"));
            modules.Add(new EntryXmlModule("Items", "items.xml", "item", "items"));
            modules.Add(new LocalizationModule());
            return;
        }
        // Gather all XML files in the base game config folder
        string[] files;
        try
        {
            files = Directory.GetFiles(gameConfigPath, "*.xml", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            files = new string[0];
        }
        System.Array.Sort(files, System.StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            string fname = Path.GetFileName(file);
            if (fname.Equals("ModInfo.xml", System.StringComparison.OrdinalIgnoreCase))
                continue;
            // Load base config to determine root and entry tags
            System.Xml.Linq.XDocument baseDoc;
            try
            {
                baseDoc = System.Xml.Linq.XDocument.Load(file);
            }
            catch
            {
                continue;
            }
            string rootTag = baseDoc.Root?.Name.LocalName ?? Path.GetFileNameWithoutExtension(fname);
            var firstEntry = baseDoc.Root?
                .Elements()
                .FirstOrDefault(e =>
                    e.Attribute("name") != null &&
                    e.Name.LocalName != "property" &&
                    e.Name.LocalName != "append" &&
                    e.Name.LocalName != "set" &&
                    e.Name.LocalName != "remove");

            string entryTag = firstEntry?.Name.LocalName;
            if (string.IsNullOrEmpty(entryTag)) continue;
            // Create friendly module name (capitalize and replace underscores)
            string moduleName = rootTag;
            if (moduleName.Contains("_"))
            {
                moduleName = string.Join(" ", moduleName.Split('_').Select(w => char.ToUpper(w[0]) + w.Substring(1)));
            }
            else if (moduleName.Length > 0)
            {
                moduleName = char.ToUpper(moduleName[0]) + moduleName.Substring(1);
            }

            if (fname.Equals("recipes.xml", StringComparison.OrdinalIgnoreCase))
                modules.Add(new RecipeConfigModule());
            else
            {
                if (moduleName == "Recipes")
                    continue;
                else if (moduleName == "Root")
                    continue;
                else
                    modules.Add(new EntryXmlModule(moduleName, fname, entryTag, rootTag));
            }
        }
        modules.Add(new LocalizationModule());

    }

    void OnEnable()
    {
        // Load last used paths from EditorPrefs
        gameConfigPath = EditorPrefs.GetString("MD_gameConfigPath", gameConfigPath);
        rootModsFolder = EditorPrefs.GetString("MD_rootModsFolder", rootModsFolder);
        exportModsFolder = EditorPrefs.GetString("MD_exportModsFolder", exportModsFolder);
        modulesListHeight = EditorPrefs.GetFloat("MD_modulesListHeight", 220f);

        // laad laatst gekozen gv per mod uit EditorPrefs (optioneel)
        var raw = EditorPrefs.GetString("MD_lastGvPerMod", "");
        if (!string.IsNullOrEmpty(raw))
        {
            // formaat: name=gv|name2=gv2|...
            foreach (var pair in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2) lastSelectedGvPerMod[kv[0]] = kv[1];
            }
        }

        BuildModulesList();
        RefreshModList();
        InitializeModulesForCurrentSelection();
    }

    void OnDisable()
    {
        // Save paths to EditorPrefs
        EditorPrefs.SetFloat("MD_modulesListHeight", modulesListHeight);
        EditorPrefs.SetString("MD_gameConfigPath", gameConfigPath);
        EditorPrefs.SetString("MD_rootModsFolder", rootModsFolder);
        EditorPrefs.SetString("MD_exportModsFolder", exportModsFolder);

        // persist lastSelectedGvPerMod
        var sb = new System.Text.StringBuilder();
        foreach (var kv in lastSelectedGvPerMod) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('|');
        EditorPrefs.SetString("MD_lastGvPerMod", sb.ToString());
    }

    // ModDesignerWindow.cs - updated OnGUI() function with resizable left pane logic
    float _cachedHeaderBottom = 0f; // zet dit als veld in de class

    void OnGUI()
    {
        // 1) Header tekenen
        GUILayout.Space(6);
        DrawTopBars();

        // 2) Marker om betrouwbare yMax te krijgen (werkt in Layout & Repaint)
        Rect marker = GUILayoutUtility.GetRect(1, 2, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint) _cachedHeaderBottom = marker.yMax;
        float headerBottom = Mathf.Max(_cachedHeaderBottom, marker.yMax);

        // (optioneel) visuele scheidslijn
        EditorGUI.DrawRect(new Rect(0, headerBottom - 1, position.width, 1), new Color(0, 0, 0, 0.2f));

        // 3) Content area onder de header
        float cw = position.width;
        float ch = Mathf.Max(1f, position.height - headerBottom);
        Rect content = new Rect(0, headerBottom, cw, ch);

        // 4) Begin group -> alles hierbinnen is lokaal (0,0) = onder header
        GUI.BeginGroup(content);
        try
        {
            const float splitterW = 6f, minLeft = 150f, minRight = 320f;

            leftWidth = Mathf.Clamp(leftWidth, minLeft, Mathf.Max(minLeft, content.width - (minRight + splitterW)));

            var leftRect = new Rect(0, 0, leftWidth, content.height);
            var splitterRect = new Rect(leftRect.xMax, 0, splitterW, content.height);
            var rightRect = new Rect(splitterRect.xMax, 0, Mathf.Max(1f, content.width - leftWidth - splitterW), content.height);

            // drag binnen de group (muisposities zijn nu group-local)
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);
            if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition))
            {
                draggingLeftSplitter = true;
                initialMouseX = Event.current.mousePosition.x;
                initialLeftWidth = leftWidth;
                Event.current.Use();
            }
            if (draggingLeftSplitter && Event.current.type == EventType.MouseDrag)
            {
                float dx = Event.current.mousePosition.x - initialMouseX;
                leftWidth = Mathf.Clamp(initialLeftWidth + dx, minLeft, Mathf.Max(minLeft, content.width - (minRight + splitterW)));
                Repaint();
                Event.current.Use();
            }
            if (draggingLeftSplitter && Event.current.type == EventType.MouseUp)
            {
                draggingLeftSplitter = false;
                Event.current.Use();
            }

            // splitter tekenen
            EditorGUI.DrawRect(new Rect(splitterRect.x + 2f, splitterRect.y, 2f, splitterRect.height), new Color(1f, 0.5f, 0f, 0.6f));

            // panes (tekenen nu gegarandeerd onder de header)
            DrawLeftPane(leftRect);
            DrawRightPane(rightRect);
        }
        finally
        {
            GUI.EndGroup();
        }
    }


    void DrawTopBars()
    {
        // Game Config path field
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Game Config Folder", GUILayout.Width(140));
        gameConfigPath = EditorGUILayout.TextField(gameConfigPath);
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            var sel = EditorUtility.OpenFolderPanel("Select Game Data/Config", gameConfigPath, "");
            if (!string.IsNullOrEmpty(sel))
            {
                gameConfigPath = sel;
                BuildModulesList();
                InitializeModulesForCurrentSelection();
            }
        }
        EditorGUILayout.EndHorizontal();

        // Root Mods folder field
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Root Mods Folder", GUILayout.Width(140));
        rootModsFolder = EditorGUILayout.TextField(rootModsFolder);
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            var sel = EditorUtility.OpenFolderPanel("Select Root Mods Folder", rootModsFolder, "");
            if (!string.IsNullOrEmpty(sel))
            {
                rootModsFolder = sel;
                RefreshModList();
            }
        }
        if (GUILayout.Button("Refresh", GUILayout.Width(70)))
        {
            RefreshModList();
        }
        EditorGUILayout.EndHorizontal();

        // Boven in OnGUI(), na Root Mods Folder:
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Export Mods Folder", GUILayout.Width(140));
        exportModsFolder = EditorGUILayout.TextField(exportModsFolder);
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string sel = EditorUtility.OpenFolderPanel("Select Export Mods Folder", exportModsFolder, "");
            if (!string.IsNullOrEmpty(sel))
            {
                exportModsFolder = sel;
            }
        }
        EditorGUILayout.EndHorizontal();


        // Extra tools
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Detect Unity (from game folder)", GUILayout.Width(230)))
        {
            string folder = EditorUtility.OpenFolderPanel("Select 7 Days to Die folder", "", "");
            if (!string.IsNullOrEmpty(folder))
            {
                if (UnityVersionUtil.TryGetUnityFromUnityPlayer(folder, out var unityVer))
                {
                    var gvi2 = currentManifest?.GetOrCreate(selectedGameVersion);
                    if (gvi2 != null) { gvi2.unity = unityVer; SaveManifestForSelected(); ApplySelectedGameVersionToContext(); }
                    EditorUtility.DisplayDialog("Unity detected", $"Detected Unity: {unityVer}\nSaved for GV {selectedGameVersion}.", "OK");
                }
                else EditorUtility.DisplayDialog("Not found", "Could not read Unity version from UnityPlayer.dll.", "OK");
            }
        }
        if (GUILayout.Button("Inspect .unity3d…", GUILayout.Width(150)))
        {
            string p = EditorUtility.OpenFilePanel("Pick AssetBundle (.unity3d)", "", "unity3d");
            if (!string.IsNullOrEmpty(p))
            {
                if (UnityVersionUtil.TryReadAssetBundleUnityVersions(p, out var engine, out var player))
                    EditorUtility.DisplayDialog("Bundle header", $"Engine: {engine}\nPlayer: {player}\n\nFile: {p}", "OK");
                else
                    EditorUtility.DisplayDialog("Unknown/invalid", "Could not read UnityFS header.", "OK");
            }
        }
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    void DrawGameVersionBar()
    {
        var mod = mods[selectedModIndex];
        if (currentManifest == null) LoadManifestForSelected();

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.BeginHorizontal();

        // dropdown
        var versions = currentManifest?.ListGameVersions() ?? Array.Empty<string>();
        int selIdx = Math.Max(0, Array.IndexOf(versions, selectedGameVersion));
        EditorGUILayout.LabelField("Game Version:", GUILayout.Width(100));
        if (versions.Length == 0)
        {
            GUILayout.Label(string.IsNullOrEmpty(selectedGameVersion) ? "(none/legacy)" : selectedGameVersion, EditorStyles.miniBoldLabel);
        }
        else
        {
            int newIdx = EditorGUILayout.Popup(selIdx, versions, GUILayout.Width(140));
            if (newIdx != selIdx)
            {
                selectedGameVersion = versions[newIdx];
                lastSelectedGvPerMod[mod.Name] = selectedGameVersion;
                SaveManifestForSelected();
                ApplySelectedGameVersionToContext();
            }
        }

        if (GUILayout.Button("+ New…", GUILayout.Width(80))) NewGameVersionFlow();
        using (new EditorGUI.DisabledGroupScope(string.IsNullOrEmpty(selectedGameVersion)))
        {
            if (GUILayout.Button("Rename", GUILayout.Width(80))) RenameGameVersionFlow();
            if (GUILayout.Button("Delete", GUILayout.Width(80))) DeleteGameVersionFlow();
            string lockLabel = (currentManifest?.GetOrCreate(selectedGameVersion)?.locked ?? false) ? "Unlock" : "Lock";
            if (GUILayout.Button(lockLabel, GUILayout.Width(80))) ToggleLockForSelectedVersion();
        }
        EditorGUILayout.EndHorizontal();

        // per-gv metadata
        var gvi = string.IsNullOrEmpty(selectedGameVersion) ? null : currentManifest?.GetOrCreate(selectedGameVersion);

        // Zorg dat de buffer matcht met de huidige GV (bij wisselen)
        if ((modVersionBufferForGv ?? "") != (selectedGameVersion ?? ""))
        {
            modVersionEditBuffer = gvi?.modVersion ?? "1.0";
            modVersionBufferForGv = selectedGameVersion ?? "";
        }

        using (new EditorGUI.DisabledGroupScope(gvi?.locked ?? false))
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent("Mod Version:"), GUILayout.Width(100));
            // huidige versie als tekst
            GUILayout.Label(string.IsNullOrEmpty(gvi?.modVersion) ? "1.0" : gvi.modVersion, EditorStyles.miniBoldLabel, GUILayout.Width(80));
            // invoerveld (buffer)
            modVersionEditBuffer = EditorGUILayout.TextField(modVersionEditBuffer, GUILayout.Width(100));
            // toepassen via knop
            if (GUILayout.Button("Change", GUILayout.Width(70)))
            {
                SetModVersionForSelected(string.IsNullOrWhiteSpace(modVersionEditBuffer) ? "1.0" : modVersionEditBuffer.Trim());
                SaveManifestForSelected();
                ApplySelectedGameVersionToContext(); // refresh UI + context
                                                     // buffer updaten zodat label en field in sync blijven
                var gvi2 = currentManifest?.GetOrCreate(selectedGameVersion);
                modVersionEditBuffer = gvi2?.modVersion ?? "1.0";
                modVersionBufferForGv = selectedGameVersion ?? "";
            }
            EditorGUILayout.EndHorizontal();
        }


        // info & lock notice
        if (gvi?.locked == true)
            EditorGUILayout.HelpBox($"Game version '{selectedGameVersion}' is LOCKED. Unlock to edit.", MessageType.Warning);

        // context path duidelijk maken
        EditorGUILayout.LabelField("Active Config Path:", mods[selectedModIndex].ConfigFolder, EditorStyles.miniLabel);

        EditorGUILayout.EndVertical();

    }
    void DrawLeftPane(Rect rect)
    {
        if (rect.width < 1f) rect.width = 1f;
        if (rect.height < 1f) rect.height = 1f;

        using (new AreaScope(rect))
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUILayout.Label("Mods", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("New Mod..."))
                    EditorApplication.delayCall += CreateNewMod;

                using (new EditorGUI.DisabledGroupScope(!(selectedModIndex >= 0 && selectedModIndex < mods.Count)))
                {
                    if (GUILayout.Button("Open Folder") && selectedModIndex >= 0 && selectedModIndex < mods.Count)
                        EditorUtility.RevealInFinder(mods[selectedModIndex].ModFolder);
                }
            }

            using (var sv = new EditorGUILayout.ScrollViewScope(modsScroll, GUILayout.ExpandHeight(true)))
            {
                modsScroll = sv.scrollPosition;
                Color accentColor = new Color(1f, 0.6f, 0.2f);
                for (int i = 0; i < mods.Count; i++)
                {
                    var old = GUI.backgroundColor;
                    GUI.backgroundColor = (i == selectedModIndex ? accentColor : Color.white);
                    if (GUILayout.Button(mods[i].Name, GUILayout.ExpandWidth(true)))
                    {
                        selectedModIndex = i;
                        InitializeModulesForCurrentSelection();
                        Repaint();
                    }
                    GUI.backgroundColor = old;
                }
            }
        }
    }


    void DrawRightPane(Rect rect)
    {
        if (rect.width < 1f) rect.width = 1f;
        if (rect.height < 1f) rect.height = 1f;

        using (new AreaScope(rect))
        {
            // Geen mod geselecteerd
            if (selectedModIndex < 0 || selectedModIndex >= mods.Count)
            {
                EditorGUILayout.HelpBox("Select a mod on the left or create a new one.", MessageType.Info);
                return; // AreaScope sluit netjes
            }

            var mod = mods[selectedModIndex];
            GUILayout.Label(mod.Name, EditorStyles.boldLabel);

            // Game Version balk
            DrawGameVersionBar();

            // >>> Kritieke guard: render niets van Modules zolang er geen GV is <<<
            var hasAnyGv = (currentManifest?.ListGameVersions()?.Length ?? 0) > 0;
            if (!hasAnyGv)
            {
                GUILayout.Space(6);
                EditorGUILayout.HelpBox("No game versions yet. Create one to enable the modules UI.", MessageType.Info);
                return; // voorkomt layout-issues in modules wanneer GV leeg is
            }

            GUILayout.Space(6);
            GUILayout.Label("Modules", EditorStyles.boldLabel);

            // Modulelijst (scroll + splitter)
            modulesListHeight = Mathf.Clamp(modulesListHeight, 80f, Mathf.Max(120f, rect.height - 300f));
            using (var sv = new EditorGUILayout.ScrollViewScope(moduleScroll, GUILayout.Height(modulesListHeight)))
            {
                moduleScroll = sv.scrollPosition;

                for (int i = 0; i < modules.Count; i++)
                {
                    string name = modules[i].ModuleName;
                    bool hasFile = moduleAvailability.TryGetValue(name, out var exists) && exists;
                    var oldColor = GUI.color;
                    GUI.color = hasFile ? Color.green : Color.red;
                    var style = (i == selectedModule) ? "Button" : "miniButton";
                    if (GUILayout.Button(name, style)) selectedModule = i;
                    GUI.color = oldColor;
                }
            }

            // Splitter hoogte
            Rect modulesSplitterRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(4), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(new Rect(modulesSplitterRect.x, modulesSplitterRect.y + modulesSplitterRect.height / 2f - 1f, modulesSplitterRect.width, 2f),
                               new Color(1f, 0.5f, 0f, 0.6f));
            EditorGUIUtility.AddCursorRect(modulesSplitterRect, MouseCursor.ResizeVertical);

            if (Event.current.type == EventType.MouseDown && modulesSplitterRect.Contains(Event.current.mousePosition))
            {
                draggingModulesHeight = true;
                modulesHeightStart = modulesListHeight;
                modulesMouseStartY = GUIUtility.GUIToScreenPoint(Event.current.mousePosition).y;
                Event.current.Use();
            }
            if (draggingModulesHeight && Event.current.type == EventType.MouseDrag)
            {
                float curY = GUIUtility.GUIToScreenPoint(Event.current.mousePosition).y;
                float delta = curY - modulesMouseStartY;
                float maxH = Mathf.Max(120f, rect.height - 300f);
                modulesListHeight = Mathf.Clamp(modulesHeightStart + delta, 80f, maxH);
                Repaint();
                Event.current.Use();
            }
            if (draggingModulesHeight && Event.current.type == EventType.MouseUp)
            {
                draggingModulesHeight = false;
                EditorPrefs.SetFloat("MD_modulesListHeight", modulesListHeight);
                Event.current.Use();
            }

            GUILayout.Space(6);

            // Als er geen geldig selectedModule is, clampen
            if (modules.Count == 0) return;
            if (selectedModule < 0 || selectedModule >= modules.Count) selectedModule = 0;

            // Als geselecteerd module-bestand ontbreekt, toon create prompt
            if (selectedModule >= 0 && selectedModule < modules.Count)
            {
                string cfgFolder = mods[selectedModIndex].ConfigFolder ?? "";
                string targetFile = null;
                string suggestedRoot = null;

                if (modules[selectedModule] is EntryXmlModule em)
                {
                    targetFile = Path.Combine(cfgFolder, em.FileName);
                    suggestedRoot = Path.GetFileNameWithoutExtension(em.FileName);
                }
                else if (modules[selectedModule] is RecipeConfigModule rc)
                {
                    targetFile = Path.Combine(cfgFolder, rc.FileName);
                    suggestedRoot = Path.GetFileNameWithoutExtension(rc.FileName);
                }

                if (!string.IsNullOrEmpty(targetFile) && !File.Exists(targetFile))
                {
                    EditorGUILayout.HelpBox($"{Path.GetFileName(targetFile)} does not exist in this mod yet.", MessageType.Info);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button($"Create {Path.GetFileName(targetFile)}"))
                        {
                            Directory.CreateDirectory(cfgFolder);
                            File.WriteAllText(
                                targetFile,
                                $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<{suggestedRoot}>\n</{suggestedRoot}>\n"
                            );
                            AssetDatabase.Refresh();
                            EditorApplication.delayCall += () =>
                            {
                                InitializeModulesForCurrentSelection();
                                Repaint();
                            };
                        }
                        if (GUILayout.Button("Open Config Folder", GUILayout.Width(150)))
                            EditorUtility.RevealInFinder(cfgFolder);
                    }
                    GUILayout.Space(6);
                }
            }

            // Content: lijst & inspector naast elkaar
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandHeight(true)))
            {
                using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(340), GUILayout.ExpandHeight(true)))
                    {
                        // Let op: als een module intern Begin/End uit balans heeft, crasht Unity alsnog.
                        // Deze call werkte voorheen, en met de GV-guard hierboven vermijden we de probleemtoestand.
                        try { modules[selectedModule].OnGUIList(new Rect()); } catch { }
                    }

                    GUILayout.Space(6);

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
                    {
                        try { modules[selectedModule].OnGUIInspector(new Rect()); } catch { }
                    }
                }

                GUILayout.FlexibleSpace();

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Save Current"))
                    {
                        modules[selectedModule].Save();
                        SaveManifestForSelected();
                    }
                    if (GUILayout.Button("Save All"))
                    {
                        foreach (var m in modules) m.Save();
                        SaveManifestForSelected();
                        if (EditorUtility.DisplayDialog("AssetBundle", "Rebuild asset bundle for this mod?", "Yes", "No"))
                            BuildAssetsForSelectedMod();
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Export this mod")) ExportMod(mod);
                    if (GUILayout.Button("Export all mods")) ExportAllMods();
                }
            }
        }
    }

    static string NormalizePath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        return Path.GetFullPath(p)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }

    static bool IsSubPathOf(string child, string parent)
    {
        var c = NormalizePath(child);
        var p = NormalizePath(parent);
        if (string.IsNullOrEmpty(c) || string.IsNullOrEmpty(p)) return false;
        return c == p || c.StartsWith(p + Path.DirectorySeparatorChar);
    }

    static bool DirHasContent(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    void ClearDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        foreach (var d in Directory.GetDirectories(dir)) Directory.Delete(d, true);
    }

    void SafeDeleteSourceTreeIfAllowed(string src, ModEntry mod, string newModFolderToShow = null)
    {
        try
        {
            if (IsSubPathOf(src, gameConfigPath))
            {
                EditorUtility.DisplayDialog("Refused",
                    "For safety, I won't delete files from the base game's Data/Config.", "OK");
                return;
            }
            if (!IsSubPathOf(src, mod.ModFolder))
            {
                EditorUtility.DisplayDialog("Refused",
                    "Source folder is outside this mod. Not deleting.", "OK");
                return;
            }

            var versionRoot = VersionRootFor(mod);
            if (string.Equals(NormalizePath(src), NormalizePath(versionRoot), StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Refused", "I will not delete the XML root folder.", "OK");
                return;
            }
            if (string.Equals(NormalizePath(src), NormalizePath(Path.Combine(versionRoot, "Config")), StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Refused", "I will not delete the legacy XML/Config folder.", "OK");
                return;
            }

            // 3-knops dialoog met 'Toon nieuwe mod map'
            while (true)
            {
                int res = EditorUtility.DisplayDialogComplex(
                    "Delete old files?",
                    $"Delete the old source folder?\n\n{src}",
                    "Delete",                               // 0
                    "Show new mod folder",                  // 1
                    "Cancel"                                // 2
                );

                if (res == 2) return; // Cancel
                if (res == 1)
                {
                    if (!string.IsNullOrEmpty(newModFolderToShow))
                        EditorUtility.RevealInFinder(newModFolderToShow);
                    // loop terug naar confirm
                    continue;
                }

                // res == 0 → Delete
                Directory.Delete(src, true);

                var parent = Path.GetDirectoryName(src);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent, true);

                AssetDatabase.Refresh();
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[ModDesigner] Failed to delete source: " + ex);
        }
    }


    // ModDesignerWindow.cs - updated ExportMod() function
    void ExportMod(ModEntry mod)
    {
        if (string.IsNullOrEmpty(exportModsFolder))
        {
            EditorUtility.DisplayDialog("Export folder", "No export folder set.", "OK");
            return;
        }

        // Kies doel gameversie (zoals je had)
        var manifest = ModManifest.Load(mod.ModFolder);
        var gvList = manifest.ListGameVersions();
        string defaultGv = string.IsNullOrEmpty(selectedGameVersion) ? manifest.currentGameVersion : selectedGameVersion;

        string chosenGv = defaultGv;
        if (gvList.Length > 0)
        {
            int pick = EntryXmlModule.SimpleListPicker.Show("Export – choose Game Version", "Pick the game version to export", gvList);
            if (pick < 0) return;
            chosenGv = gvList[pick];
        }
        var gvi = string.IsNullOrEmpty(chosenGv) ? null : manifest.GetOrCreate(chosenGv);

        // (Unity mismatch waarschuwing: ongewijzigd)
        if (!string.IsNullOrEmpty(gvi?.unity) && !Application.unityVersion.StartsWith(gvi.unity))
        {
            int res = EditorUtility.DisplayDialogComplex(
                "Unity mismatch",
                $"Target GV '{chosenGv}' expects Unity '{gvi.unity}', current editor is '{Application.unityVersion}'.\n\nProceed anyway?",
                "Force export", "Cancel", "Proceed (no bundle)"
            );
            if (res == 1) return;
            if (res == 2) gvi.unity = "";
        }

        // Save all modules (ongewijzigd)
        foreach (var m in modules) m.Save();

        // Optioneel bundle (ongewijzigd)
        bool buildBundle = EditorUtility.DisplayDialog("AssetBundle", "Build asset bundle for this mod?", "Yes", "No");
        if (buildBundle && (gvi == null || string.IsNullOrEmpty(gvi.unity) || Application.unityVersion.StartsWith(gvi.unity)))
        {
            BuildAssetBundleForMod(mod);
        }

        // --- NIEUW: versie-gesegmenteerde exportmap ---
        string fullVersion = ComposeFullVersionString(chosenGv, gvi?.modVersion ?? "1.0");
        string destRoot = Path.Combine(exportModsFolder, mod.Name + "_" + fullVersion);      // "ExportLocation/<Version>"
        string destModPath = destRoot;              // binnen de versie-map komt de mod-map
        if (Directory.Exists(destModPath)) Directory.Delete(destModPath, true);
        Directory.CreateDirectory(destModPath);
        Directory.CreateDirectory(destRoot);

        // --- ModInfo.xml schrijven (GV-specifiek prefereren) ---
        string srcModInfo =
            // 1) versie-specifiek
            (!string.IsNullOrEmpty(chosenGv) ? Path.Combine(mod.ModFolder, "XML", chosenGv, "ModInfo.xml") : null);
        if (string.IsNullOrEmpty(srcModInfo) || !File.Exists(srcModInfo))
        {
            // 2) XML/ModInfo.xml
            string fallback1 = Path.Combine(mod.ModFolder, "XML", "ModInfo.xml");
            // 3) root/ModInfo.xml
            string fallback2 = Path.Combine(mod.ModFolder, "ModInfo.xml");
            srcModInfo = File.Exists(fallback1) ? fallback1 : (File.Exists(fallback2) ? fallback2 : null);
        }

        string outModInfo = Path.Combine(destModPath, "ModInfo.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(outModInfo)!);

        if (!string.IsNullOrEmpty(srcModInfo) && File.Exists(srcModInfo))
        {
            try
            {
                var xdoc = System.Xml.Linq.XDocument.Load(srcModInfo);
                // Sommige bestanden hebben <xml><ModInfo>... of alleen <xml> met velden.
                var container = xdoc.Root?.Element("ModInfo") ?? xdoc.Root;
                if (container != null)
                {
                    var verEl = container.Element("Version");
                    if (verEl == null)
                    {
                        verEl = new System.Xml.Linq.XElement("Version", new System.Xml.Linq.XAttribute("value", fullVersion));
                        container.Add(verEl);
                    }
                    else
                    {
                        if (verEl.Attribute("value") != null) verEl.SetAttributeValue("value", fullVersion);
                        else verEl.Value = fullVersion;
                    }
                }
                xdoc.Save(outModInfo);
            }
            catch
            {
                File.Copy(srcModInfo, outModInfo, true);
            }
        }
        else
        {
            // Minimale ModInfo genereren als niks bestaat
            File.WriteAllText(outModInfo,
    $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<xml><ModInfo>
  <Name value=""{mod.Name}""/>
  <DisplayName value=""{mod.Name}""/>
  <Description value=""Created with Mod Designer"" />
  <Author value=""You""/>
  <Version value=""{fullVersion}""/>
  <Website value="""" />
</ModInfo></xml>");
        }

        // --- Config kopiëren voor gekozen GV (ongewijzigde basis, maar naar nieuwe dest) ---
        string srcConfig = string.IsNullOrEmpty(chosenGv)
            ? ResolveConfigFolder(mod.ModFolder)
            : Path.Combine(mod.ModFolder, "XML", chosenGv, "Config");
        if (!Directory.Exists(srcConfig))
        {
            EditorUtility.DisplayDialog("Missing Config", $"Config folder not found for GV '{chosenGv}'.\n{srcConfig}", "OK");
            return;
        }
        string destConfig = Path.Combine(destModPath, "Config");
        CopyDirectory(srcConfig, destConfig);

        // --- Resources & UIAtlases etc. (ongewijzigd) ---
        string xmlRoot = Path.Combine(mod.ModFolder, "XML");
        foreach (string dir in new[] { "Resources", "UIAtlases", "ItemIcons" })
        {
            string src = Path.Combine(xmlRoot, dir);
            if (Directory.Exists(src))
                CopyDirectory(src, Path.Combine(destModPath, dir));
        }
        // losse bestanden in XML root meenemen (Localization.txt, etc.)
        foreach (string file in Directory.GetFiles(xmlRoot))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("ModInfo.xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Localization.txt", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.Combine(destModPath, "Config"));
                File.Copy(file, Path.Combine(destModPath, "Config", "Localization.txt"), true);
            }
        }

        // --- NIEUW: README meenemen indien aanwezig ---
        CopyReadmeIfExists(mod.ModFolder, chosenGv, destModPath);
        CopyModSettingsIfExists(mod.ModFolder, chosenGv, destModPath);

        EditorUtility.DisplayDialog("Mod exported",
            $"Mod '{mod.Name}' exported to:\n{destModPath}\n\n" +
            $"Game Version: {chosenGv}\nVersion in ModInfo: {fullVersion}",
            "OK");
    }

    void CopyReadmeIfExists(string modRoot, string chosenGv, string destModPath)
    {
        // Zoeken in volgorde: versie-specifiek → XML root → mod root
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(chosenGv))
        {
            candidates.Add(Path.Combine(modRoot, "XML", chosenGv, "readme.md"));
            candidates.Add(Path.Combine(modRoot, "XML", chosenGv, "readme.txt"));
        }
        candidates.Add(Path.Combine(modRoot, "XML", "readme.md"));
        candidates.Add(Path.Combine(modRoot, "XML", "readme.txt"));
        candidates.Add(Path.Combine(modRoot, "readme.md"));
        candidates.Add(Path.Combine(modRoot, "readme.txt"));

        string found = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrEmpty(found))
        {
            string dest = Path.Combine(destModPath, Path.GetFileName(found));
            try { File.Copy(found, dest, true); } catch { /* ignore */ }
        }
    }

    void CopyModSettingsIfExists(string modRoot, string chosenGv, string destModPath)
    {
        // Zoeken in volgorde: versie-specifiek → XML root → mod root
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(chosenGv))
        {
            candidates.Add(Path.Combine(modRoot, "XML", chosenGv, "ModSettings.xml"));
            candidates.Add(Path.Combine(modRoot, "XML", chosenGv, "modsettings.txt"));
        }
        candidates.Add(Path.Combine(modRoot, "XML", "ModSettings.txt"));
        candidates.Add(Path.Combine(modRoot, "modsettings.txt"));

        string found = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrEmpty(found))
        {
            string dest = Path.Combine(destModPath, Path.GetFileName(found));
            try { File.Copy(found, dest, true); } catch { /* ignore */ }
        }
    }


    void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            if (Path.GetExtension(file).Equals(".meta", System.StringComparison.OrdinalIgnoreCase)) continue;
            string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    // ModDesignerWindow.cs - updated ExportAllMods() function
    void ExportAllMods()
    {
        if (string.IsNullOrEmpty(exportModsFolder))
        {
            EditorUtility.DisplayDialog("Export folder", "No export folder set.", "OK");
            return;
        }
        foreach (var m in modules) m.Save();
        foreach (var mod in mods)
        {
            ExportMod(mod);
        }
        EditorUtility.DisplayDialog("Export complete", $"All mods have been exported to {exportModsFolder}.", "OK");
    }


    void BuildAssetBundleForMod(ModEntry mod)
    {
        // Zoek prefab assets in de mod Prefabs map
        string prefabsFolder = Path.Combine(mod.ModFolder, "Prefabs");
        string resourcesFolder = Path.Combine(mod.ModFolder, "XML", "Resources");
        Directory.CreateDirectory(resourcesFolder);
        if (!Directory.Exists(prefabsFolder))
        {
            // Geen Prefabs map, niets te bundelen
            return;
        }
        string[] prefabPaths = Directory.GetFiles(prefabsFolder, "*.prefab", SearchOption.AllDirectories)
                                        .Select(ModDesignerWindow.SystemPathToAssetPath)
                                        .Where(p => !string.IsNullOrEmpty(p))
                                        .ToArray();
        if (prefabPaths.Length == 0)
        {
            // Geen prefab bestanden
            return;
        }
        UnityEngine.Object[] assets = prefabPaths.Select(p => AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p))
                                     .Where(obj => obj != null).ToArray();
        if (assets.Length == 0) return;

        string bundleName = mod.Name.Replace(" ", "") + ".unity3d";
        string outputPath = Path.Combine(resourcesFolder, bundleName);
#pragma warning disable CS0618
        Selection.objects = assets;
        BuildPipeline.BuildAssetBundle(assets[0], assets, outputPath,
            BuildAssetBundleOptions.CollectDependencies | BuildAssetBundleOptions.CompleteAssets,
            BuildTarget.StandaloneWindows);
        Selection.objects = assets;
#pragma warning restore CS0618

        // Update Model properties in XML naar de nieuwe bundlename
        string configPath = mod.ConfigFolder;  // pad waar blocks.xml/items.xml staan
        string bundleFile = bundleName;
        // Controlleer zowel blocks.xml als items.xml
        string[] xmlFiles = { "blocks.xml", "items.xml" };
        foreach (string xmlFile in xmlFiles)
        {
            string xmlFullPath = Path.Combine(configPath, xmlFile);
            if (!File.Exists(xmlFullPath)) continue;
            var xdoc = XDocument.Load(xmlFullPath);
            bool changed = false;
            foreach (var prop in xdoc.Descendants("property"))
            {
                XAttribute nameAttr = prop.Attribute("name");
                XAttribute valueAttr = prop.Attribute("value");
                if (nameAttr != null && valueAttr != null && nameAttr.Value == "Model")
                {
                    string val = valueAttr.Value;
                    // Zoek naar pattern binnen value
                    // Voorbeeld: "#@modfolder:Resources/OldName.unity3d?PrefabName"
                    int resIdx = val.IndexOf("Resources/", StringComparison.OrdinalIgnoreCase);
                    int qIdx = val.IndexOf('?');
                    if (resIdx >= 0 && qIdx > resIdx)
                    {
                        string currentBundle = val.Substring(resIdx + "Resources/".Length, qIdx - (resIdx + "Resources/".Length));
                        if (!currentBundle.Equals(bundleFile, StringComparison.OrdinalIgnoreCase))
                        {
                            // Vervang bundlename
                            string newVal = val.Substring(0, resIdx + "Resources/".Length) + bundleFile + val.Substring(qIdx);
                            valueAttr.Value = newVal;
                            changed = true;
                        }
                    }
                }
            }
            if (changed)
            {
                xdoc.Save(xmlFullPath);
            }
        }
    }


    // ModDesignerWindow.cs - updated BuildAssetsForSelectedMod() function
    void BuildAssetsForSelectedMod()
    {
        if (selectedModIndex < 0 || selectedModIndex >= mods.Count) return;
        var mod = mods[selectedModIndex];
        var prefabsFolder = Path.Combine(mod.ModFolder, "Prefabs");
        var resourcesFolder = Path.Combine(mod.ModFolder, "XML", "Resources");
        Directory.CreateDirectory(resourcesFolder);
        if (!Directory.Exists(prefabsFolder))
        {
            EditorUtility.DisplayDialog("No Prefabs folder", $"Folder not found:\n{prefabsFolder}", "OK");
            return;
        }
        var prefabPaths = Directory.GetFiles(prefabsFolder, "*.prefab", SearchOption.AllDirectories)
                                   .Select(SystemPathToAssetPath)
                                   .Where(p => !string.IsNullOrEmpty(p))
                                   .ToArray();
        if (prefabPaths.Length == 0)
        {
            EditorUtility.DisplayDialog("No prefabs", "There are no .prefab files found.", "OK");
            return;
        }
        var objs = prefabPaths.Select(p => AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p))
                              .Where(o => o != null)
                              .ToArray();
        var bundleName = mods[selectedModIndex].Name.Replace(" ", "") + ".unity3d";
        var outPath = Path.Combine(resourcesFolder, bundleName);
#pragma warning disable CS0618
        Selection.objects = objs;
        BuildPipeline.BuildAssetBundle(
            objs.First(),
            objs,
            outPath,
            BuildAssetBundleOptions.CollectDependencies | BuildAssetBundleOptions.CompleteAssets,
            BuildTarget.StandaloneWindows
        );
#pragma warning restore CS0618
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("AssetBundle ready", $"AssetBundle built at:\n{outPath}", "OK");
    }


    public static string? SystemPathToAssetPath(string sys)
    {
        sys = sys.Replace('\\', '/');
        var dataPath = Application.dataPath.Replace('\\', '/');
        if (!sys.StartsWith(dataPath)) return null;
        return "Assets" + sys.Substring(dataPath.Length);
    }

    void RefreshModList()
    {
        mods.Clear();
        selectedModIndex = -1;
        if (!Directory.Exists(rootModsFolder)) return;

        foreach (var dir in Directory.EnumerateDirectories(rootModsFolder))
        {
            var cfg = ResolveConfigFolder(dir);
            if (cfg == null) continue;
            mods.Add(new ModEntry
            {
                Name = Path.GetFileName(dir),
                ModFolder = dir,
                ConfigFolder = cfg
            });
        }
        if (mods.Count > 0) selectedModIndex = 0;
        InitializeModulesForCurrentSelection();
    }

    string? ResolveConfigFolder(string modFolder)
    {
        var xml = Path.Combine(modFolder, "XML");
        var xmlCfg = Path.Combine(xml, "Config");
        var cfg = Path.Combine(modFolder, "Config");

        if (Directory.Exists(xmlCfg)) return xmlCfg;        // primary expected path
        if (Directory.Exists(cfg)) return cfg;              // fallback path
        if (Directory.Exists(xml)) return xml;              // last resort (legacy mods)
        return null;
    }

    void InitializeModulesForCurrentSelection()
    {
        var ctx = new ModContext { GameConfigPath = gameConfigPath };
        if (selectedModIndex >= 0 && selectedModIndex < mods.Count)
        {
            var mod = mods[selectedModIndex];

            currentManifest = ModManifest.Load(mod.ModFolder);

            // <<< voeg toe
            PruneManifestVersionsForSelectedMod(save: true);

            // daarna pas selectedGameVersion bepalen
            string cachedGv;
            if (lastSelectedGvPerMod.TryGetValue(mod.Name, out cachedGv))
                selectedGameVersion = cachedGv;
            else
                selectedGameVersion = string.IsNullOrEmpty(currentManifest.currentGameVersion) ? "" : currentManifest.currentGameVersion;

            // Stel de ConfigFolder van de ModEntry in
            mod.ConfigFolder = ConfigPathFor(mod, selectedGameVersion) ?? ResolveConfigFolder(mod.ModFolder);

            // (2) Context
            ctx.ModFolder = mod.ModFolder;
            ctx.ModConfigPath = mod.ConfigFolder;
            ctx.ModName = mod.Name;

            var gvi = string.IsNullOrEmpty(selectedGameVersion) ? null : currentManifest.GetOrCreate(selectedGameVersion);
            ctx.SelectedGameVersion = selectedGameVersion ?? "";
            ctx.IsVersionLocked = gvi?.locked ?? false;
            ctx.UnityTarget = gvi?.unity ?? "";
            ctx.ModVersion = gvi?.modVersion ?? "1.0";
            ctx.ManifestPath = ModManifest.GetManifestPath(mod.ModFolder);
        }

        moduleAvailability.Clear();
        foreach (var m in modules)
        {
            m.Initialize(ctx);

            if (m is EntryXmlModule entry)
            {
                var path = Path.Combine(ctx.ModConfigPath ?? "", entry.FileName);
                moduleAvailability[entry.ModuleName] = File.Exists(path);
            }
            else if (m is RecipeConfigModule recipe)
            {
                var path = Path.Combine(ctx.ModConfigPath ?? "", recipe.FileName);
                moduleAvailability[recipe.ModuleName] = File.Exists(path);
            }
            else
            {
                moduleAvailability[m.ModuleName] = true;
            }
        }
        Repaint();
    }


    // ModDesignerWindow.cs - updated CreateNewMod() function
    void CreateNewMod()
    {
        if (!Directory.Exists(rootModsFolder))
        {
            EditorUtility.DisplayDialog("Root Mods Folder", "Please set the Root Mods Folder first.", "OK");
            return;
        }
        EditorApplication.delayCall += () =>
        {
            if (!EditorPrompt.PromptString("New Mod", "Mod Name:", "MyNewMod", out var name)) return;
            var modRoot = Path.Combine(rootModsFolder, name);
            var xmlFolder = Path.Combine(modRoot, "XML");
            var configFolder = Path.Combine(xmlFolder, "Config");
            Directory.CreateDirectory(configFolder);
            var modInfoPath = Path.Combine(xmlFolder, "ModInfo.xml");
            if (!File.Exists(modInfoPath))
            {
                Directory.CreateDirectory(xmlFolder);
                File.WriteAllText(modInfoPath,
        $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<xml><ModInfo>
  <Name value=""{name}""/>
  <DisplayName value=""{name}""/>
  <Description value=""Created with Mod Designer"" />
  <Author value=""You""/>
  <Version value=""1.0.0""/>
  <Website value="""" />
</ModInfo></xml>");
            }
            AssetDatabase.Refresh();
            RefreshModList();
            selectedModIndex = mods.FindIndex(m => m.Name == name);
            InitializeModulesForCurrentSelection();
            Repaint();
        };
    }

    void LoadManifestForSelected()
    {
        currentManifest = null;
        selectedGameVersion = "";
        if (selectedModIndex < 0 || selectedModIndex >= mods.Count) return;

        var mod = mods[selectedModIndex];
        currentManifest = ModManifest.Load(mod.ModFolder);

        // >>> NIEUW: opruimen van niet-bestaande versie-mappen
        PruneManifestVersionsForSelectedMod(save: true);

        // kies gv: manifest → cache (alleen als nog bestaat) → leeg
        if (!string.IsNullOrEmpty(currentManifest.currentGameVersion))
        {
            selectedGameVersion = currentManifest.currentGameVersion;
        }
        else if (lastSelectedGvPerMod.TryGetValue(mod.Name, out var gv) && currentManifest.versions.ContainsKey(gv))
        {
            selectedGameVersion = gv;
        }
        else
        {
            selectedGameVersion = "";
        }

        EnsureSelectedGvIsValid(scheduleApply: false);
        ApplySelectedGameVersionToContext();
    }


    void SaveManifestForSelected()
    {
        if (selectedModIndex < 0 || selectedModIndex >= mods.Count) return;
        if (currentManifest == null) return;

        currentManifest.currentGameVersion = selectedGameVersion ?? "";
        currentManifest.Save(mods[selectedModIndex].ModFolder);
    }

    void ApplySelectedGameVersionToContext()
    {
        if (selectedModIndex < 0 || selectedModIndex >= mods.Count) { _applyGvScheduled = false; return; }

        var mod = mods[selectedModIndex];

        // Config-pad naar de GV mappen
        var gvPath = ConfigPathFor(mod, selectedGameVersion);
        if (!string.IsNullOrEmpty(gvPath))
            mod.ConfigFolder = gvPath;

        // Context opbouwen
        var ctx = new ModContext
        {
            GameConfigPath = gameConfigPath,
            ModFolder = mod.ModFolder,
            ModConfigPath = mod.ConfigFolder,
            ModName = mod.Name,
            ManifestPath = ModManifest.GetManifestPath(mod.ModFolder),
            SelectedGameVersion = selectedGameVersion ?? ""
        };

        var gvi = string.IsNullOrEmpty(selectedGameVersion) ? null : currentManifest?.GetOrCreate(selectedGameVersion);
        ctx.IsVersionLocked = gvi?.locked ?? false;
        ctx.UnityTarget = gvi?.unity ?? "";
        ctx.ModVersion = gvi?.modVersion ?? "1.0";

        // Modules re-initialiseren met nieuwe context (zonder her-enter van je vensterlogica)
        foreach (var m in modules) m.Initialize(ctx);

        // Availability opnieuw berekenen (zodat je Modules-lijst kleuren/knoppen klopt)
        moduleAvailability.Clear();
        foreach (var m in modules)
        {
            if (m is EntryXmlModule entry)
            {
                var path = Path.Combine(ctx.ModConfigPath ?? "", entry.FileName);
                moduleAvailability[entry.ModuleName] = File.Exists(path);
            }
            else if (m is RecipeConfigModule recipe)
            {
                var path = Path.Combine(ctx.ModConfigPath ?? "", recipe.FileName);
                moduleAvailability[recipe.ModuleName] = File.Exists(path);
            }
            else
            {
                moduleAvailability[m.ModuleName] = true;
            }
        }

        _applyGvScheduled = false;
        Repaint();
    }

    // vervang je bestaande CopyDirectoryNoMeta door deze
    void CopyDirectoryNoMeta(string src, string dst, string skipSubtree = null)
    {
        // Beveiliging: nooit in eigen subtree kopiëren
        if (IsSubPathOf(dst, src) || NormalizePath(dst) == NormalizePath(src))
        {
            Debug.LogError($"[ModDesigner] Refusing to copy '{src}' into its own subtree '{dst}'.");
            return;
        }

        Directory.CreateDirectory(dst);

        foreach (var f in Directory.GetFiles(src, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetExtension(f).Equals(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        }

        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.TopDirectoryOnly))
        {
            // skip de net aangemaakte GV (of andere uitgesloten subtree)
            if (!string.IsNullOrEmpty(skipSubtree) &&
                (NormalizePath(d) == NormalizePath(skipSubtree) || IsSubPathOf(d, skipSubtree)))
                continue;

            CopyDirectoryNoMeta(d, Path.Combine(dst, Path.GetFileName(d)), skipSubtree);
        }
    }


    void NewGameVersionFlow()
    {
        if (selectedModIndex < 0 || selectedModIndex >= mods.Count) return;
        var mod = mods[selectedModIndex];

        if (!EditorPrompt.PromptString("New Game Version", "Version (e.g. A21.2 or 2.4):", "A21.2", out var newGv)) return;
        newGv = newGv.Trim();
        if (string.IsNullOrEmpty(newGv)) return;

        if (currentManifest == null) currentManifest = new ModManifest();

        // <<< verplaats deze omhoog
        PruneManifestVersionsForSelectedMod(save: true);

        // pas nu checken
        if (currentManifest.versions.ContainsKey(newGv))
        {
            EditorUtility.DisplayDialog("Exists", $"Game version '{newGv}' already exists.", "OK");
            return;
        }

        bool firstEver = (currentManifest.ListGameVersions().Length == 0);

        // --- bron bepalen ---
        string src = null;
        string srcLabel = "(none)";
        if (firstEver && Directory.Exists(gameConfigPath))
        {
            src = gameConfigPath;                  // Base game Data/Config
            srcLabel = "(Base Game Data/Config)";
        }
        else
        {
            var options = new List<string>();
            var gvList = currentManifest.ListGameVersions();
            options.AddRange(gvList);

            var legacyCfg = ResolveConfigFolder(mod.ModFolder);
            bool hasLegacy = !string.IsNullOrEmpty(legacyCfg) && Directory.Exists(legacyCfg);
            if (Directory.Exists(gameConfigPath)) options.Insert(0, "(Base Game Data/Config)");
            if (hasLegacy) options.Add("(Legacy XML/Config)");

            int pick = EntryXmlModule.SimpleListPicker.Show(
                "Copy from which version?",
                "Choose a source to import from",
                options.ToArray()
            );

            if (pick >= 0)
            {
                string chosen = options[pick];
                srcLabel = chosen;
                if (chosen == "(Base Game Data/Config)") src = gameConfigPath;
                else if (chosen == "(Legacy XML/Config)") src = legacyCfg;
                else src = Path.Combine(VersionRootFor(mod), chosen, "Config"); // GV binnen je mod
            }
        }

        // --- bestemmingspaden ---
        string destGvRoot = Path.Combine(VersionRootFor(mod), newGv);   // XML/<newGv>
        string destConfig = Path.Combine(destGvRoot, "Config");

        // Bestemming vol? Alleen checken als hij al bestond
        if (Directory.Exists(destGvRoot) && DirHasContent(destGvRoot))
        {
            int how = EditorUtility.DisplayDialogComplex(
                "Destination not empty",
                $"Destination has content:\n{destGvRoot}\n\nHow to proceed?",
                "Replace (clean first)", "Merge", "Cancel");
            if (how == 2) return;
            if (how == 0) ClearDirectory(destGvRoot);
        }

        // --- detecteer of bron een GV-root binnen deze mod is ---
        string srcGvRoot = null;
        if (!string.IsNullOrEmpty(src))
        {
            var parent = Directory.GetParent(src)?.FullName; // verwacht XML/<oldGv>
            var versionRoot = VersionRootFor(mod);           // ...\XML

            // Alleen GV-root als parent een submap is van XML én niet exact de XML-map zelf,
            // én er een Config-submap onder zit.
            if (!string.IsNullOrEmpty(parent) &&
                IsSubPathOf(parent, versionRoot) &&
                !string.Equals(NormalizePath(parent), NormalizePath(versionRoot), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(parent, "Config")))
            {
                srcGvRoot = parent; // bv ...\XML\2.3
            }
        }


        // --- importmodus ---
        int importChoice = 2; // 0=Copy, 1=Move, 2=Skip
        if (!string.IsNullOrEmpty(src) && Directory.Exists(src))
        {
            bool canMove = !string.IsNullOrEmpty(srcGvRoot); // move alleen als bron een echte GV-root is

            if (canMove)
            {
                importChoice = EditorUtility.DisplayDialogComplex(
                    "Import files",
                    $"Import from:\n{srcLabel}\n{src}\n\nChoose import mode:",
                    "Copy", "Move (copy, then delete source)", "Skip");
            }
            else
            {
                bool copy = EditorUtility.DisplayDialog(
                    "Import files",
                    $"Import from:\n{srcLabel}\n{src}\n\nMove is not available for this source.",
                    "Copy", "Cancel");
                importChoice = copy ? 0 : 2;
            }
        }


        // --- kopiëren met preview ---
        List<CopyCandidate> plan = BuildNewGvCopyCandidates(mod, newGv, src, srcGvRoot);

        // Bestemmingspaden
        Directory.CreateDirectory(destGvRoot);

        // Laat preview met vinkjes zien
        string srcNice =
            !string.IsNullOrEmpty(srcGvRoot) ? srcGvRoot :
            (!string.IsNullOrEmpty(src) ? src : "(none)");

        bool ok = CopyPreviewWindow.Show(
            title: "Kopiëren naar nieuwe versie",
            subtitle: $"Bron: {srcLabel}\n{srcNice}\nDoel: {destGvRoot}",
            destPath: destGvRoot,
            items: plan
        );
        if (!ok) return;

        // Uitvoeren
        ExecuteCopyCandidates(destGvRoot, plan);

        // Indien gebruiker 'Move' gekozen had: bron opruimen met extra 'Toon nieuwe mod map' knop
        if (importChoice == 1 && !string.IsNullOrEmpty(src))
        {
            string toDelete = !string.IsNullOrEmpty(srcGvRoot) ? srcGvRoot : src;
            var versionRoot = VersionRootFor(mod);
            var legacyConfig = Path.Combine(versionRoot, "Config");

            if (string.Equals(NormalizePath(toDelete), NormalizePath(versionRoot), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizePath(toDelete), NormalizePath(legacyConfig), StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("[ModDesigner] Skipping delete of XML root/legacy Config.");
            }
            else
            {
                SafeDeleteSourceTreeIfAllowed(toDelete, mod, destGvRoot);
            }
        }


        // --- zorg dat er ModInfo.xml is in de nieuwe GV-root ---
        Directory.CreateDirectory(destGvRoot); // maak nu pas zeker aan
        string gvModInfo = Path.Combine(destGvRoot, "ModInfo.xml");
        if (!File.Exists(gvModInfo))
        {
            // probeer nog een fallback ModInfo te vinden (XML/ModInfo.xml of root/ModInfo.xml)
            string srcModInfo = null;
            string rootXmlModInfo = Path.Combine(mod.ModFolder, "XML", "ModInfo.xml");
            string legacyModInfo = Path.Combine(mod.ModFolder, "ModInfo.xml");
            if (File.Exists(rootXmlModInfo)) srcModInfo = rootXmlModInfo;
            else if (File.Exists(legacyModInfo)) srcModInfo = legacyModInfo;

            if (!string.IsNullOrEmpty(srcModInfo))
                File.Copy(srcModInfo, gvModInfo, true);
            else
            {
                File.WriteAllText(gvModInfo,
    $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<xml><ModInfo>
  <Name value=""{mod.Name}""/>
  <DisplayName value=""{mod.Name}""/>
  <Description value=""Created with Mod Designer"" />
  <Author value=""You""/>
  <Version value=""{ComposeFullVersionString(newGv, "1.0")}""/>
  <Website value="""" />
</ModInfo></xml>");
            }
        }

        // --- manifest bijwerken ---
        var gvi = new ModManifest.GameVersionInfo
        {
            modVersion = "1.0",
            unity = "",
            locked = false
        };
        if (firstEver && TryDetectUnityFromInstall(out var unityVer) && !string.IsNullOrEmpty(unityVer))
            gvi.unity = unityVer;

        currentManifest.versions[newGv] = gvi;
        currentManifest.currentGameVersion = newGv;
        selectedGameVersion = newGv;
        lastSelectedGvPerMod[mod.Name] = selectedGameVersion;

        SaveManifestForSelected();
        ApplySelectedGameVersionToContext();
    }



    bool TryDetectUnityFromInstall(out string unityVer)
    {
        unityVer = "";
        try
        {
            if (string.IsNullOrEmpty(gameConfigPath) || !Directory.Exists(gameConfigPath)) return false;
            // gameConfigPath = .../Data/Config → gameRoot = twee niveaus omhoog
            var dataDir = Directory.GetParent(gameConfigPath);
            var gameRoot = dataDir?.Parent?.FullName;
            if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot)) return false;
            return UnityVersionUtil.TryGetUnityFromUnityPlayer(gameRoot, out unityVer);
        }
        catch { return false; }
    }



    void RenameGameVersionFlow()
    {
        if (selectedModIndex < 0 || selectedModIndex >= mods.Count) return;
        if (string.IsNullOrEmpty(selectedGameVersion))
        {
            EditorUtility.DisplayDialog("No version", "Select a game version first.", "OK"); return;
        }
        var mod = mods[selectedModIndex];
        if (!EditorPrompt.PromptString("Rename Game Version", "New name (e.g. 2.5):", selectedGameVersion, out var newName)) return;
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == selectedGameVersion) return;

        string oldDir = Path.Combine(VersionRootFor(mod), selectedGameVersion);
        string newDir = Path.Combine(VersionRootFor(mod), newName);
        if (Directory.Exists(newDir))
        {
            EditorUtility.DisplayDialog("Exists", $"Folder for '{newName}' already exists.", "OK"); return;
        }

        if (Directory.Exists(oldDir)) Directory.Move(oldDir, newDir);
        if (currentManifest != null && currentManifest.versions.ContainsKey(selectedGameVersion))
        {
            currentManifest.versions[newName] = currentManifest.versions[selectedGameVersion];
            currentManifest.versions.Remove(selectedGameVersion);
        }

        selectedGameVersion = newName;
        SaveManifestForSelected();
        ApplySelectedGameVersionToContext();
    }

    void DeleteGameVersionFlow()
    {
        if (selectedModIndex < 0 || selectedModIndex >= mods.Count) return;
        if (string.IsNullOrEmpty(selectedGameVersion))
        {
            EditorUtility.DisplayDialog("No version", "Select a game version first.", "OK"); return;
        }
        var mod = mods[selectedModIndex];

        // safety
        if (currentManifest?.versions.TryGetValue(selectedGameVersion, out var gvi) == true && gvi.locked)
        {
            EditorUtility.DisplayDialog("Locked", "Unlock this version first.", "OK"); return;
        }

        if (!EditorUtility.DisplayDialog("Delete", $"Delete game version '{selectedGameVersion}'?\n(This removes XML/{selectedGameVersion} folder)", "Yes", "No"))
            return;

        string dir = Path.Combine(VersionRootFor(mod), selectedGameVersion);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        currentManifest?.versions.Remove(selectedGameVersion);

        // kies nieuwe selectie
        var all = currentManifest?.ListGameVersions() ?? new string[0];
        selectedGameVersion = all.FirstOrDefault() ?? "";
        PruneManifestVersionsForSelectedMod(save: true);
        SaveManifestForSelected();
        ApplySelectedGameVersionToContext();
    }

    void ToggleLockForSelectedVersion()
    {
        if (string.IsNullOrEmpty(selectedGameVersion) || currentManifest == null) return;
        var gvi = currentManifest.GetOrCreate(selectedGameVersion);
        gvi.locked = !gvi.locked;
        SaveManifestForSelected();
        ApplySelectedGameVersionToContext();
    }

    void SetModVersionForSelected(string newVersion)
    {
        if (currentManifest == null) return;

        // Zorg dat er een geldige GV is
        EnsureSelectedGvIsValid();
        if (string.IsNullOrEmpty(selectedGameVersion))
        {
            EditorUtility.DisplayDialog("No Game Version", "Create or select a Game Version first.", "OK");
            return;
        }

        var gvi = currentManifest.GetOrCreate(selectedGameVersion);
        gvi.modVersion = string.IsNullOrWhiteSpace(newVersion) ? "1.0" : newVersion.Trim();

        // Meteen naar disk
        currentManifest.Save(mods[selectedModIndex].ModFolder);
    }


    string ComposeFullVersionString(string gameVersion, string modVersion)
    {
        // "2.4" + "1.1" => "2.4.1.1"
        gameVersion = (gameVersion ?? "").Trim('.');
        modVersion = (modVersion ?? "").Trim('.');
        if (string.IsNullOrEmpty(gameVersion)) return string.IsNullOrEmpty(modVersion) ? "1.0" : modVersion;
        if (string.IsNullOrEmpty(modVersion)) return gameVersion;
        return gameVersion + "." + modVersion;
    }

    void EnsureSelectedGvIsValid(bool scheduleApply = true)
    {
        if (currentManifest == null) return;
        var versions = currentManifest.ListGameVersions();
        if (versions == null || versions.Length == 0) return;

        if (string.IsNullOrEmpty(selectedGameVersion) ||
            Array.IndexOf(versions, selectedGameVersion) < 0)
        {
            var mod = (selectedModIndex >= 0 && selectedModIndex < mods.Count) ? mods[selectedModIndex] : null;
            selectedGameVersion = versions[0];
            if (mod != null) lastSelectedGvPerMod[mod.Name] = selectedGameVersion;
            SaveManifestForSelected();

            if (scheduleApply)
            {
                if (!_applyGvScheduled)
                {
                    _applyGvScheduled = true;
                    EditorApplication.delayCall += () =>
                    {
                        ApplySelectedGameVersionToContext();
                    };
                }
            }
        }
    }

    // --- helpers om copy candidates op te bouwen en uit te voeren ---
    List<CopyCandidate> BuildNewGvCopyCandidates(ModEntry mod, string chosenGv, string src, string srcGvRoot)
    {
        // Doel: altijd de juiste set tonen: Config + (ModInfo, ModSettings, readme's)
        var items = new List<CopyCandidate>();
        string xmlRoot = Path.Combine(mod.ModFolder, "XML");
        string destRootName = chosenGv; // komt terecht in XML/<gv>

        // 1) Config directory bepalen
        //    - als srcGvRoot != null -> Config zit onder srcGvRoot/Config
        //    - anders: als src wijst naar een Config-map -> die
        //    - anders: probeer legacy Config: mod.XML/Config
        string srcConfig =
            !string.IsNullOrEmpty(srcGvRoot) ? Path.Combine(srcGvRoot, "Config") :
            (!string.IsNullOrEmpty(src) && Directory.Exists(src) && Path.GetFileName(src).Equals("Config", StringComparison.OrdinalIgnoreCase)) ? src :
            Path.Combine(xmlRoot, "Config");

        if (Directory.Exists(srcConfig))
            items.Add(new CopyCandidate { Label = "Config/", SourcePath = srcConfig, DestRelPath = "Config", IsDir = true });

        // 2) ModInfo.xml: probeer in volgorde gv-root -> XML -> root
        var candidatesModInfo = new[]
        {
        !string.IsNullOrEmpty(srcGvRoot) ? Path.Combine(srcGvRoot, "ModInfo.xml") : null,
        Path.Combine(xmlRoot, "ModInfo.xml"),
        Path.Combine(mod.ModFolder, "ModInfo.xml")
    }.Where(p => !string.IsNullOrEmpty(p)).ToList();

        string pickModInfo = candidatesModInfo.FirstOrDefault(File.Exists);
        if (!string.IsNullOrEmpty(pickModInfo))
        {
            items.Add(new CopyCandidate { Label = "ModInfo.xml", SourcePath = pickModInfo, DestRelPath = "ModInfo.xml", IsDir = false });
        }
        else
        {
            // Virtueel item: genereren als de gebruiker dit aanvinkt
            items.Add(new CopyCandidate { Label = "ModInfo.xml (genereren)", SourcePath = "", DestRelPath = "ModInfo.xml", IsDir = false, IsVirtualGenerate = true });
        }

        // 3) ModSettings (xml/txt) – neem de eerste die bestaat
        var modSettingsNames = new[] { "ModSettings.xml", "ModSettings.txt", "modsettings.txt" };
        string pickSettings =
            (!string.IsNullOrEmpty(srcGvRoot) ? modSettingsNames.Select(n => Path.Combine(srcGvRoot, n)).FirstOrDefault(File.Exists) : null)
            ?? modSettingsNames.Select(n => Path.Combine(xmlRoot, n)).FirstOrDefault(File.Exists)
            ?? modSettingsNames.Select(n => Path.Combine(mod.ModFolder, n)).FirstOrDefault(File.Exists);
        if (!string.IsNullOrEmpty(pickSettings))
        {
            items.Add(new CopyCandidate
            {
                Label = Path.GetFileName(pickSettings),
                SourcePath = pickSettings,
                DestRelPath = Path.GetFileName(pickSettings),
                IsDir = false
            });
        }

        // 4) readme.* (md/txt)
        string pickReadme =
            (!string.IsNullOrEmpty(srcGvRoot) ? new[] { "readme.md", "readme.txt" }.Select(n => Path.Combine(srcGvRoot, n)).FirstOrDefault(File.Exists) : null)
            ?? new[] { "readme.md", "readme.txt" }.Select(n => Path.Combine(xmlRoot, n)).FirstOrDefault(File.Exists)
            ?? new[] { "readme.md", "readme.txt" }.Select(n => Path.Combine(mod.ModFolder, n)).FirstOrDefault(File.Exists);
        if (!string.IsNullOrEmpty(pickReadme))
        {
            items.Add(new CopyCandidate
            {
                Label = Path.GetFileName(pickReadme),
                SourcePath = pickReadme,
                DestRelPath = Path.GetFileName(pickReadme),
                IsDir = false
            });
        }

        return items;
    }

    void ExecuteCopyCandidates(string destGvRoot, List<CopyCandidate> items)
    {
        Directory.CreateDirectory(destGvRoot);
        foreach (var it in items.Where(i => i.Selected))
        {
            string destAbs = Path.Combine(destGvRoot, it.DestRelPath);
            if (it.IsVirtualGenerate)
            {
                // minimale ModInfo.xml genereren (zelfde schema als elders in je code)
                File.WriteAllText(destAbs,
    $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<xml><ModInfo>
  <Name value=""{mods[selectedModIndex].Name}""/>
  <DisplayName value=""{mods[selectedModIndex].Name}""/>
  <Description value=""Created with Mod Designer"" />
  <Author value=""You""/>
  <Version value=""{ComposeFullVersionString(selectedGameVersion, currentManifest?.GetOrCreate(selectedGameVersion)?.modVersion ?? "1.0")}""/>
  <Website value="""" />
</ModInfo></xml>");
                continue;
            }

            if (it.IsDir)
            {
                CopyDirectoryNoMeta(it.SourcePath, destAbs);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destAbs) ?? "");
                File.Copy(it.SourcePath, destAbs, true);
            }
        }
    }

    void PruneManifestVersionsForSelectedMod(bool save = true)
    {
        if (selectedModIndex < 0 || selectedModIndex >= mods.Count) return;
        if (currentManifest == null) return;

        var mod = mods[selectedModIndex];
        string versionRoot = VersionRootFor(mod); // ...\XML

        var toRemove = new List<string>();
        foreach (var kv in currentManifest.versions)
        {
            string gv = kv.Key;
            string gvDir = Path.Combine(versionRoot, gv);
            // We beschouwen een versie alleen geldig als de versie-map bestaat én er een Config-submap is
            bool exists = Directory.Exists(gvDir) && Directory.Exists(Path.Combine(gvDir, "Config"));
            if (!exists) toRemove.Add(gv);
        }

        // verwijder uit manifest
        foreach (var gv in toRemove) currentManifest.versions.Remove(gv);

        // als currentGameVersion of de cached selectie wegvalt, zet nieuwe veilige waarde
        bool touchedSelection = false;
        if (toRemove.Contains(currentManifest.currentGameVersion ?? ""))
        {
            currentManifest.currentGameVersion = currentManifest.ListGameVersions().FirstOrDefault() ?? "";
            selectedGameVersion = currentManifest.currentGameVersion;
            touchedSelection = true;
        }

        if (lastSelectedGvPerMod.TryGetValue(mod.Name, out var cached) && toRemove.Contains(cached))
        {
            // zet naar huidige geldige selectie of verwijder cache
            if (!string.IsNullOrEmpty(selectedGameVersion))
                lastSelectedGvPerMod[mod.Name] = selectedGameVersion;
            else
                lastSelectedGvPerMod.Remove(mod.Name);
            touchedSelection = true;
        }

        if (touchedSelection) Repaint();
        if (save) currentManifest.Save(mod.ModFolder);
    }

}
