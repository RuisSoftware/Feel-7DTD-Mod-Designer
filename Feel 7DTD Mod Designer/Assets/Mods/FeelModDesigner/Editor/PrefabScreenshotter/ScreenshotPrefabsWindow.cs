using System.IO;
using UnityEditor;
using UnityEngine;

public class ScreenshotPrefabsWindow : EditorWindow
{
    // Dit zijn de teksten in de UI (mogen zowel Assets/… als absolute paden zijn)
    private string prefabFolder = "Assets/Mods/Root/feel-pokemon/Prefabs";
    private string outputFolder = "Assets/Mods/Root/feel-pokemon/XML/UIAtlases/ItemIconAtlas";
    private int iconSize = 512;
    private float yawDeg = 45f;   // 0..360
    private float pitchDeg = 25f; // -89..89 is veilig

    // —————— MENU: opent nu een EditorWindow ——————
    [MenuItem("Tools/Feel 7DTD/Prefab Screenshotter")]
    public static void ShowWindow() => GetWindow<ScreenshotPrefabsWindow>("Feel - Prefab Screenshotter");

    void OnGUI()
    {
        EditorGUILayout.LabelField("Prefab Screenshotter", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        EditorGUILayout.HelpBox(
            "Selecteer een prefab-map en een output-map.\n" +
            "Voor alle gevonden prefabs wordt een transparante PNG-icon gemaakt.",
            MessageType.Info);

        EditorGUILayout.Space();

        // Prefab map
        EditorGUILayout.LabelField("Prefab map (Assets/… of volledig pad)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        prefabFolder = EditorGUILayout.TextField(prefabFolder);
        if (GUILayout.Button("Kies…", GUILayout.MaxWidth(60)))
        {
            string folder = EditorUtility.OpenFolderPanel("Kies prefab map", Application.dataPath, "");
            if (!string.IsNullOrEmpty(folder))
            {
                prefabFolder = folder.Replace("\\", "/");
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        // Output map
        EditorGUILayout.LabelField("Output map (Assets/… of volledig pad)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        outputFolder = EditorGUILayout.TextField(outputFolder);
        if (GUILayout.Button("Kies…", GUILayout.MaxWidth(60)))
        {
            string folder = EditorUtility.OpenFolderPanel("Kies output map", Application.dataPath, "");
            if (!string.IsNullOrEmpty(folder))
            {
                outputFolder = folder.Replace("\\", "/");
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        iconSize = EditorGUILayout.IntSlider("Icon size (px)", iconSize, 64, 1024);

        yawDeg = EditorGUILayout.Slider("Yaw (°)", yawDeg, 0f, 360f);
        pitchDeg = EditorGUILayout.Slider("Pitch (°)", pitchDeg, -89f, 89f);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Preset: Front/Right")) { yawDeg = 45f; pitchDeg = 25f; }
        if (GUILayout.Button("Preset: Front/Left")) { yawDeg = 315f; pitchDeg = 25f; } // of -45°
        EditorGUILayout.EndHorizontal();


        EditorGUILayout.Space();

        using (new EditorGUI.DisabledGroupScope(
                   string.IsNullOrEmpty(prefabFolder) ||
                   string.IsNullOrEmpty(outputFolder)))
        {
            if (GUILayout.Button("Genereer screenshots voor alle prefabs", GUILayout.Height(30)))
            {
                GenerateScreenshots();
            }
        }
    }

    private void GenerateScreenshots()
    {
        // --- Prefab folder: converteer naar Assets/… voor AssetDatabase ---
        string prefabAssetPath = EnsureAssetPath(prefabFolder);
        if (string.IsNullOrEmpty(prefabAssetPath) || !AssetDatabase.IsValidFolder(prefabAssetPath))
        {
            EditorUtility.DisplayDialog(
                "Ongeldige prefab map",
                "De prefab map bestaat niet of ligt niet onder Assets/:\n" + prefabFolder,
                "OK");
            return;
        }

        // --- Output folder: filesystem path + asset path (indien binnen Assets) ---
        string outputSystemPath = EnsureSystemPath(outputFolder);
        if (string.IsNullOrEmpty(outputSystemPath))
        {
            EditorUtility.DisplayDialog(
                "Ongeldige output map",
                "De output map kon niet worden geïnterpreteerd:\n" + outputFolder,
                "OK");
            return;
        }

        if (!Directory.Exists(outputSystemPath))
        {
            Directory.CreateDirectory(outputSystemPath);
        }

        var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { prefabAssetPath });
        if (prefabGuids == null || prefabGuids.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Geen prefabs gevonden",
                "Geen prefabs gevonden in:\n" + prefabAssetPath,
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
                if (prefab == null)
                    continue;

                string fileName = prefab.name + ".png";
                string savePath = Path.Combine(outputSystemPath, fileName).Replace("\\", "/");

                float progress = (float)(i + 1) / prefabGuids.Length;
                EditorUtility.DisplayProgressBar(
                    "Prefab screenshots genereren",
                    prefab.name + " (" + (i + 1) + "/" + prefabGuids.Length + ")",
                    progress);

                ScreenshotPrefabs.TryMakePrefabIcon(prefab, savePath, iconSize, yawDeg, pitchDeg);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Debug.Log("Transparante icons opgeslagen in " + outputSystemPath);
    }

    /// <summary>
    /// Probeert van een willekeurig pad een Assets/… path te maken.
    /// - Als het al met "Assets" begint, wordt het gewoon opgeschoond.
    /// - Als het een absoluut pad is onder het project, wordt het omgezet.
    /// </summary>
    private static string EnsureAssetPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        path = path.Replace("\\", "/");

        if (path.StartsWith("Assets"))
            return path;

        string dataPath = Application.dataPath.Replace("\\", "/");

        // Volledig pad naar Assets/… binnen dit project?
        if (path.StartsWith(dataPath))
        {
            return "Assets" + path.Substring(dataPath.Length);
        }

        return null; // ligt buiten Assets
    }

    /// <summary>
    /// Zorgt voor een geldig system path:
    /// - Als het met "Assets" begint, wordt het naar een absolute pad in het project omgezet.
    /// - Anders wordt het gezien als een al-bestaand systeem-pad.
    /// </summary>
    private static string EnsureSystemPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        path = path.Replace("\\", "/");

        if (path.StartsWith("Assets"))
        {
            string projectRoot = Application.dataPath.Replace("\\", "/");
            projectRoot = projectRoot.Substring(0, projectRoot.Length - "Assets".Length);
            return Path.Combine(projectRoot, path).Replace("\\", "/");
        }

        // Anders gaan we er vanuit dat het al een absolute (of relative) system path is
        return path;
    }
}
