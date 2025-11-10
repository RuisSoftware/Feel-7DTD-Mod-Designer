// Assets/Editor/ModDesignerWindow.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

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


    void BuildModulesList()
    {
        modules.Clear();
        // Always include ModInfo first
        modules.Add(new ModInfoModule());
        if (!Directory.Exists(gameConfigPath))
        {
            // Base config folder not set or not found – fallback to core config modules
            modules.Add(new EntryXmlModule("Blocks", "blocks.xml", "block"));
            modules.Add(new EntryXmlModule("Items", "items.xml", "item"));
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
            var firstEntry = baseDoc.Descendants().FirstOrDefault(e => e.Attribute("name") != null);
            string entryTag = firstEntry != null ? firstEntry.Name.LocalName : null;
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

        BuildModulesList();
        RefreshModList();
        InitializeModulesForCurrentSelection();
    }

    void OnDisable()
    {
        // Save paths to EditorPrefs
        EditorPrefs.SetString("MD_gameConfigPath", gameConfigPath);
        EditorPrefs.SetString("MD_rootModsFolder", rootModsFolder);
        EditorPrefs.SetString("MD_exportModsFolder", exportModsFolder);
    }

    // ModDesignerWindow.cs - updated OnGUI() function with resizable left pane logic
    void OnGUI()
    {
        GUILayout.Space(6);
        DrawTopBars();

        // Define main areas for mod list and content
        var area = new Rect(0, 70, position.width, position.height - 70);
        // Ensure leftWidth stays within reasonable bounds
        leftWidth = Mathf.Clamp(leftWidth, 150f, position.width - 400f);
        var leftRect = new Rect(area.x, area.y, leftWidth, area.height);
        var splitterRect = new Rect(leftRect.x + leftRect.width, area.y, 6, area.height);
        var rightRect = new Rect(splitterRect.x + splitterRect.width, area.y, area.width - leftWidth - 6, area.height);

        // Draw draggable vertical splitter
        EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);
        EditorGUI.DrawRect(new Rect(splitterRect.x + 2, splitterRect.y, 2, splitterRect.height), new Color(1f, 0.5f, 0f, 0.6f));
        if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition))
        {
            draggingLeftSplitter = true;
            initialMouseX = Event.current.mousePosition.x;
            initialLeftWidth = leftWidth;
            Event.current.Use();
        }
        if (draggingLeftSplitter && Event.current.type == EventType.MouseDrag)
        {
            float delta = Event.current.mousePosition.x - initialMouseX;
            leftWidth = Mathf.Clamp(initialLeftWidth + delta, 150f, position.width - 200f);
            Event.current.Use();
            Repaint();
        }
        if (draggingLeftSplitter && Event.current.type == EventType.MouseUp)
        {
            draggingLeftSplitter = false;
            Event.current.Use();
        }

        // Draw left mod list and right content
        DrawLeftPane(leftRect);
        DrawRightPane(rightRect);
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

    }

    // ModDesignerWindow.cs - updated DrawLeftPane() function
    void DrawLeftPane(Rect rect)
    {
        GUILayout.BeginArea(rect, EditorStyles.helpBox);
        GUILayout.Label("Mods", EditorStyles.boldLabel);

        // New mod and open folder buttons
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("New Mod..."))
        {
            EditorApplication.delayCall += CreateNewMod;
        }
        GUI.enabled = selectedModIndex >= 0 && selectedModIndex < mods.Count;
        if (GUILayout.Button("Open Folder") && selectedModIndex >= 0 && selectedModIndex < mods.Count)
        {
            EditorUtility.RevealInFinder(mods[selectedModIndex].ModFolder);
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        // Mods list
        modsScroll = GUILayout.BeginScrollView(modsScroll, GUILayout.ExpandHeight(true));
        Color accentColor = new Color(1f, 0.6f, 0.2f);
        for (int i = 0; i < mods.Count; i++)
        {
            GUI.backgroundColor = (i == selectedModIndex ? accentColor : Color.white);
            if (GUILayout.Button(mods[i].Name, GUILayout.ExpandWidth(true)))
            {
                selectedModIndex = i;
                InitializeModulesForCurrentSelection();
                Repaint();
            }
            GUI.backgroundColor = Color.white;
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }


    // ModDesignerWindow.cs - updated DrawRightPane() function
    void DrawRightPane(Rect rect)
    {
        bool useArea = rect.width > 1f && rect.height > 1f;
        bool areaStarted = false;
        try
        {
            if (useArea)
            {
                GUILayout.BeginArea(rect);
                areaStarted = true;
            }

            if (selectedModIndex < 0 || selectedModIndex >= mods.Count)
            {
                EditorGUILayout.HelpBox("Select a mod on the left or create a new one.", MessageType.Info);
                return;
            }

            var mod = mods[selectedModIndex];
            // Header
            GUILayout.Label(mod.Name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Config:", mod.ConfigFolder, EditorStyles.miniLabel);
            GUILayout.Space(6);

            // Module selection list
            GUILayout.Label("Modules", EditorStyles.boldLabel);
            var moduleNames = modules.Select(m => m.ModuleName).ToArray();
            float listHeight = Mathf.Min(moduleNames.Length, 10) * 25f;
            moduleScroll = GUILayout.BeginScrollView(moduleScroll, GUILayout.Height(listHeight));
            for (int i = 0; i < modules.Count; i++)
            {
                string name = modules[i].ModuleName;
                bool hasFile = moduleAvailability.TryGetValue(name, out var exists) && exists;
                var oldColor = GUI.color;
                GUI.color = hasFile ? Color.green : Color.red;
                GUIStyle style = (i == selectedModule) ? "Button" : "miniButton";
                if (GUILayout.Button(name, style)) selectedModule = i;
                GUI.color = oldColor;
            }
            GUILayout.EndScrollView();
            GUILayout.Space(6);

            // Prompt to create missing config file for selected module
            if (selectedModule >= 0 && selectedModule < modules.Count)
            {
                string cfgFolder = mods[selectedModIndex].ConfigFolder;
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
                    EditorGUILayout.BeginHorizontal();
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
                    {
                        EditorUtility.RevealInFinder(cfgFolder);
                    }
                    EditorGUILayout.EndHorizontal();
                    GUILayout.Space(6);
                }
            }

            // Module content (list and inspector panels)
            EditorGUILayout.BeginVertical(GUILayout.ExpandHeight(true));
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

            // Left panel: module entry list
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(340), GUILayout.ExpandHeight(true));
            try { modules[selectedModule].OnGUIList(new Rect()); } catch { }
            EditorGUILayout.EndVertical();

            GUILayout.Space(6);

            // Right panel: module inspector
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            try { modules[selectedModule].OnGUIInspector(new Rect()); } catch { }
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();

            // Bottom buttons for saving and exporting
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Save Current")) modules[selectedModule].Save();
            if (GUILayout.Button("Save All"))
            {
                foreach (var m in modules) m.Save();
                if (EditorUtility.DisplayDialog("AssetBundle", "Rebuild asset bundle for this mod?", "Yes", "No"))
                {
                    BuildAssetsForSelectedMod();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Export this mod")) ExportMod(mod);
            if (GUILayout.Button("Export all mods")) ExportAllMods();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }
        finally
        {
            if (areaStarted) GUILayout.EndArea();
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
        foreach (var m in modules) m.Save();  // save all modules (including Localization)
        BuildAssetBundleForMod(mod);
        string destModPath = Path.Combine(exportModsFolder, mod.Name);
        if (Directory.Exists(destModPath))
        {
            Directory.Delete(destModPath, true);
        }
        Directory.CreateDirectory(destModPath);
        string modInfoSrc = File.Exists(Path.Combine(mod.ModFolder, "ModInfo.xml"))
                            ? Path.Combine(mod.ModFolder, "ModInfo.xml")
                            : Path.Combine(mod.ModFolder, "XML", "ModInfo.xml");
        if (File.Exists(modInfoSrc))
        {
            File.Copy(modInfoSrc, Path.Combine(destModPath, "ModInfo.xml"), overwrite: true);
        }
        string xmlPath = Path.Combine(mod.ModFolder, "XML");
        if (Directory.Exists(xmlPath))
        {
            foreach (string dir in Directory.GetDirectories(xmlPath))
            {
                string dirName = Path.GetFileName(dir);
                if (dirName.Equals("Config", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("Resources", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("UIAtlases", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("ItemIcons", StringComparison.OrdinalIgnoreCase))
                {
                    string destDir = Path.Combine(destModPath, dirName);
                    CopyDirectory(dir, destDir);
                }
            }
            foreach (string file in Directory.GetFiles(xmlPath))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("ModInfo.xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName.Equals("Localization.txt", StringComparison.OrdinalIgnoreCase))
                {
                    string dest = Path.Combine(destModPath, "Config", "Localization.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest, true);
                }
                else if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    string dest = Path.Combine(destModPath, "Config", fileName);
                    Directory.CreateDirectory(Path.Combine(destModPath, "Config"));
                    File.Copy(file, dest, true);
                }
            }
        }
        else if (Directory.Exists(Path.Combine(mod.ModFolder, "Config")))
        {
            CopyDirectory(Path.Combine(mod.ModFolder, "Config"), Path.Combine(destModPath, "Config"));
            if (Directory.Exists(Path.Combine(mod.ModFolder, "Resources")))
                CopyDirectory(Path.Combine(mod.ModFolder, "Resources"), Path.Combine(destModPath, "Resources"));
            if (Directory.Exists(Path.Combine(mod.ModFolder, "UIAtlases")))
                CopyDirectory(Path.Combine(mod.ModFolder, "UIAtlases"), Path.Combine(destModPath, "UIAtlases"));
            if (File.Exists(Path.Combine(mod.ModFolder, "ModInfo.xml")))
                File.Copy(Path.Combine(mod.ModFolder, "ModInfo.xml"), Path.Combine(destModPath, "ModInfo.xml"), true);
        }
        EditorUtility.DisplayDialog("Mod exported", $"Mod '{mod.Name}' was copied to: {destModPath}", "OK");
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
            BuildAssetBundleForMod(mod);
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
            ctx.ModFolder = mods[selectedModIndex].ModFolder;
            ctx.ModConfigPath = mods[selectedModIndex].ConfigFolder;
            ctx.ModName = mods[selectedModIndex].Name;
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
                moduleAvailability[m.ModuleName] = true; // ModInfo etc altijd aanwezig
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


    void Ensure(string folder, string file, string root)
    {
        var path = Path.Combine(folder, file);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n{root}\n");
        }
    }
}
