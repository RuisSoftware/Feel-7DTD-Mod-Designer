using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Editor window to link 7 Days to Die managed assemblies (DLLs)
/// into your Unity project via csc.rsp so you can use 7DTD types
/// directly in C# scripts.
/// 
/// What it does now:
/// - Detects the 7DTD Managed folder (client or server)
/// - Lets you pick which DLLs to use
/// - Copies those DLLs into Assets/7DTDLibs/
/// - Creates Assets/csc.rsp with -r:7DTDLibs/SomeDll.dll lines
/// </summary>
public class SevenDTDLibrariesWindow : EditorWindow
{
    private const string PrefKeyInstallPath = "SevenDTD.InstallPath";
    private const string LibsFolderName = "7DTDLibs"; // under Assets/

    private string installPath;   // User-chosen 7DTD install or Managed folder
    private string managedPath;   // Resolved Managed folder (7DaysToDie_Data/Managed, etc.)
    private bool managedPathValid;

    private Vector2 dllScroll;

    private class DllInfo
    {
        public string name;
        public string fullPath;
        public bool selected;
        public bool recommended;
    }

    private List<DllInfo> foundDlls = new List<DllInfo>();
    private bool dllsScanned;

    // DLLs that are usually useful when modding 7DTD
    private static readonly string[] RecommendedDllNames =
    {
        "Assembly-CSharp.dll",
        "Assembly-CSharp-firstpass.dll",
        "0Harmony.dll"
        // Add more favorites here if you want
    };

    [MenuItem("Tools/Feel 7DTD/Setup 7DTD Libraries")]
    public static void Open()
    {
        var window = GetWindow<SevenDTDLibrariesWindow>("7DTD Libraries");
        window.minSize = new Vector2(600, 320);
        window.Show();
    }

    private void OnEnable()
    {
        installPath = EditorPrefs.GetString(PrefKeyInstallPath, string.Empty);
        TryResolveManagedPath();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("7 Days to Die Library Setup", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        EditorGUILayout.HelpBox(
            "Point this tool to your 7 Days to Die installation folder or directly to the Managed folder.\n" +
            "It will:\n" +
            "- Copy selected 7DTD DLLs into Assets/" + LibsFolderName + "/\n" +
            "- Generate Assets/csc.rsp with references to those DLLs\n\n" +
            "After that, your C# scripts can reference 7DTD types like GameManager, EntityPlayer, Harmony, etc.",
            MessageType.Info);

        EditorGUILayout.Space();

        DrawInstallPathSection();
        EditorGUILayout.Space();

        DrawManagedPathInfo();
        EditorGUILayout.Space();

        using (new EditorGUI.DisabledGroupScope(!managedPathValid))
        {
            if (GUILayout.Button("Scan DLLs in Managed folder", GUILayout.Height(24)))
            {
                ScanDlls();
            }
        }

        EditorGUILayout.Space();

        DrawDllList();

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledGroupScope(!managedPathValid || !dllsScanned || !foundDlls.Any(d => d.selected)))
        {
            if (GUILayout.Button("Create / Update Assets/csc.rsp with selected DLLs", GUILayout.Height(28)))
            {
                CreateOrUpdateRspAndCopyDlls();
            }
        }
    }

    private void DrawInstallPathSection()
    {
        EditorGUILayout.LabelField("7DTD install folder / Managed folder", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        installPath = EditorGUILayout.TextField(installPath);
        if (GUILayout.Button("Browse…", GUILayout.MaxWidth(80)))
        {
            string folder = EditorUtility.OpenFolderPanel("Select 7 Days To Die folder or Managed folder", installPath, "");
            if (!string.IsNullOrEmpty(folder))
            {
                installPath = folder.Replace("\\", "/");
                EditorPrefs.SetString(PrefKeyInstallPath, installPath);
                TryResolveManagedPath();
            }
        }
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Try to auto-detect Managed folder", GUILayout.MaxWidth(280)))
        {
            TryResolveManagedPath(true);
        }
    }

    private void DrawManagedPathInfo()
    {
        EditorGUILayout.LabelField("Resolved Managed folder", EditorStyles.boldLabel);

        if (string.IsNullOrEmpty(managedPath))
        {
            EditorGUILayout.HelpBox(
                "No valid Managed folder detected yet.\n" +
                "Select your 7DTD installation folder or the Managed folder directly,\n" +
                "then click 'Try to auto-detect Managed folder'.",
                MessageType.Warning);
        }
        else
        {
            using (new EditorGUI.DisabledGroupScope(true))
            {
                EditorGUILayout.TextField(managedPath);
            }

            if (!managedPathValid)
            {
                EditorGUILayout.HelpBox(
                    "The Managed folder does not exist. Please verify that your installPath is correct.\n" +
                    "Normally this is the folder that contains '7DaysToDie.exe' or '7DaysToDieServer.exe',\n" +
                    "or the '*_Data/Managed' folder itself.",
                    MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Managed folder detected. You can now scan for DLLs.",
                    MessageType.Info);
            }
        }
    }

    private void DrawDllList()
    {
        if (!dllsScanned)
        {
            EditorGUILayout.HelpBox("No scan performed yet. Click 'Scan DLLs in Managed folder' first.", MessageType.None);
            return;
        }

        if (foundDlls.Count == 0)
        {
            EditorGUILayout.HelpBox("No DLLs found in the Managed folder.", MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField("Found DLLs", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Select recommended"))
        {
            foreach (var dll in foundDlls)
            {
                dll.selected = dll.recommended;
            }
        }
        if (GUILayout.Button("Select all"))
        {
            foreach (var dll in foundDlls)
            {
                dll.selected = true;
            }
        }
        if (GUILayout.Button("Deselect all"))
        {
            foreach (var dll in foundDlls)
            {
                dll.selected = false;
            }
        }
        EditorGUILayout.EndHorizontal();

        dllScroll = EditorGUILayout.BeginScrollView(dllScroll, GUILayout.Height(160));
        foreach (var dll in foundDlls)
        {
            EditorGUILayout.BeginHorizontal();
            dll.selected = EditorGUILayout.Toggle(dll.selected, GUILayout.Width(20));

            var label = dll.name;
            if (dll.recommended) label += "  (recommended)";

            EditorGUILayout.LabelField(label);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();
    }

    // ----------------- Logic: resolving Managed folder & scanning DLLs -----------------

    private void TryResolveManagedPath(bool showDialogOnFail = false)
    {
        managedPath = null;
        managedPathValid = false;

        if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
        {
            if (showDialogOnFail)
            {
                EditorUtility.DisplayDialog(
                    "Invalid folder",
                    "The selected folder does not exist:\n" + installPath,
                    "OK");
            }
            return;
        }

        string path = installPath.Replace("\\", "/");

        // 1) User selected the Managed folder directly
        if (path.EndsWith("/Managed") && Directory.Exists(path))
        {
            managedPath = path;
            managedPathValid = true;
            return;
        }

        // 2) Try client: 7DaysToDie_Data/Managed
        string clientManaged = Path.Combine(path, "7DaysToDie_Data/Managed").Replace("\\", "/");
        if (Directory.Exists(clientManaged))
        {
            managedPath = clientManaged;
            managedPathValid = true;
            return;
        }

        // 3) Try dedicated server: 7DaysToDieServer_Data/Managed
        string serverManaged = Path.Combine(path, "7DaysToDieServer_Data/Managed").Replace("\\", "/");
        if (Directory.Exists(serverManaged))
        {
            managedPath = serverManaged;
            managedPathValid = true;
            return;
        }

        if (showDialogOnFail)
        {
            EditorUtility.DisplayDialog(
                "Managed folder not found",
                "Could not find a Managed folder under:\n" + installPath + "\n\n" +
                "Make sure this is the actual 7DTD installation folder (with 7DaysToDie.exe or 7DaysToDieServer.exe),\n" +
                "or select the '*_Data/Managed' folder directly.",
                "OK");
        }
    }

    private void ScanDlls()
    {
        dllsScanned = false;
        foundDlls.Clear();

        if (!managedPathValid)
        {
            EditorUtility.DisplayDialog(
                "Managed folder invalid",
                "There is no valid Managed folder.\n" +
                "Try to auto-detect the Managed folder first.",
                "OK");
            return;
        }

        try
        {
            var dllFiles = Directory.GetFiles(managedPath, "*.dll", SearchOption.TopDirectoryOnly);

            foreach (var dllPath in dllFiles)
            {
                var fileName = Path.GetFileName(dllPath);
                bool isRecommended = RecommendedDllNames.Contains(fileName);

                foundDlls.Add(new DllInfo
                {
                    name = fileName,
                    fullPath = dllPath.Replace("\\", "/"),
                    selected = isRecommended,
                    recommended = isRecommended
                });
            }

            // Sort: recommended first, then alphabetical
            foundDlls = foundDlls
                .OrderByDescending(d => d.recommended)
                .ThenBy(d => d.name)
                .ToList();

            dllsScanned = true;

            if (foundDlls.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "No DLLs found",
                    "No .dll files were found in:\n" + managedPath,
                    "OK");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError("Error while scanning DLLs: " + ex);
            EditorUtility.DisplayDialog(
                "Scan error",
                "Something went wrong while scanning DLLs.\nCheck the Console for details.",
                "OK");
        }
    }

    // ----------------- csc.rsp generation (no copying, use absolute paths) -----------------
    private void CreateOrUpdateRspAndCopyDlls()
    {
        // Assets/csc.rsp is where Unity expects it
        string assetsPath = Application.dataPath.Replace("\\", "/");
        string rspPath = Path.Combine(assetsPath, "csc.rsp").Replace("\\", "/");

        var selected = foundDlls.Where(d => d.selected).ToList();
        if (selected.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "No DLLs selected",
                "Select at least one DLL before creating csc.rsp.",
                "OK");
            return;
        }

        if (File.Exists(rspPath))
        {
            bool overwrite = EditorUtility.DisplayDialog(
                "Assets/csc.rsp already exists",
                "A csc.rsp already exists in your Assets folder:\n" + rspPath + "\n\n" +
                "Do you want to OVERWRITE this file?\n" +
                "(If you added custom compiler options manually, they will be lost.)",
                "Overwrite",
                "Cancel");

            if (!overwrite)
                return;
        }

        var sb = new StringBuilder();

        try
        {
            foreach (var dll in selected)
            {
                // Make sure we have an absolute, normalized path
                string fullPath = Path.GetFullPath(dll.fullPath).Replace("\\", "/");

                // Quote the path because of spaces
                sb.AppendLine($"-r:\"{fullPath}\"");
            }

            File.WriteAllText(rspPath, sb.ToString(), Encoding.UTF8);
            AssetDatabase.Refresh();
        }
        catch (System.Exception ex)
        {
            Debug.LogError("Error writing csc.rsp: " + ex);
            EditorUtility.DisplayDialog(
                "Write error",
                "Could not write Assets/csc.rsp.\nCheck the Console for details.",
                "OK");
            return;
        }

        EditorUtility.DisplayDialog(
            "Done",
            "csc.rsp has been created/updated at:\n" + rspPath + "\n\n" +
            "The 7DTD DLLs are still in their original location.\n\n" +
            "Unity should now recompile. If IntelliSense still doesn't pick it up,\n" +
            "go to Unity and choose 'Assets → Open C# Project' to regenerate the .csproj files.",
            "OK");
    }

}
