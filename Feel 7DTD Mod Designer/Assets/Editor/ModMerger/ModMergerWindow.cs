// Assets/Editor/ModMergerWindow.cs
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Xml.Linq;
using System;

public class ModMergerWindow : EditorWindow
{
    // Paths and settings
    string rootPath = "";
    string outputPath = "";
    string mergedModName = "";
    string modInfoPath = "";
    string readmePath = "";
    string searchFilter = "";
    string ignoreExtField = ".meta";
    List<string> ignoreExtList = new List<string>();

    // Scroll position
    Vector2 scrollPos;

    // Tabs
    enum Tab { Files, Conflicts }
    Tab currentTab = Tab.Files;

    // Foldout & include state
    Dictionary<string, bool> modFoldouts = new Dictionary<string, bool>();
    Dictionary<string, bool> folderFoldouts = new Dictionary<string, bool>();
    Dictionary<string, bool> includeMap = new Dictionary<string, bool>();

    // Conflict structures
    class ConflictItem
    {
        public string RelPath;
        public string ElementKey;
        public List<(string modName, string filePath, XElement xml)> Variants;
        public int SelectedIndex;
    }
    List<ConflictItem> conflicts = new List<ConflictItem>();

    // Data structures for files
    class ModFile { public string FullPath, RelativePath; }
    class ModFolder
    {
        public string Name;
        public string FullPath;    // mod root
        public string XmlRoot;     // the "XML" folder path
        public Dictionary<string, List<ModFile>> FolderFiles = new Dictionary<string, List<ModFile>>();
    }
    List<ModFolder> modFolders = new List<ModFolder>();

    // Persistence helper
    [Serializable] class IncludeEntry { public string path; public bool include; }
    [Serializable] class IncludeData { public List<IncludeEntry> entries = new List<IncludeEntry>(); }
    [Serializable] class ConflictChoice { public string RelPath, ElementKey; public int SelectedIndex; }
    [Serializable] class ConflictChoiceWrapper { public List<ConflictChoice> items; }

    [MenuItem("Tools/Feel 7DTD/Mod Merger")]
    public static void ShowWindow() => GetWindow<ModMergerWindow>("Feel - Mod Merger");

    void OnEnable()
    {
        // load saved prefs
        rootPath = EditorPrefs.GetString("MM_rootPath", "");
        outputPath = EditorPrefs.GetString("MM_outputPath", "");
        mergedModName = EditorPrefs.GetString("MM_modName", "");
        modInfoPath = EditorPrefs.GetString("MM_modInfoPath", "");
        readmePath = EditorPrefs.GetString("MM_readmePath", "");
        ignoreExtField = EditorPrefs.GetString("MM_ignoreExtField", ".meta");
        ParseIgnoreExts();
        RefreshModList();
    }

    void OnDisable()
    {
        // save prefs
        EditorPrefs.SetString("MM_rootPath", rootPath);
        EditorPrefs.SetString("MM_outputPath", outputPath);
        EditorPrefs.SetString("MM_modName", mergedModName);
        EditorPrefs.SetString("MM_modInfoPath", modInfoPath);
        EditorPrefs.SetString("MM_readmePath", readmePath);
        EditorPrefs.SetString("MM_ignoreExtField", ignoreExtField);
        SaveIncludeSettings();
        SaveConflictChoices();
    }

    void OnGUI()
    {
        GUILayout.Label("Mod Merger Settings", EditorStyles.boldLabel);

        DrawPathField(ref rootPath, "Root Mods Folder", "Folder containing your mod subfolders");
        DrawPathField(ref outputPath, "Output Folder", "Folder for the merged mod output");
        mergedModName = EditorGUILayout.TextField("Merged Mod Name", mergedModName);
        DrawFileField(ref modInfoPath, "Custom ModInfo.xml", "Optional ModInfo.xml at root");
        DrawFileField(ref readmePath, "README.md", "Optional README.md at root");

        EditorGUILayout.Space();
        GUILayout.Label("Ignore Extensions (comma-separated)", EditorStyles.label);
        EditorGUILayout.BeginHorizontal();
        ignoreExtField = EditorGUILayout.TextField(ignoreExtField);
        if (GUILayout.Button("Parse", GUILayout.Width(50))) ParseIgnoreExts();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        searchFilter = EditorGUILayout.TextField("Search Filter", searchFilter);

        EditorGUILayout.Space();
        if (GUILayout.Button("Refresh Mod List"))
            RefreshModList();

        EditorGUILayout.Space();
        currentTab = (Tab)GUILayout.Toolbar((int)currentTab, new[] { "Files", "Conflicts" });

        if (currentTab == Tab.Files)
            DrawFilesTab();
        else
            DrawConflictsTab();

        if (currentTab == Tab.Files)
        {
            EditorGUILayout.Space();
            if (GUILayout.Button("Merge Selected"))
            {
                if (!ValidateInputs())
                {
                    EditorUtility.DisplayDialog("Error", "Please fill in all required fields.", "OK");
                    return;
                }
                var xmlGroups = CollectXmlGroups(out var locFiles);
                DetectConflicts(xmlGroups);
                LoadConflictChoices();

                if (conflicts.Count > 0)
                {
                    EditorUtility.DisplayDialog("Conflicts found",
                        $"Found {conflicts.Count} conflicts. Switch to the Conflicts tab to resolve.",
                        "OK");
                }
                else
                {
                    PerformMerge(xmlGroups, locFiles);
                }
            }
        }
    }

    #region GUI Helpers

    void DrawFilesTab()
    {
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));
        EditorGUILayout.LabelField("Mods & Folders", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("box");
        foreach (var mod in modFolders)
        {
            if (!modFoldouts.ContainsKey(mod.Name)) modFoldouts[mod.Name] = true;
            modFoldouts[mod.Name] = EditorGUILayout.Foldout(modFoldouts[mod.Name], mod.Name, true);
            if (!modFoldouts[mod.Name]) continue;

            EditorGUI.indentLevel++;
            foreach (var kv in mod.FolderFiles.OrderBy(k => k.Key))
            {
                string folderPath = kv.Key;
                string displayName = string.IsNullOrEmpty(folderPath) ? "<root>" : folderPath;
                string foldKey = mod.Name + "/" + folderPath;

                if (!folderFoldouts.ContainsKey(foldKey))
                    folderFoldouts[foldKey] = false;

                bool oldInc = includeMap.TryGetValue(mod.XmlRoot + "/" + folderPath, out bool tmp) ? tmp : true;
                EditorGUILayout.BeginHorizontal();
                bool newInc = EditorGUILayout.Toggle(oldInc, GUILayout.Width(16));
                if (newInc != oldInc)
                {
                    includeMap[mod.XmlRoot + "/" + folderPath] = newInc;
                    foreach (var f in kv.Value)
                        includeMap[f.FullPath] = newInc;
                }
                folderFoldouts[foldKey] = EditorGUILayout.Foldout(folderFoldouts[foldKey], displayName, true);
                EditorGUILayout.EndHorizontal();

                if (!folderFoldouts[foldKey]) continue;

                EditorGUI.indentLevel++;
                foreach (var file in kv.Value)
                {
                    if (!string.IsNullOrEmpty(searchFilter) &&
                        !file.RelativePath.ToLower().Contains(searchFilter.ToLower()))
                        continue;

                    bool finc = includeMap.TryGetValue(file.FullPath, out bool fval) ? fval : true;
                    finc = EditorGUILayout.ToggleLeft(Path.GetFileName(file.RelativePath), finc);
                    includeMap[file.FullPath] = finc;
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndScrollView();
    }

    void DrawConflictsTab()
    {
        EditorGUILayout.LabelField("Conflicts", EditorStyles.boldLabel);
        GUILayout.Space(4);

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
        foreach (var c in conflicts)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField($"{c.RelPath} → {c.ElementKey}", EditorStyles.boldLabel);

            for (int i = 0; i < c.Variants.Count; i++)
            {
                var (modName, filePath, xml) = c.Variants[i];
                string preview = xml.ToString(SaveOptions.DisableFormatting);
                preview = preview.Length > 100 ? preview.Substring(0, 100) + "…" : preview;
                bool sel = (c.SelectedIndex == i);
                if (EditorGUILayout.ToggleLeft($"{modName}: {preview}", sel))
                    c.SelectedIndex = i;
            }
            EditorGUILayout.EndVertical();
            GUILayout.Space(4);
        }
        EditorGUILayout.EndScrollView();

        if (conflicts.Count > 0 && GUILayout.Button("Apply Conflict Resolutions"))
            ApplyConflictResolutions();
    }

    void DrawPathField(ref string path, string label, string tooltip = "")
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(new GUIContent(label, tooltip), GUILayout.Width(140));
        path = EditorGUILayout.TextField(path);
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            string sel = EditorUtility.OpenFolderPanel("Select " + label, "", "");
            if (!string.IsNullOrEmpty(sel)) path = sel;
        }
        EditorGUILayout.EndHorizontal();
    }

    void DrawFileField(ref string path, string label, string tooltip = "")
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(new GUIContent(label, tooltip), GUILayout.Width(140));
        path = EditorGUILayout.TextField(path);
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            string ext = Path.GetExtension(path).TrimStart('.');
            string sel = EditorUtility.OpenFilePanel("Select " + label, "", ext);
            if (!string.IsNullOrEmpty(sel)) path = sel;
        }
        EditorGUILayout.EndHorizontal();
    }

    #endregion

    bool ValidateInputs() =>
        Directory.Exists(rootPath) &&
        !string.IsNullOrEmpty(outputPath) &&
        !string.IsNullOrEmpty(mergedModName);

    void ParseIgnoreExts()
    {
        ignoreExtList = ignoreExtField
            .Split(',')
            .Select(e => e.Trim().ToLower())
            .Where(e => e.StartsWith("."))
            .ToList();
    }

    void RefreshModList()
    {
        modFolders.Clear();
        includeMap.Clear();
        if (!Directory.Exists(rootPath)) return;

        // Eenvoudige implementatie; je kunt hier lazy‐loading toepassen
        foreach (var modDir in Directory.GetDirectories(rootPath))
        {
            var xmlRoot = Path.Combine(modDir, "XML");
            if (!Directory.Exists(xmlRoot)) continue;

            var mod = new ModFolder
            {
                Name = Path.GetFileName(modDir),
                FullPath = modDir,
                XmlRoot = xmlRoot
            };

            var allFiles = Directory
                .GetFiles(xmlRoot, "*.*", SearchOption.AllDirectories)
                .Where(f => !ignoreExtList.Contains(Path.GetExtension(f).ToLower()))
                .Select(f => new ModFile
                {
                    FullPath = f,
                    RelativePath = Path.GetRelativePath(xmlRoot, f).Replace("\\", "/")
                })
                .ToList();

            foreach (var grp in allFiles.GroupBy(f => Path.GetDirectoryName(f.RelativePath) ?? ""))
                mod.FolderFiles[grp.Key] = grp.ToList();

            if (mod.FolderFiles.Count > 0)
                modFolders.Add(mod);
        }

        LoadIncludeSettings();
    }

    Dictionary<string, List<string>> CollectXmlGroups(out List<(string fullPath, string relDir)> locFiles)
    {
        var xmlGroups = new Dictionary<string, List<string>>();
        locFiles = new List<(string, string)>();

        foreach (var mod in modFolders)
        {
            foreach (var kv in mod.FolderFiles)
            {
                if (includeMap.TryGetValue(mod.XmlRoot + "/" + kv.Key, out bool folderOn) && !folderOn)
                    continue;

                foreach (var mf in kv.Value)
                {
                    if (includeMap.TryGetValue(mf.FullPath, out bool inc) && !inc)
                        continue;

                    string relPath = mf.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString());
                    string ext = Path.GetExtension(mf.FullPath).ToLower();

                    if (Path.GetFileName(mf.FullPath)
                        .Equals("localization.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        locFiles.Add((mf.FullPath, Path.GetDirectoryName(relPath)));
                        continue;
                    }

                    if (ext == ".xml")
                    {
                        if (!xmlGroups.ContainsKey(relPath))
                            xmlGroups[relPath] = new List<string>();
                        xmlGroups[relPath].Add(mf.FullPath);
                    }
                }
            }
        }

        return xmlGroups;
    }
    void DetectConflicts(Dictionary<string, List<string>> xmlGroups)
    {
        conflicts.Clear();

        foreach (var kv in xmlGroups)
        {
            // Load all top-level elements for each mod’s file
            var perModElems = new Dictionary<string, XElement[]>();
            var perModPaths = new Dictionary<string, string>();
            foreach (var path in kv.Value)
            {
                string modName = modFolders.First(m => path.StartsWith(m.XmlRoot)).Name;
                perModPaths[modName] = path;
                perModElems[modName] = XDocument.Load(path)
                                                 .Root
                                                 .Elements()
                                                 .ToArray();
            }

            // Build a set of “keys” (tag + xpath + name) to scan for conflicts
            var allKeys = perModElems.Values
                .SelectMany(e => e)
                .Select(e => (
                    Tag: e.Name.LocalName,
                    XPath: (string)e.Attribute("xpath") ?? "",
                    Name: (string)e.Attribute("name") ?? ""
                ))
                .Distinct();

            foreach (var key in allKeys)
            {
                // skip any tags we want to simply merge en masse
                if (key.Tag == "append"
                 || key.Tag == "remove"
                 || key.Tag == "modelse"
                 || key.Tag == "modif")    // <-- no conflicts on <modif>
                    continue;

                // gather each mod’s element for this key
                var variants = new List<(string modName, string filePath, XElement xml)>();
                foreach (var modName in perModElems.Keys)
                {
                    var match = perModElems[modName]
                        .FirstOrDefault(e =>
                            e.Name.LocalName == key.Tag
                         && ((string)e.Attribute("xpath") ?? "") == key.XPath
                         && ((string)e.Attribute("name") ?? "") == key.Name);

                    if (match != null)
                        variants.Add((modName, perModPaths[modName], match));
                }

                // if there's 0 or 1 variants, no conflict
                if (variants.Count <= 1)
                    continue;

                // check if they all actually differ
                bool anyDifferent = false;
                for (int i = 0; i < variants.Count && !anyDifferent; i++)
                    for (int j = i + 1; j < variants.Count; j++)
                        if (!XNode.DeepEquals(variants[i].xml, variants[j].xml))
                        {
                            anyDifferent = true;
                            break;
                        }

                if (!anyDifferent)
                    continue;

                // we have a real conflict: add it to your list
                conflicts.Add(new ConflictItem
                {
                    RelPath = kv.Key,
                    ElementKey = $"{key.Tag} @xpath='{key.XPath}'{(key.Name != "" ? $" name='{key.Name}'" : "")}",
                    Variants = variants,
                    SelectedIndex = 0
                });
            }
        }
    }

    void ApplyConflictResolutions()
    {
        foreach (var c in conflicts)
        {
            var chosen = c.Variants[c.SelectedIndex].xml;
            foreach (var (modName, filePath, xml) in c.Variants)
            {
                if (filePath == c.Variants[c.SelectedIndex].filePath) continue;
                var doc = XDocument.Load(filePath);
                var toReplace = doc.Descendants()
                    .First(e => (e.Attribute("name")?.Value ?? e.Name.LocalName) == c.ElementKey);
                toReplace.ReplaceWith(new XElement(chosen));
                doc.Save(filePath);
            }
        }
        SaveConflictChoices();

        var xmlGroups = CollectXmlGroups(out var locFiles);
        PerformMerge(xmlGroups, locFiles);
    }

    void PerformMerge(Dictionary<string, List<string>> xmlGroups, List<(string fullPath, string relDir)> locFiles)
    {
        // 0) Basisfolders aanmaken
        string destRoot = Path.Combine(outputPath, mergedModName);
        string destConfig = Path.Combine(destRoot, "Config");
        Directory.CreateDirectory(destConfig);

        // 0a) Kopieer alleen *éénmaal* ModInfo + README naar de mod root
        if (File.Exists(modInfoPath))
            File.Copy(modInfoPath, Path.Combine(destRoot, "ModInfo.xml"), true);
        if (File.Exists(readmePath))
            File.Copy(readmePath, Path.Combine(destRoot, "README.md"), true);

        // 1) Merge alle XML-configs
        foreach (var kv in xmlGroups)
        {
            // bepaal targetfolder onder Config
            string relFolder = Path.GetDirectoryName(kv.Key) ?? "";
            if (relFolder.StartsWith("Config" + Path.DirectorySeparatorChar) || relFolder == "Config")
                relFolder = relFolder.Length > "Config".Length
                            ? relFolder.Substring("Config".Length + 1)
                            : "";
            string targetFolder = string.IsNullOrEmpty(relFolder)
                                 ? destConfig
                                 : Path.Combine(destConfig, relFolder);
            Directory.CreateDirectory(targetFolder);

            // bouw de merged XML
            var mergedRoot = new XElement("root");
            foreach (var src in kv.Value)
            {
                var doc = XDocument.Load(src);
                foreach (var elem in doc.Root.Elements())
                {
                    // a) alle <modelse>-children combineren
                    if (elem.Name == "modelse")
                    {
                        var existing = mergedRoot.Element("modelse");
                        if (existing == null)
                            mergedRoot.Add(new XElement(elem));
                        else
                            foreach (var child in elem.Elements())
                                existing.Add(new XElement(child));
                    }
                    // b) generieke append-handling
                    else if (elem.Name == "append" && elem.Attribute("xpath") != null)
                    {
                        var xpath = elem.Attribute("xpath").Value;
                        var existing = mergedRoot.Elements("append")
                                                 .FirstOrDefault(x => x.Attribute("xpath")?.Value == xpath);
                        if (existing != null)
                            foreach (var child in elem.Elements())
                                existing.Add(new XElement(child));
                        else
                            mergedRoot.Add(new XElement(elem));
                    }
                    // c) alle overige elementen 1-op-1 toevoegen
                    else
                    {
                        mergedRoot.Add(new XElement(elem));
                    }
                }
            }

            // opslaan
            var outDoc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), mergedRoot);
            outDoc.Save(Path.Combine(targetFolder, Path.GetFileName(kv.Key)));
        }

        // 2) Merge localization.txt per subfolder
        var locGroups = locFiles
            .GroupBy(x => x.relDir ?? "")
            .ToDictionary(g => g.Key, g => g.Select(x => x.fullPath).ToList());
        foreach (var kv in locGroups)
        {
            var lines = kv.Value
                .SelectMany(path => File.ReadAllLines(path))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .Distinct()
                .ToList();

            string outDir = string.IsNullOrEmpty(kv.Key)
                ? destConfig
                : Path.Combine(destConfig, kv.Key);
            Directory.CreateDirectory(outDir);
            File.WriteAllLines(Path.Combine(outDir, "localization.txt"), lines);
        }

        // 3) Copy non-XML assets *uit de XML-mappen* van elke mod
        foreach (var mod in modFolders)
        {
            // bepaal welke XML-submappen zijn uitgezet
            var disabledXmlFolders = new HashSet<string>(
                includeMap
                  .Where(kv => kv.Key.StartsWith(mod.XmlRoot) && !kv.Value)
                  .Select(kv => {
                      var rel = kv.Key.Substring(mod.XmlRoot.Length + 1);
                      return rel.Split(Path.DirectorySeparatorChar)[0];
                  })
                  .Distinct()
            );

            // enkel files áánwezig in de XML-root van de mod
            foreach (var srcPath in Directory.GetFiles(mod.XmlRoot, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(srcPath).ToLower();
                if (ignoreExtList.Contains(ext))
                    continue;

                // sla uitgezette XML-subfolder over
                var relUnderXml = Path.GetRelativePath(mod.XmlRoot, srcPath);
                var topFolder = relUnderXml.Split(Path.DirectorySeparatorChar)[0];
                if (disabledXmlFolders.Contains(topFolder))
                    continue;

                // copieer wél naar Config/… dezelfde relative structuur
                string relInConfig = Path.GetDirectoryName(relUnderXml) ?? "";
                string dstFolder = Path.Combine(destConfig, relInConfig);
                Directory.CreateDirectory(dstFolder);
                File.Copy(srcPath, Path.Combine(dstFolder, Path.GetFileName(srcPath)), true);
            }
        }

        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Done", $"Merged mod created at:\n{destRoot}", "OK");
    }

    void SaveIncludeSettings()
    {
        var data = new IncludeData();
        foreach (var kv in includeMap)
            data.entries.Add(new IncludeEntry { path = kv.Key, include = kv.Value });
        EditorPrefs.SetString("MM_includeMap", JsonUtility.ToJson(data));
    }

    void LoadIncludeSettings()
    {
        includeMap.Clear();
        var json = EditorPrefs.GetString("MM_includeMap", "");
        if (string.IsNullOrEmpty(json)) return;
        var data = JsonUtility.FromJson<IncludeData>(json);
        foreach (var e in data.entries)
            includeMap[e.path] = e.include;
    }

    void SaveConflictChoices()
    {
        var list = conflicts.Select(c => new ConflictChoice
        {
            RelPath = c.RelPath,
            ElementKey = c.ElementKey,
            SelectedIndex = c.SelectedIndex
        }).ToList();
        var wrapper = new ConflictChoiceWrapper { items = list };
        EditorPrefs.SetString("MM_conflicts", JsonUtility.ToJson(wrapper));
    }

    void LoadConflictChoices()
    {
        var json = EditorPrefs.GetString("MM_conflicts", "");
        if (string.IsNullOrEmpty(json)) return;
        var wrapper = JsonUtility.FromJson<ConflictChoiceWrapper>(json);
        foreach (var w in wrapper.items)
        {
            var c = conflicts.FirstOrDefault(x =>
                x.RelPath == w.RelPath &&
                x.ElementKey == w.ElementKey);
            if (c != null) c.SelectedIndex = w.SelectedIndex;
        }
    }
}
