using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class ScreenshotPrefabsWindow : EditorWindow
{
    // UI text fields (both "Assets/…" and absolute paths are accepted)
    private string prefabFolder = "Assets/Mods/Root/feel-pokemon/Prefabs";
    private string outputFolder = "Assets/Mods/Root/feel-pokemon/XML/UIAtlases/ItemIconAtlas";
    private int iconSize = 512;
    private float yawDeg = 45f;   // 0..360
    private float pitchDeg = 25f; // -89..89 is safe

    // Prefab list state
    private readonly List<string> _prefabAssetPaths = new();   // full asset paths
    private readonly List<string> _filteredAssetPaths = new(); // after search filter
    private string _lastScannedAssetFolder = "";
    private string _search = "";
    private Vector2 _listScroll;

    // Row/UI
    private const float RowHeight = 24f;
    private GUIStyle _rowNameStyle;
    private GUIStyle _rowPathStyle;

    [MenuItem("Tools/Feel 7DTD/Prefab Screenshotter")]
    public static void ShowWindow() => GetWindow<ScreenshotPrefabsWindow>("Feel - Prefab Screenshotter");

    void OnGUI()
    {
        EnsureStyles();

        EditorGUILayout.LabelField("Prefab Icon Screenshotter", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // Clear explanation: this tool does NOT modify XML
        EditorGUILayout.HelpBox(
            "This tool does NOT write to any XML. It only renders transparent PNG icons for every prefab in the selected folder.\n\n" +
            "To link the generated icons to blocks/items, add them via XML (CustomIcon) or use the Feel Mod Designer.\n\n" +
            "If you want to batch-generate icons AND automatically link them in XML, use the Feel Mod Designer.",
            MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Output details:\n" +
            "• One transparent PNG per prefab\n" +
            "• File name = prefab name (e.g. MyPrefab → MyPrefab.png)\n" +
            "• Camera uses your yaw/pitch settings",
            MessageType.Info);

        EditorGUILayout.Space();

        // Prefab folder
        EditorGUILayout.LabelField("Prefab folder (Assets/… or absolute path)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        string prevPrefabFolder = prefabFolder;
        prefabFolder = EditorGUILayout.TextField(prefabFolder);
        if (GUILayout.Button("Browse…", GUILayout.MaxWidth(70)))
        {
            string folder = EditorUtility.OpenFolderPanel("Choose prefab folder", Application.dataPath, "");
            if (!string.IsNullOrEmpty(folder))
                prefabFolder = folder.Replace("\\", "/");
        }
        if (GUILayout.Button("Refresh", GUILayout.MaxWidth(70)))
        {
            ScanPrefabs();
        }
        EditorGUILayout.EndHorizontal();

        if (prefabFolder != prevPrefabFolder)
        {
            // If user edited path, auto-refresh when they stop typing (cheap immediate refresh here).
            ScanPrefabs();
        }

        EditorGUILayout.Space();

        // Output folder
        EditorGUILayout.LabelField("Output folder (Assets/… or absolute path)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        outputFolder = EditorGUILayout.TextField(outputFolder);
        if (GUILayout.Button("Browse…", GUILayout.MaxWidth(70)))
        {
            string folder = EditorUtility.OpenFolderPanel("Choose output folder", Application.dataPath, "");
            if (!string.IsNullOrEmpty(folder))
                outputFolder = folder.Replace("\\", "/");
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        iconSize = EditorGUILayout.IntSlider("Icon size (px)", iconSize, 64, 1024);
        yawDeg = EditorGUILayout.Slider("Yaw (°)", yawDeg, 0f, 360f);
        pitchDeg = EditorGUILayout.Slider("Pitch (°)", pitchDeg, -89f, 89f);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Preset: Front/Right")) { yawDeg = 45f; pitchDeg = 25f; }
        if (GUILayout.Button("Preset: Front/Left")) { yawDeg = 315f; pitchDeg = 25f; } // = -45°
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledGroupScope(
                   string.IsNullOrEmpty(prefabFolder) ||
                   string.IsNullOrEmpty(outputFolder)))
        {
            if (GUILayout.Button("Generate icons for all prefabs", GUILayout.Height(30)))
            {
                GenerateScreenshots();
            }
        }

        EditorGUILayout.Space();
        DrawPrefabList();
    }

    // ---------- Prefab list (virtualized) ----------
    private void DrawPrefabList()
    {
        // Make sure list is available
        if (!IsUnderAssets(prefabFolder))
        {
            EditorGUILayout.HelpBox(
                "Prefab list preview requires the folder to be under Assets/.\n" +
                "Current folder is outside the project; listing is disabled.",
                MessageType.Info);
            return;
        }

        // Search/filter bar
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Prefabs in folder", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        _search = GUILayout.TextField(_search, GUI.skin.FindStyle("ToolbarSeachTextField") ?? GUI.skin.textField, GUILayout.MaxWidth(260));
        if (GUILayout.Button("×", GUILayout.Width(22)))
        {
            _search = "";
            ApplyPrefabFilter();
        }
        EditorGUILayout.EndHorizontal();

        // Stats
        EditorGUILayout.LabelField(
            $"{_filteredAssetPaths.Count} shown  •  {_prefabAssetPaths.Count} total",
            EditorStyles.miniLabel);

        // Virtualized list area
        float targetHeight = Mathf.Clamp(position.height - 420f, 180f, 520f);
        Rect outer = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(targetHeight), GUILayout.ExpandWidth(true));

        int count = _filteredAssetPaths.Count;
        Rect view = new Rect(0, 0, outer.width - 16, Mathf.Max(1, count) * RowHeight);

        _listScroll = GUI.BeginScrollView(outer, _listScroll, view);

        int firstIndex = Mathf.Max(0, Mathf.FloorToInt(_listScroll.y / RowHeight));
        int visible = Mathf.CeilToInt(outer.height / RowHeight) + 2;
        int lastIndex = Mathf.Min(count - 1, firstIndex + visible);

        for (int i = firstIndex; i <= lastIndex; i++)
        {
            Rect rowRect = new Rect(0, i * RowHeight, view.width, RowHeight);

            // subtle zebra background
            if ((i & 1) == 0)
                EditorGUI.DrawRect(rowRect, new Color(1f, 1f, 1f, 0.035f));

            string assetPath = _filteredAssetPaths[i];
            string name = Path.GetFileNameWithoutExtension(assetPath);

            // Name (left)
            Rect nameRect = new Rect(rowRect.x + 8, rowRect.y + 2, rowRect.width * 0.45f, RowHeight - 4);
            GUI.Label(nameRect, name, _rowNameStyle);

            // Path (middle, faint)
            Rect pathRect = new Rect(rowRect.x + rowRect.width * 0.46f, rowRect.y + 2, rowRect.width * 0.36f, RowHeight - 4);
            GUI.Label(pathRect, assetPath, _rowPathStyle);

            // Actions (right)
            float btnW = 60f;
            Rect pingRect = new Rect(rowRect.xMax - btnW * 2 - 12, rowRect.y + 2, btnW, RowHeight - 6);
            Rect selectRect = new Rect(rowRect.xMax - btnW - 6, rowRect.y + 2, btnW, RowHeight - 6);

            if (GUI.Button(pingRect, "Ping"))
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (obj != null) { EditorGUIUtility.PingObject(obj); }
            }
            if (GUI.Button(selectRect, "Select"))
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (obj != null) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
            }
        }

        GUI.EndScrollView();

        // Auto re-apply filter if search changed this frame
        if (Event.current.type == EventType.Repaint)
        {
            ApplyPrefabFilter(); // cheap: only re-filters if search changed since last call
        }
    }

    private void EnsureStyles()
    {
        _rowNameStyle ??= new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Bold,
            clipping = TextClipping.Clip
        };
        _rowPathStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip
        };
    }

    private void ApplyPrefabFilter()
    {
        // Fast guard to not rebuild every repaint if unchanged
        // Build filtered list based on current _search
        string needle = (_search ?? "").Trim();
        _filteredAssetPaths.Clear();

        if (string.IsNullOrEmpty(needle))
        {
            _filteredAssetPaths.AddRange(_prefabAssetPaths);
            return;
        }

        string lower = needle.ToLowerInvariant();
        foreach (var p in _prefabAssetPaths)
        {
            // match name or full asset path
            if (p.ToLowerInvariant().Contains(lower) ||
                Path.GetFileNameWithoutExtension(p).ToLowerInvariant().Contains(lower))
            {
                _filteredAssetPaths.Add(p);
            }
        }
    }

    private void ScanPrefabs()
    {
        string assetFolder = EnsureAssetPath(prefabFolder);
        _prefabAssetPaths.Clear();

        if (string.IsNullOrEmpty(assetFolder) || !AssetDatabase.IsValidFolder(assetFolder))
        {
            _lastScannedAssetFolder = "";
            _filteredAssetPaths.Clear();
            return;
        }

        _lastScannedAssetFolder = assetFolder;

        var guids = AssetDatabase.FindAssets("t:Prefab", new[] { assetFolder });
        foreach (var guid in guids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                _prefabAssetPaths.Add(assetPath);
        }

        _prefabAssetPaths.Sort(System.StringComparer.OrdinalIgnoreCase);
        ApplyPrefabFilter();
        Repaint();
    }

    private bool IsUnderAssets(string path)
    {
        string asset = EnsureAssetPath(path);
        return !string.IsNullOrEmpty(asset) && AssetDatabase.IsValidFolder(asset);
    }

    private void GenerateScreenshots()
    {
        // Prefab folder: convert to Assets/… for AssetDatabase
        string prefabAssetPath = EnsureAssetPath(prefabFolder);
        if (string.IsNullOrEmpty(prefabAssetPath) || !AssetDatabase.IsValidFolder(prefabAssetPath))
        {
            EditorUtility.DisplayDialog(
                "Invalid prefab folder",
                "The prefab folder does not exist or is not under Assets/:\n" + prefabFolder,
                "OK");
            return;
        }

        // Output folder: filesystem path (and asset path if inside Assets)
        string outputSystemPath = EnsureSystemPath(outputFolder);
        if (string.IsNullOrEmpty(outputSystemPath))
        {
            EditorUtility.DisplayDialog(
                "Invalid output folder",
                "The output folder could not be resolved:\n" + outputFolder,
                "OK");
            return;
        }

        if (!Directory.Exists(outputSystemPath))
            Directory.CreateDirectory(outputSystemPath);

        var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { prefabAssetPath });
        if (prefabGuids == null || prefabGuids.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "No prefabs found",
                "No prefabs were found in:\n" + prefabAssetPath,
                "OK");
            return;
        }

        try
        {
            for (int i = 0; i < prefabGuids.Length; i++)
            {
                string guid = prefabGuids[i];
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null) continue;

                string fileName = prefab.name + ".png";
                string savePath = Path.Combine(outputSystemPath, fileName).Replace("\\", "/");

                float progress = (float)(i + 1) / prefabGuids.Length;
                EditorUtility.DisplayProgressBar(
                    "Generating prefab icons",
                    prefab.name + " (" + (i + 1) + "/" + prefabGuids.Length + ")",
                    progress);

                ScreenshotPrefabs.TryMakePrefabIcon(prefab, savePath, iconSize, yawDeg, pitchDeg);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Debug.Log("Transparent prefab icons saved to: " + outputSystemPath +
                  "\n(Remember: this tool does NOT modify XML. Link the icons via XML or the Feel Mod Designer.)");
    }

    /// <summary>
    /// Tries to normalize a path to an Assets/… asset path.
    /// If it's already "Assets/…", it is returned as-is.
    /// If it's an absolute path under this project, it is converted.
    /// Otherwise returns null.
    /// </summary>
    private static string EnsureAssetPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        path = path.Replace("\\", "/");

        if (path.StartsWith("Assets"))
            return path;

        string dataPath = Application.dataPath.Replace("\\", "/");

        if (path.StartsWith(dataPath))
            return "Assets" + path.Substring(dataPath.Length);

        return null; // outside Assets
    }

    /// <summary>
    /// Ensures a valid system path.
    /// If it starts with "Assets", it will be converted to an absolute path in this project.
    /// Otherwise the path is returned as-is (assumed to be a valid absolute/relative system path).
    /// </summary>
    private static string EnsureSystemPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        path = path.Replace("\\", "/");

        if (path.StartsWith("Assets"))
        {
            string projectRoot = Application.dataPath.Replace("\\", "/");
            projectRoot = projectRoot.Substring(0, projectRoot.Length - "Assets".Length);
            return Path.Combine(projectRoot, path).Replace("\\", "/");
        }

        return path;
    }
}
