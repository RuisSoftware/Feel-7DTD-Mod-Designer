// Assets/Editor/ModsRootConflictScanner.cs
using UnityEditor;
using UnityEngine;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

public class ModsRootConflictScanner : EditorWindow
{
    [MenuItem("Tools/Feel 7DTD/Mods Root Resolver")]
    static void Open() => GetWindow<ModsRootConflictScanner>("Mods Root Resolver");

    enum NamingMode { KeepOriginal, PrefabPlusType } // Prefab + " Material"/" Texture"

    [Serializable]
    class ModInfo
    {
        public string Name;
        public string ModPath;       // e.g. Assets/Mods/Root/feel-batterybanks
        public string PrefabsPath;   // .../Prefabs
        public string MaterialsPath; // .../Materials
        public string TexturesPath;  // .../Textures
        public string XmlPath;       // .../XML
        public string ModelsPath;    // .../Models
        public string MeshesPath;    // .../Meshes
        public bool HasPrefabs => AssetDatabase.IsValidFolder(PrefabsPath);
        public bool HasMaterials => AssetDatabase.IsValidFolder(MaterialsPath);
        public bool HasTextures => AssetDatabase.IsValidFolder(TexturesPath);
        public bool HasXml => AssetDatabase.IsValidFolder(XmlPath);
        public bool HasModels => AssetDatabase.IsValidFolder(ModelsPath);
        public bool HasMeshes => AssetDatabase.IsValidFolder(MeshesPath);
    }

    class MatConflict
    {
        public ModInfo Mod;
        public string PrefabPath;
        public string PrefabName;        // file name without extension
        public string RendererPath;
        public int MaterialSlot;
        public string SourceMatPath;     // current asset path
        public string DestMatPath;       // suggestion (recomputed in Execute based on chosen naming)
        public bool Selected = true;
        public string Reason;            // OutsideThisMod | WrongFolder
    }

    class TexConflict
    {
        public ModInfo Mod;
        public string MaterialPath;      // .mat in (or under) this mod
        public string PropertyName;      // _BaseMap/_MainTex/etc.
        public string SourceTexPath;     // current asset path
        public string DestTexPath;       // suggestion (recomputed in Execute based on chosen naming)
        public string PreferredPrefabName; // if known: prefab that uses this material (first one)
        public bool Selected = true;
        public string Reason;            // OutsideThisMod | WrongFolder
    }

    // ---- UI / State ----
    string _rootPath = "Assets/Mods";
    Vector2 _modsScroll, _reportScroll, _foldersScroll;
    List<ModInfo> _mods = new();
    ModInfo _selectedMod;
    bool _scanning;
    bool _cancelScan;

    // Results
    List<MatConflict> _matConf = new();
    List<TexConflict> _texConf = new();

    // Resizable panels
    float _modsListHeight = 190f;
    bool _resizingModsList;
    float _modsListStartH, _modsListStartY;

    float _foldersListHeight = 150f;
    bool _resizingFoldersList;
    float _foldersStartH, _foldersStartY;

    // Naming options
    NamingMode _materialNaming = NamingMode.KeepOriginal;
    NamingMode _textureNaming = NamingMode.KeepOriginal;

    // Known folders for your standard
    static readonly string[] kKnownFolders = new[]
    {
        "XML","Prefabs","Materials","Textures","Models","Meshes","Scenes","Animations","Particles","Scripts"
    };

    // reverse index: material -> set(prefabPaths) (filled during prefab scan)
    readonly Dictionary<string, HashSet<string>> _matToPrefabs = new(StringComparer.OrdinalIgnoreCase);

    void OnGUI()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Root mods folder", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        _rootPath = EditorGUILayout.TextField(_rootPath);
        if (GUILayout.Button("Pick…", GUILayout.Width(70)))
        {
            var pick = EditorUtility.OpenFolderPanel("Pick Mods Root (inside project)", Application.dataPath, "");
            if (!string.IsNullOrEmpty(pick) && pick.Replace('\\', '/').Contains("/Assets/"))
                _rootPath = "Assets" + pick.Replace('\\', '/').Split(new[] { "/Assets" }, StringSplitOptions.None)[1];
            else if (!string.IsNullOrEmpty(pick))
                EditorUtility.DisplayDialog("Invalid", "Choose a folder inside your Unity project (Assets/…)", "OK");
        }
        if (GUILayout.Button("Scan all mods", GUILayout.Width(120)))
        {
            BuildMods();
            _selectedMod = null;
            _matConf.Clear(); _texConf.Clear();
        }
        EditorGUILayout.EndHorizontal();

        if (!Directory.Exists(AbsPath(_rootPath)))
        {
            EditorGUILayout.HelpBox("Root folder does not exist yet.", MessageType.Warning);
            return;
        }

        // ---- MODS LIST (resizable) ----
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Mods in root", EditorStyles.boldLabel);

        _modsScroll = EditorGUILayout.BeginScrollView(_modsScroll, GUILayout.Height(_modsListHeight));
        if (_mods.Count == 0)
        {
            EditorGUILayout.HelpBox("Not scanned yet. Click 'Scan all mods'.", MessageType.Info);
        }
        else
        {
            foreach (var m in _mods)
            {
                EditorGUILayout.BeginHorizontal("box");
                if (GUILayout.Button(m.Name, (_selectedMod == m) ? "ButtonLeft" : "Button"))
                {
                    _selectedMod = m;
                    _matConf.Clear(); _texConf.Clear();
                    ScanMod(m);
                }
                GUILayout.FlexibleSpace();
                var ok = m.HasPrefabs;
                GUILayout.Label(ok ? "Prefabs ✓" : "Prefabs ✗",
                    ok ? EditorStyles.miniBoldLabel : EditorStyles.miniLabel, GUILayout.Width(90));

                // Models status (if you have that previous patch)
                ok = m.HasModels;
                GUILayout.Label(ok ? "Models ✓" : "Models ✗",
                    ok ? EditorStyles.miniBoldLabel : EditorStyles.miniLabel, GUILayout.Width(90));

                // Meshes status
                ok = m.HasMeshes;
                GUILayout.Label(ok ? "Meshes ✓" : "Meshes ✗",
                    ok ? EditorStyles.miniBoldLabel : EditorStyles.miniLabel, GUILayout.Width(90));

                if (GUILayout.Button("Open", GUILayout.Width(60)))
                    EditorUtility.RevealInFinder(AbsPath(m.ModPath));
                EditorGUILayout.EndHorizontal();
            }
        }
        EditorGUILayout.EndScrollView();

        DrawResizeSplitter(ref _resizingModsList, ref _modsListStartH, ref _modsListStartY, ref _modsListHeight, 90f, 450f);

        if (_selectedMod == null)
        {
            EditorGUILayout.HelpBox("Click a mod to scan.", MessageType.Info);
            return;
        }

        // ---- MOD HEADER + FOLDER OVERVIEW (scrollable, resizable) ----
        EditorGUILayout.Space(6);
        DrawModHeader(_selectedMod);

        // Folders overview (color codes + create buttons)
        DrawFolderOverview(_selectedMod);

        // ---- ACTIONS: SCAN / STOP ----
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(_scanning))
        {
            if (GUILayout.Button("Scan this mod", GUILayout.Width(140)))
                ScanMod(_selectedMod);
        }
        if (_scanning)
        {
            GUI.color = Color.red;
            if (GUILayout.Button("Stop", GUILayout.Width(80)))
                _cancelScan = true;
            GUI.color = Color.white;
        }
        EditorGUILayout.EndHorizontal();

        // ---- NAMING ----
        EditorGUILayout.Space(6);
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Naming when copying", EditorStyles.boldLabel);
        _materialNaming = (NamingMode)EditorGUILayout.EnumPopup(new GUIContent("Materials"),
            _materialNaming);
        _textureNaming = (NamingMode)EditorGUILayout.EnumPopup(new GUIContent("Textures"),
            _textureNaming);
        EditorGUILayout.HelpBox(
            "• Keep Original: keep the original filename.\n" +
            "• PrefabPlusType: renames to \"<PrefabName> Material.mat\" or \"<PrefabName> Texture.<ext>\".",
            MessageType.None);
        EditorGUILayout.EndVertical();

        // ---- REPORT + EXECUTE ----
        DrawReportAndActions();
    }

    void DrawModHeader(ModInfo m)
    {
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField($"[{m.Name}] {m.ModPath}", EditorStyles.boldLabel);

        if (!m.HasPrefabs)
        {
            EditorGUILayout.HelpBox("This mod does not have a Prefabs folder. For 7DTD, ALL custom model prefabs must live here.", MessageType.Error);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Create Prefabs folder"))
            {
                EnsureFolder(m.PrefabsPath);
                AssetDatabase.Refresh();
            }
            if (GUILayout.Button("Open mod folder"))
                EditorUtility.RevealInFinder(AbsPath(m.ModPath));
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndVertical();
    }

    void DrawFolderOverview(ModInfo m)
    {
        // collect subfolders
        var present = GetSubFolders(m.ModPath).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _foldersScroll = EditorGUILayout.BeginScrollView(_foldersScroll, GUILayout.Height(_foldersListHeight));
        // Known folders
        foreach (var k in kKnownFolders)
        {
            bool exists = present.Contains(k);
            DrawFolderRow(m, k, exists,
                exists ? new Color(0.75f, 0.75f, 0.75f) : new Color(1f, 0.4f, 0.4f),
                exists ? "Exists" : "Missing",
                allowCreate: !exists);
            present.Remove(k);
        }

        // Unknown (others) → yellow
        foreach (var unk in present.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            DrawFolderRow(m, unk, true, new Color(1f, 0.9f, 0.3f), "Unknown", allowCreate: false);
        }
        EditorGUILayout.EndScrollView();

        DrawResizeSplitter(ref _resizingFoldersList, ref _foldersStartH, ref _foldersStartY, ref _foldersListHeight, 90f, 450f);
    }

    void DrawFolderRow(ModInfo m, string folderName, bool exists, Color color, string tag, bool allowCreate)
    {
        Rect r = EditorGUILayout.BeginHorizontal("box");
        // color dot
        var dot = new Rect(r.x + 6, r.y + 6, 12, 12);
        EditorGUI.DrawRect(dot, color);

        GUILayout.Space(22);
        GUILayout.Label(folderName, EditorStyles.boldLabel, GUILayout.Width(140));
        GUILayout.Label(tag, EditorStyles.miniLabel, GUILayout.Width(80));

        string assetPath = m.ModPath.TrimEnd('/') + "/" + folderName;
        GUILayout.Label(assetPath, EditorStyles.miniLabel);
        GUILayout.FlexibleSpace();

        if (exists)
        {
            if (GUILayout.Button("Open", GUILayout.Width(60)))
                EditorUtility.RevealInFinder(AbsPath(assetPath));
        }
        else if (allowCreate)
        {
            if (GUILayout.Button("Create", GUILayout.Width(70)))
            {
                EnsureFolder(assetPath);
                AssetDatabase.Refresh();
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    // --- Report + execute ---
    void DrawReportAndActions()
    {
        EditorGUILayout.Space(6);
        _reportScroll = EditorGUILayout.BeginScrollView(_reportScroll);

        if (_matConf.Count == 0 && _texConf.Count == 0)
        {
            EditorGUILayout.HelpBox("No conflicts (or not scanned yet).", MessageType.Info);
        }
        else
        {
            if (_matConf.Count > 0)
            {
                EditorGUILayout.LabelField($"Material conflicts ({_matConf.Count})", EditorStyles.boldLabel);
                foreach (var c in _matConf)
                {
                    EditorGUILayout.BeginVertical("box");
                    c.Selected = EditorGUILayout.ToggleLeft($"[{c.Mod.Name}] Prefab: {c.PrefabName}  ({c.PrefabPath})", c.Selected);
                    EditorGUILayout.LabelField("Renderer", c.RendererPath, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("Slot", c.MaterialSlot.ToString(), EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("Reason", c.Reason, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("Material", c.SourceMatPath, EditorStyles.miniLabel);

                    // Show current destination based on current naming option (live recomputed)
                    string previewDest = MakeMaterialDestPath(c);
                    EditorGUILayout.LabelField("→ Copy to", previewDest, EditorStyles.miniLabel);

                    EditorGUILayout.EndVertical();
                }
            }

            if (_texConf.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField($"Texture conflicts ({_texConf.Count})", EditorStyles.boldLabel);
                foreach (var c in _texConf)
                {
                    EditorGUILayout.BeginVertical("box");
                    string baseName = string.IsNullOrEmpty(c.PreferredPrefabName)
                        ? Path.GetFileNameWithoutExtension(c.MaterialPath)
                        : c.PreferredPrefabName;

                    c.Selected = EditorGUILayout.ToggleLeft($"[{c.Mod.Name}] Material: {Path.GetFileName(c.MaterialPath)}  (prefab: {baseName})", c.Selected);
                    EditorGUILayout.LabelField("Property", c.PropertyName, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("Reason", c.Reason, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("Texture", c.SourceTexPath, EditorStyles.miniLabel);

                    // Live recomputed destination based on current naming
                    string previewDest = MakeTextureDestPath(c, baseName);
                    EditorGUILayout.LabelField("→ Copy to", previewDest, EditorStyles.miniLabel);

                    EditorGUILayout.EndVertical();
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select none")) { SetSelect(false); }
            if (GUILayout.Button("Select all")) { SetSelect(true); }
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(!_matConf.Any(x => x.Selected) && !_texConf.Any(x => x.Selected)))
            {
                if (GUILayout.Button("Execute fixes", GUILayout.Height(26)))
                    ExecuteFixes();
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    // ----------------- Discovery -----------------
    void BuildMods()
    {
        _mods.Clear();
        if (!Directory.Exists(AbsPath(_rootPath))) return;

        foreach (var dir in Directory.GetDirectories(AbsPath(_rootPath)))
        {
            var name = Path.GetFileName(dir).Trim();
            if (string.IsNullOrEmpty(name)) continue;

            string assetDir = ToAssetPath(dir);
            var m = new ModInfo
            {
                Name = name,
                ModPath = assetDir,
                PrefabsPath = assetDir + "/Prefabs",
                MaterialsPath = assetDir + "/Materials",
                TexturesPath = assetDir + "/Textures",
                XmlPath = assetDir + "/XML",
                ModelsPath = assetDir + "/Models",
                MeshesPath = assetDir + "/Meshes",
            };
            _mods.Add(m);
        }
        Repaint();
    }

    // ----------------- Scan per mod -----------------
    void ScanMod(ModInfo mod)
    {
        _matConf.Clear();
        _texConf.Clear();
        _matToPrefabs.Clear();

        if (!mod.HasPrefabs) { Repaint(); return; }

        _scanning = true;
        _cancelScan = false;

        try
        {
            // 1) Walk prefabs -> material conflicts + reverse index material->prefabs
            var prefabs = FindAssetsUnder(mod.PrefabsPath, "*.prefab");
            int total = prefabs.Length;
            for (int i = 0; i < total; i++)
            {
                if (_cancelScan) break;

                string pp = prefabs[i];
                float prog = total > 0 ? (i + 1f) / total : 1f;
                if (EditorUtility.DisplayCancelableProgressBar($"Scanning {mod.Name}",
                        $"{Path.GetFileName(pp)}  ({i + 1}/{total})", prog))
                {
                    _cancelScan = true;
                    break;
                }

                string prefabName = Path.GetFileNameWithoutExtension(pp);
                var root = PrefabUtility.LoadPrefabContents(pp);
                try
                {
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        var mats = r.sharedMaterials;
                        if (mats == null || mats.Length == 0) continue;

                        for (int slot = 0; slot < mats.Length; slot++)
                        {
                            var mat = mats[slot];
                            if (mat == null) continue;

                            string matPath = AssetDatabase.GetAssetPath(mat);
                            if (string.IsNullOrEmpty(matPath)) continue;

                            // reverse index
                            if (!_matToPrefabs.TryGetValue(matPath, out var set))
                                _matToPrefabs[matPath] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            set.Add(pp);

                            // Rule: material must be in this mod's .../Materials folder
                            bool underThisMod = IsUnder(matPath, mod.ModPath);
                            bool underMaterials = IsUnder(matPath, mod.MaterialsPath);

                            if (!underThisMod || !underMaterials)
                            {
                                EnsureFolder(mod.MaterialsPath);
                                _matConf.Add(new MatConflict
                                {
                                    Mod = mod,
                                    PrefabPath = pp,
                                    PrefabName = prefabName,
                                    RendererPath = HierarchyPath(r.transform),
                                    MaterialSlot = slot,
                                    SourceMatPath = matPath,
                                    Reason = !underThisMod ? "OutsideThisMod" : "WrongFolder",
                                    DestMatPath = MakeMaterialDestPath(new MatConflict
                                    {
                                        Mod = mod,
                                        PrefabName = prefabName,
                                        SourceMatPath = matPath
                                    }) // preliminary preview
                                });
                            }
                        }
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            EditorUtility.ClearProgressBar();

            // 2) All materials (under this mod) -> texture conflicts
            var modMatAssets = FindAssetsUnder(mod.ModPath, "*.mat");
            for (int i = 0; i < modMatAssets.Length; i++)
            {
                if (_cancelScan) break;

                string mp = modMatAssets[i];
                float prog = modMatAssets.Length > 0 ? (i + 1f) / modMatAssets.Length : 1f;
                if (EditorUtility.DisplayCancelableProgressBar($"Scanning Textures in {mod.Name}",
                        $"{Path.GetFileName(mp)}  ({i + 1}/{modMatAssets.Length})", prog))
                {
                    _cancelScan = true; break;
                }

                var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
                if (!mat) continue;

                // determine one preferred prefab name that uses this material
                string preferredPrefab = null;
                if (_matToPrefabs.TryGetValue(mp, out var users) && users.Count > 0)
                {
                    var first = users.First();
                    preferredPrefab = Path.GetFileNameWithoutExtension(first);
                }

                foreach (var prop in mat.GetTexturePropertyNames())
                {
                    var tex = mat.GetTexture(prop);
                    if (!tex) continue;

                    string txPath = AssetDatabase.GetAssetPath(tex);
                    if (string.IsNullOrEmpty(txPath)) continue;

                    bool underThisMod = IsUnder(txPath, mod.ModPath);
                    bool underTextures = IsUnder(txPath, mod.TexturesPath);

                    if (!underThisMod || !underTextures)
                    {
                        EnsureFolder(mod.TexturesPath);
                        _texConf.Add(new TexConflict
                        {
                            Mod = mod,
                            MaterialPath = mp,
                            PropertyName = prop,
                            SourceTexPath = txPath,
                            Reason = !underThisMod ? "OutsideThisMod" : "WrongFolder",
                            PreferredPrefabName = preferredPrefab,
                            DestTexPath = MakeTextureDestPath(new TexConflict
                            {
                                Mod = mod,
                                MaterialPath = mp,
                                SourceTexPath = txPath,
                                PreferredPrefabName = preferredPrefab
                            }, preferredPrefab) // preliminary preview
                        });
                    }
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            _scanning = false;
            Repaint();
        }
    }

    // ----------------- Execute fixes -----------------
    void ExecuteFixes()
    {
        // ========== MATERIALS ==========
        // Group per (Mod, SourceMatPath) → 1 copy per source
        // --- MATERIALS: first ONLY copying in batch ---
        var matGroups = _matConf.Where(x => x.Selected)
            .GroupBy(x => (x.Mod, x.SourceMatPath), x => x, new MatKeyComparer())
            .ToList();

        var matCopyResult = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var grp in matGroups)
            {
                var mod = grp.Key.Mod;
                string sourceMat = grp.Key.SourceMatPath;
                string destMat = ComputeSharedMatDest(mod, sourceMat, grp);
                EnsureFolder(Path.GetDirectoryName(destMat));

                string finalMatPath = CopyAssetSmart(sourceMat, destMat, mod.Name);
                if (string.IsNullOrEmpty(finalMatPath)) continue;

                matCopyResult[mod.ModPath + "|" + sourceMat] = finalMatPath;
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // --- ONLY here retarget (OUTSIDE StartAssetEditing) ---
        foreach (var grp in matGroups)
        {
            var mod = grp.Key.Mod;
            string sourceMat = grp.Key.SourceMatPath;
            if (!matCopyResult.TryGetValue(mod.ModPath + "|" + sourceMat, out var finalMatPath))
                continue;

            foreach (var c in grp)
            {
                bool ok = RetargetMaterialOnPrefab(
                    c.PrefabPath,
                    c.RendererPath,
                    c.MaterialSlot,
                    c.SourceMatPath,
                    finalMatPath
                );
                if (!ok)
                    Debug.LogWarning($"[ModsRootScanner] Retarget FAIL: {System.IO.Path.GetFileName(c.PrefabPath)} -> {finalMatPath}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // After materials, rescan so texture conflicts are up-to-date
        if (_selectedMod != null) ScanMod(_selectedMod);

        // ========== TEXTURES ==========
        // Group per (Mod, SourceTexPath) → 1 copy per source
        var texGroups = _texConf
            .Where(x => x.Selected)
            .GroupBy(x => (x.Mod, x.SourceTexPath), x => x, new TexKeyComparer())
            .ToList();

        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var grp in texGroups)
            {
                var mod = grp.Key.Mod;
                string sourceTex = grp.Key.SourceTexPath;

                string destTex = ComputeSharedTexDest(mod, sourceTex, grp);
                EnsureFolder(Path.GetDirectoryName(destTex));

                string finalTexPath = CopyAssetSmart(sourceTex, destTex, mod.Name);
                if (string.IsNullOrEmpty(finalTexPath))
                {
                    Debug.LogWarning($"[ModsRootScanner] Texture copy failed: {sourceTex} -> {destTex}");
                    continue;
                }

                // Point all materials in this group to this ONE texture
                var newTex = AssetDatabase.LoadAssetAtPath<Texture>(finalTexPath);
                if (!newTex) continue;

                foreach (var c in grp)
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(c.MaterialPath);
                    if (!mat) continue;
                    Undo.RegisterCompleteObjectUndo(mat, "Retarget texture");
                    mat.SetTexture(c.PropertyName, newTex);
                    EditorUtility.SetDirty(mat);
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        if (_selectedMod != null) ScanMod(_selectedMod);
        EditorUtility.DisplayDialog("Done", "Dedup and fixes applied.", "OK");
    }

    // Grouping key comparers
    class MatKeyComparer : IEqualityComparer<(ModInfo Mod, string SourceMatPath)>
    {
        public bool Equals((ModInfo Mod, string SourceMatPath) x, (ModInfo Mod, string SourceMatPath) y)
            => ReferenceEquals(x.Mod, y.Mod) && string.Equals(x.SourceMatPath, y.SourceMatPath, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((ModInfo Mod, string SourceMatPath) obj)
            => (obj.Mod?.GetHashCode() ?? 0) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceMatPath ?? "");
    }

    class TexKeyComparer : IEqualityComparer<(ModInfo Mod, string SourceTexPath)>
    {
        public bool Equals((ModInfo Mod, string SourceTexPath) x, (ModInfo Mod, string SourceTexPath) y)
            => ReferenceEquals(x.Mod, y.Mod) && string.Equals(x.SourceTexPath, y.SourceTexPath, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((ModInfo Mod, string SourceTexPath) obj)
            => (obj.Mod?.GetHashCode() ?? 0) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceTexPath ?? "");
    }


    // ----------------- Helpers: naming -----------------

    // ---------- Helpers: GUID & compare ----------
    static string GetGuid(string assetPath)
    {
        return string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath);
    }

    static bool MatGuidEquals(Material m, string guid)
    {
        if (m == null || string.IsNullOrEmpty(guid)) return false;
        string p = AssetDatabase.GetAssetPath(m);
        if (string.IsNullOrEmpty(p)) return false;
        return string.Equals(AssetDatabase.AssetPathToGUID(p), guid, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Helpers: resolve Renderer ----------
    Transform ResolveTransformByPath(GameObject root, string path)
    {
        if (root == null || string.IsNullOrEmpty(path)) return null;

        // path is a relative Transform path "Root/Child/Sub"
        var t = root.transform;
        var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0] == root.name)
        {
            // accept paths that start with the root name
            parts = parts.Skip(1).ToArray();
        }
        foreach (var p in parts)
        {
            t = t.Find(p);
            if (!t) return null;
        }
        return t;
    }

    Renderer ResolveRenderer(GameObject root, string rendererPath, string oldMatGuid, int slotIndex)
    {
        // 1) Preferred: via the saved path
        var t = ResolveTransformByPath(root, rendererPath);
        var r = t ? t.GetComponent<Renderer>() : null;
        if (r)
        {
            var mats = r.sharedMaterials ?? Array.Empty<Material>();
            if (slotIndex >= 0 && slotIndex < mats.Length && MatGuidEquals(mats[slotIndex], oldMatGuid))
                return r;
            // If slot doesn't match, don't give up—fallbacks below
        }

        // 2) Fallback: find any Renderer that in the given slot has the old material
        foreach (var rr in root.GetComponentsInChildren<Renderer>(true))
        {
            var mats = rr.sharedMaterials ?? Array.Empty<Material>();
            if (slotIndex >= 0 && slotIndex < mats.Length && MatGuidEquals(mats[slotIndex], oldMatGuid))
                return rr;
        }

        // 3) Last resort: find a renderer where the old material exists in any slot
        foreach (var rr in root.GetComponentsInChildren<Renderer>(true))
        {
            var mats = rr.sharedMaterials ?? Array.Empty<Material>();
            for (int i = 0; i < mats.Length; i++)
            {
                if (MatGuidEquals(mats[i], oldMatGuid)) return rr;
            }
        }
        return null;
    }

    // ---------- Core: retarget material in prefab ----------
    bool RetargetMaterialOnPrefab(
        string prefabPath,
        string rendererPath,
        int slotIndex,
        string oldMatAssetPath,
        string newMatAssetPath)
    {
        var newMat = AssetDatabase.LoadAssetAtPath<Material>(newMatAssetPath);
        if (!newMat)
        {
            Debug.LogWarning($"[ModsRootScanner] New material load failed: {newMatAssetPath}");
            return false;
        }

        // Use the recommended scope; this loads and closes cleanly
        using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
        {
            var root = scope.prefabContentsRoot;
            if (!root) return false;

            // 1) Find renderer by path, else fallback by old material path
            var r = ResolveRendererByPathOrOldMat(root, rendererPath, oldMatAssetPath);
            if (!r)
            {
                Debug.LogWarning($"[ModsRootScanner] Renderer not found for '{prefabPath}' → '{rendererPath}'.");
                return false;
            }

            // 2) Change materials via SerializedObject (more robust for nested instances)
            var so = new SerializedObject(r);
            var matsProp = so.FindProperty("m_Materials");
            bool changed = false;

            // Replace ALL slots that use the old path
            for (int i = 0; i < matsProp.arraySize; i++)
            {
                var elem = matsProp.GetArrayElementAtIndex(i);
                var curMat = elem.objectReferenceValue as Material;
                if (!curMat) continue;

                var curPath = AssetDatabase.GetAssetPath(curMat);
                if (!string.IsNullOrEmpty(curPath) &&
                    string.Equals(curPath, oldMatAssetPath, StringComparison.OrdinalIgnoreCase))
                {
                    elem.objectReferenceValue = newMat;
                    changed = true;
                }
            }

            // Fallback: replace specific slotIndex if nothing matched
            if (!changed && slotIndex >= 0 && slotIndex < matsProp.arraySize)
            {
                matsProp.GetArrayElementAtIndex(slotIndex).objectReferenceValue = newMat;
                changed = true;
            }

            if (!changed) return false;

            so.ApplyModifiedPropertiesWithoutUndo();

            // Ensure overrides on nested prefab instances are recorded
            if (PrefabUtility.IsPartOfPrefabInstance(r))
                PrefabUtility.RecordPrefabInstancePropertyModifications(r);

            // Mark both renderer and root dirty
            EditorUtility.SetDirty(r);
            EditorUtility.SetDirty(root);

            // Important: explicitly save outside StartAssetEditing
            var ok = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
            return ok && success;
        }
    }

    // Fallback search method: first by path, then by old material path
    Renderer ResolveRendererByPathOrOldMat(GameObject root, string rendererPath, string oldMatAssetPath)
    {
        var t = ResolveTransformByPath(root, rendererPath);
        var r = t ? t.GetComponent<Renderer>() : null;
        if (r) return r;

        foreach (var rr in root.GetComponentsInChildren<Renderer>(true))
        {
            var mats = rr.sharedMaterials ?? Array.Empty<Material>();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (!m) continue;
                var mp = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(mp) &&
                    string.Equals(mp, oldMatAssetPath, StringComparison.OrdinalIgnoreCase))
                    return rr;
            }
        }
        return null;
    }


    string MakeMaterialDestPath(MatConflict c)
    {
        string name =
            (_materialNaming == NamingMode.KeepOriginal)
            ? Path.GetFileName(c.SourceMatPath)
            : (San(c.PrefabName) + " Material.mat");

        return c.Mod.MaterialsPath.TrimEnd('/') + "/" + name;
    }

    string MakeTextureDestPath(TexConflict c, string baseName)
    {
        string ext = Path.GetExtension(c.SourceTexPath);
        string name =
            (_textureNaming == NamingMode.KeepOriginal)
            ? Path.GetFileName(c.SourceTexPath)
            : (San(baseName) + " Texture" + ext);

        return c.Mod.TexturesPath.TrimEnd('/') + "/" + name;
    }

    // ----------------- Misc helpers -----------------
    static string AbsPath(string assetOrAbs)
    {
        if (assetOrAbs.StartsWith("Assets/"))
            return Path.Combine(Path.GetFullPath(Application.dataPath + "/.."), assetOrAbs).Replace('\\', '/');
        return assetOrAbs.Replace('\\', '/');
    }

    static string ToAssetPath(string abs)
    {
        string p = abs.Replace('\\', '/');
        var idx = p.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return p.Substring(idx + 1); // start at Assets/...
        if (p.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase)) return "Assets";
        return p;
    }

    static string[] FindAssetsUnder(string rootAssetDir, string pattern)
    {
        var abs = AbsStatic(rootAssetDir);
        if (!Directory.Exists(abs)) return Array.Empty<string>();
        return Directory.GetFiles(abs, pattern, SearchOption.AllDirectories)
                        .Select(ToAssetStatic)
                        .Where(a => a.StartsWith("Assets/"))
                        .ToArray();
    }
    static string AbsStatic(string assetOrAbs)
        => assetOrAbs.StartsWith("Assets/") ? Path.Combine(Path.GetFullPath(Application.dataPath + "/.."), assetOrAbs).Replace('\\', '/') : assetOrAbs.Replace('\\', '/');
    static string ToAssetStatic(string abs) => ("Assets" + abs.Replace('\\', '/').Split(new[] { "/Assets" }, StringSplitOptions.None)[1]);

    static bool IsUnder(string assetPath, string rootAssetDir)
    {
        assetPath = assetPath.Replace('\\', '/'); rootAssetDir = rootAssetDir.Replace('\\', '/');
        if (!rootAssetDir.EndsWith("/")) rootAssetDir += "/";
        return assetPath.StartsWith(rootAssetDir, StringComparison.OrdinalIgnoreCase);
    }

    static void EnsureFolder(string assetDir)
    {
        if (string.IsNullOrEmpty(assetDir)) return;
        var parts = assetDir.Replace('\\', '/').Split('/');
        string acc = parts[0];
        if (!AssetDatabase.IsValidFolder(acc)) Directory.CreateDirectory(acc);
        for (int i = 1; i < parts.Length; i++)
        {
            var next = acc + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(acc, parts[i]);
            acc = next;
        }
    }

    static string UniquePath(string sourceAsset, string desiredAsset, string modTag)
    {
        desiredAsset = desiredAsset.Replace('\\', '/');
        EnsureFolder(Path.GetDirectoryName(desiredAsset));
        string ext = Path.GetExtension(desiredAsset);
        string name = Path.GetFileNameWithoutExtension(desiredAsset);
        string dir = Path.GetDirectoryName(desiredAsset);
        string candidate = desiredAsset;

        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate) != null)
        {
            // same content? reuse
            if (SameContent(sourceAsset, candidate)) return candidate;
            int n = 1;
            do
            {
                candidate = $"{dir}/{name}__{San(modTag)}_{n}{ext}";
                n++;
            } while (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate) != null);
        }
        return candidate;
    }

    static string CopyAssetSmart(string src, string dst, string modTag)
    {
        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(dst) != null && SameContent(src, dst))
            return dst;

        dst = UniquePath(src, dst, modTag);

        if (src.StartsWith("Assets/") && dst.StartsWith("Assets/"))
            return AssetDatabase.CopyAsset(src, dst) ? dst : null;

        try
        {
            string proj = Path.GetFullPath(Application.dataPath + "/..").Replace('\\', '/');
            string srcAbs = src.StartsWith("Assets/") ? Path.Combine(proj, src).Replace('\\', '/') : src;
            string dstAbs = Path.Combine(proj, dst).Replace('\\', '/');
            Directory.CreateDirectory(Path.GetDirectoryName(dstAbs));
            File.Copy(srcAbs, dstAbs, true);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            return dst;
        }
        catch (Exception ex)
        {
            Debug.LogError($"CopyAssetSmart failed: {ex.Message}");
            return null;
        }
    }

    static bool SameContent(string aAsset, string bAsset)
    {
        try
        {
            string proj = Path.GetFullPath(Application.dataPath + "/..").Replace('\\', '/');
            string a = aAsset.StartsWith("Assets/") ? Path.Combine(proj, aAsset).Replace('\\', '/') : aAsset;
            string b = bAsset.StartsWith("Assets/") ? Path.Combine(proj, bAsset).Replace('\\', '/') : bAsset;
            if (!File.Exists(a) || !File.Exists(b)) return false;
            var sha = System.Security.Cryptography.SHA1.Create();
            var A = sha.ComputeHash(File.ReadAllBytes(a));
            var B = sha.ComputeHash(File.ReadAllBytes(b));
            if (A.Length != B.Length) return false;
            for (int i = 0; i < A.Length; i++) if (A[i] != B[i]) return false;
            return true;
        }
        catch { return false; }
    }

    static string San(string s) =>
        string.IsNullOrEmpty(s) ? "Mod" : System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Za-z0-9_]+", "");

    static string HierarchyPath(Transform t)
    {
        var stack = new List<string>();
        while (t != null) { stack.Add(t.name); t = t.parent; }
        stack.Reverse();
        return string.Join("/", stack);
    }

    static Transform FindByPath(Transform root, string path)
    {
        if (!root || string.IsNullOrEmpty(path)) return null;
        var parts = path.Split('/');
        var cur = root;
        int idx = 0;
        if (parts.Length > 0 && parts[0] == root.name) idx = 1;
        for (int i = idx; i < parts.Length; i++)
        {
            cur = cur.Find(parts[i]);
            if (!cur) return null;
        }
        return cur;
    }

    static IEnumerable<string> GetSubFolders(string assetDir)
    {
        var abs = AbsStatic(assetDir);
        if (!Directory.Exists(abs)) yield break;
        foreach (var d in Directory.GetDirectories(abs)) yield return ToAssetStatic(d);
    }

    void SetSelect(bool on)
    {
        foreach (var c in _matConf) c.Selected = on;
        foreach (var c in _texConf) c.Selected = on;
    }

    void DrawResizeSplitter(ref bool resizing, ref float startH, ref float startY, ref float height, float min, float max)
    {
        Rect splitter = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(5), GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(new Rect(splitter.x, splitter.y + 2, splitter.width, 1), new Color(1f, 0.5f, 0f, 0.6f));
        EditorGUIUtility.AddCursorRect(splitter, MouseCursor.ResizeVertical);

        if (Event.current.type == EventType.MouseDown && splitter.Contains(Event.current.mousePosition))
        {
            resizing = true;
            startH = height;
            startY = GUIUtility.GUIToScreenPoint(Event.current.mousePosition).y;
            Event.current.Use();
        }
        if (resizing && Event.current.type == EventType.MouseDrag)
        {
            float y = GUIUtility.GUIToScreenPoint(Event.current.mousePosition).y;
            height = Mathf.Clamp(startH + (y - startY), min, max);
            Repaint();
            Event.current.Use();
        }
        if (resizing && Event.current.type == EventType.MouseUp)
        {
            resizing = false;
            Event.current.Use();
        }
    }

    // Determines a shared filename per source material within one mod.
    string ComputeSharedMatDest(ModInfo mod, string sourceMatPath, IEnumerable<MatConflict> group)
    {
        // choose consistent name based on current naming mode
        if (_materialNaming == NamingMode.KeepOriginal)
        {
            string file = Path.GetFileName(sourceMatPath);
            return mod.MaterialsPath.TrimEnd('/') + "/" + file;
        }
        else
        {
            // PrefabPlusType: deterministically choose 1 "owner" (alphabetically smallest prefab name)
            string owner = group.Select(g => g.PrefabName)
                                .Where(n => !string.IsNullOrEmpty(n))
                                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault() ?? "Shared";
            string name = San(owner) + " Material.mat";
            return mod.MaterialsPath.TrimEnd('/') + "/" + name;
        }
    }

    // Determines a shared filename per source texture within one mod.
    string ComputeSharedTexDest(ModInfo mod, string sourceTexPath, IEnumerable<TexConflict> group)
    {
        string ext = Path.GetExtension(sourceTexPath);
        if (_textureNaming == NamingMode.KeepOriginal)
        {
            string file = Path.GetFileName(sourceTexPath);
            return mod.TexturesPath.TrimEnd('/') + "/" + file;
        }
        else
        {
            // PrefabPlusType: deterministically pick one base name (prefer prefab, else material name)
            string owner = group.Select(g => g.PreferredPrefabName)
                                .Where(n => !string.IsNullOrEmpty(n))
                                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault();
            if (string.IsNullOrEmpty(owner))
            {
                owner = group.Select(g => Path.GetFileNameWithoutExtension(g.MaterialPath))
                             .Where(n => !string.IsNullOrEmpty(n))
                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault() ?? "Shared";
            }
            string name = San(owner) + " Texture" + ext;
            return mod.TexturesPath.TrimEnd('/') + "/" + name;
        }
    }

}
