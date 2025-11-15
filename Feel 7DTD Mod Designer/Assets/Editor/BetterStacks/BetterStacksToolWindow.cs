// Assets/Editor/BetterStacksToolWindow.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using Microsoft.Win32; // Voor Steam registry lookup

public class BetterStacksToolWindow : EditorWindow
{
    // === Defaults ===
    private string modsBasePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "7DaysToDie", "Mods");

    private string configBasePath =
        Path.Combine("D:\\", "Programs", "Steam", "steamapps", "common",
                     "7 Days To Die", "Data", "Config");

    // Template map waar ModInfo.xml + readme.md staan
    private string templateFolderPath;

    private string modNameBase = "feel-betterstacks";

    // Actie: óf multiply óf set
    private enum StackAction { Multiply, Set }
    private StackAction selectedAction = StackAction.Multiply;

    // Types
    private bool typeItem = true;
    private bool typeBlock = true;

    // CSV inputs
    private string numbersCsv = "100,200,1000,10000,25000,30000,50000";
    private string exclusionsCsv = "";

    private Vector2 _scroll;

    // AUTO-DETECT flag
    private bool autoDetectTried = false;

    [MenuItem("Tools/Feel 7DTD/Better Stacks Generator")]
    public static void ShowWindow()
    {
        var win = GetWindow<BetterStacksToolWindow>("Better Stacks");
        win.minSize = new Vector2(600, 380);
        win.InitDefaults();
    }

    private void InitDefaults()
    {
        if (string.IsNullOrEmpty(templateFolderPath))
        {
            try
            {
                templateFolderPath = Directory.GetParent(Application.dataPath)?.FullName
                                     ?? Directory.GetCurrentDirectory();
            }
            catch
            {
                templateFolderPath = Directory.GetCurrentDirectory();
            }
        }
    }

    private void OnGUI()
    {
        InitDefaults();
        TryAutoDetect7DTDPathsOnce();  // AUTO-DETECT, éénmalig bij openen

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("Better Stacks Generator", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        // --- Paths ---
        EditorGUILayout.LabelField("Paths", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Mods base path", GUILayout.Width(120));
        modsBasePath = EditorGUILayout.TextField(modsBasePath);
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string sel = EditorUtility.OpenFolderPanel("Select Mods base folder", modsBasePath, "");
            if (!string.IsNullOrEmpty(sel))
                modsBasePath = sel;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Config base path", GUILayout.Width(120));
        configBasePath = EditorGUILayout.TextField(configBasePath);
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string sel = EditorUtility.OpenFolderPanel("Select 7DTD Data/Config folder", configBasePath, "");
            if (!string.IsNullOrEmpty(sel))
                configBasePath = sel;
        }
        EditorGUILayout.EndHorizontal();

        // Handmatige trigger voor auto-detect (optioneel)
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Auto-detect 7DTD", GUILayout.Width(160)))
        {
            TryAutoDetect7DTDPaths(showDialog: true);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Template folder", GUILayout.Width(120));
        templateFolderPath = EditorGUILayout.TextField(templateFolderPath);
        if (GUILayout.Button("Browse", GUILayout.Width(70)))
        {
            string sel = EditorUtility.OpenFolderPanel("Select template folder (ModInfo.xml + readme.md)", templateFolderPath, "");
            if (!string.IsNullOrEmpty(sel))
                templateFolderPath = sel;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        // --- Mod naam ---
        EditorGUILayout.LabelField("Mod naming", EditorStyles.boldLabel);
        modNameBase = EditorGUILayout.TextField("Base mod name", modNameBase);

        EditorGUILayout.Space();

        // --- Action (ófk multiply óf set) ---
        EditorGUILayout.LabelField("Action", EditorStyles.boldLabel);
        selectedAction = (StackAction)EditorGUILayout.EnumPopup("Mode", selectedAction);

        EditorGUILayout.Space();

        // --- Types ---
        EditorGUILayout.LabelField("Types", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        typeItem = EditorGUILayout.ToggleLeft("item", typeItem, GUILayout.Width(120));
        typeBlock = EditorGUILayout.ToggleLeft("block", typeBlock, GUILayout.Width(120));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        // --- Numbers ---
        EditorGUILayout.LabelField("Stack numbers", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Comma-separated integers. Voorbeeld: 100,200,1000,10000,25000,30000,50000",
            MessageType.Info);
        numbersCsv = EditorGUILayout.TextField("Numbers", numbersCsv);

        EditorGUILayout.Space();

        // --- Exclusions ---
        EditorGUILayout.LabelField("Exclusions", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Comma-separated substrings. Als een item/block-naam één van deze substrings bevat, " +
            "wordt hij overgeslagen.",
            MessageType.Info);
        exclusionsCsv = EditorGUILayout.TextField("Exclude names", exclusionsCsv);

        EditorGUILayout.Space(10);

        using (new EditorGUI.DisabledGroupScope(!CanRun()))
        {
            if (GUILayout.Button("Generate Better Stack Mods", GUILayout.Height(32)))
            {
                RunGenerator();
            }
        }

        if (!CanRun())
        {
            EditorGUILayout.HelpBox(
                "Controleer of er een geldige Mods path en Config path zijn, en kies minstens 1 type (item/block).",
                MessageType.Warning);
        }

        EditorGUILayout.EndScrollView();
    }

    private bool CanRun()
    {
        if (string.IsNullOrEmpty(modsBasePath) || string.IsNullOrEmpty(configBasePath))
            return false;
        if (!Directory.Exists(configBasePath))
            return false;
        if (!typeItem && !typeBlock)
            return false;
        return true;
    }

    private void RunGenerator()
    {
        string action = selectedAction == StackAction.Multiply ? "multiply" : "set";

        var types = new List<string>();
        if (typeItem) types.Add("item");
        if (typeBlock) types.Add("block");

        var numbers = ParseIntList(numbersCsv);
        if (numbers.Count == 0)
        {
            EditorUtility.DisplayDialog("No numbers", "Geen geldige integers gevonden in 'Numbers'.", "OK");
            return;
        }

        var exclusions = ParseStringList(exclusionsCsv);
        if (exclusions.Count == 1 && string.IsNullOrWhiteSpace(exclusions[0]))
            exclusions.Clear();

        try
        {
            int totalSteps = numbers.Count * types.Count;
            int step = 0;

            foreach (var num in numbers)
            {
                foreach (var type in types)
                {
                    float progress = (float)step / Mathf.Max(1, totalSteps);
                    EditorUtility.DisplayProgressBar(
                        "Generating Better Stacks",
                        $"{action} {num} {type}s",
                        progress);

                    CreateModFolders(action, num, type, modNameBase,
                                     modsBasePath, configBasePath, templateFolderPath,
                                     exclusions);
                    step++;
                }
            }

            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("Done", "All operations completed successfully!\nCheck the output in the Mods directory.", "OK");
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError("[BetterStacks] Error: " + ex);
            EditorUtility.DisplayDialog("Error", ex.Message, "OK");
        }
    }

    // === CORE LOGIC ===

    private void CreateModFolders(
        string action,
        int number,
        string type,
        string modNameBase,
        string modsBasePath,
        string configBasePath,
        string templateFolder,
        List<string> exclusions)
    {
        string modName = $"{modNameBase}_{action}_{number}";
        string modPath = Path.Combine(modsBasePath, modName);
        string configPath = Path.Combine(modPath, "Config");
        Directory.CreateDirectory(configPath);

        string originalFilePath = Path.Combine(configBasePath, $"{type}s.xml");
        if (!File.Exists(originalFilePath))
        {
            Debug.LogWarning($"[{modName}] Source file not found: {originalFilePath}");
            return;
        }

        XDocument xdoc = XDocument.Load(originalFilePath);
        XElement root = xdoc.Root;
        if (root == null)
        {
            Debug.LogWarning($"[{modName}] Root missing in {originalFilePath}");
            return;
        }

        string outFile = Path.Combine(configPath, $"{type}s.xml");
        using (var writer = new StreamWriter(outFile, false, Encoding.UTF8))
        {
            writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            writer.WriteLine("<configs>");

            var elements = root
                .Descendants(type)
                .Where(e => e.Attribute("name") != null);

            foreach (var element in elements)
            {
                string elementName = (string)element.Attribute("name") ?? "";

                if (exclusions.Any(excl =>
                        !string.IsNullOrEmpty(excl) &&
                        elementName.IndexOf(excl, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                var modSlotsEffect = element
                    .Descendants("passive_effect")
                    .FirstOrDefault(pe => (string)pe.Attribute("name") == "ModSlots");

                if (modSlotsEffect != null)
                {
                    string value = (string)modSlotsEffect.Attribute("value") ?? "0";
                    if (HasModSlots(value))
                    {
                        continue;
                    }
                }

                var stackProp = element
                    .Descendants("property")
                    .FirstOrDefault(p => (string)p.Attribute("name") == "Stacknumber");

                if (stackProp == null)
                    continue;

                string stackVal = (string)stackProp.Attribute("value") ?? "";
                if (!Regex.IsMatch(stackVal, @"^\d+$"))
                    continue;

                int oldValue = int.Parse(stackVal);
                int newValue = action == "multiply" ? oldValue * number : number;

                string safeName = EscapeForXPathName(elementName);
                string xpath = $"/{type}s/{type}[@name='{safeName}']/property[@name='Stacknumber']/@value";

                writer.Write("   <set xpath=\"");
                writer.Write(xpath);
                writer.Write("\">");
                writer.Write(newValue);
                writer.WriteLine("</set>");
            }

            writer.WriteLine("</configs>");
        }

        if (string.IsNullOrEmpty(templateFolder))
        {
            templateFolder = Directory.GetCurrentDirectory();
        }

        foreach (var filename in new[] { "ModInfo.xml", "readme.md" })
        {
            string src = Path.Combine(templateFolder, filename);
            string dst = Path.Combine(modPath, filename);
            if (File.Exists(src))
            {
                File.Copy(src, dst, true);
            }
            else
            {
                Debug.LogWarning($"[{modName}] Template file not found: {src}");
            }
        }

        Debug.Log($"[BetterStacks] Generated mod: {modName}");
    }

    // === AUTO-DETECT 7DTD ===

    private void TryAutoDetect7DTDPathsOnce()
    {
        if (autoDetectTried) return;
        autoDetectTried = true;
        TryAutoDetect7DTDPaths(showDialog: false);
    }

    private void TryAutoDetect7DTDPaths(bool showDialog)
    {
        string foundConfig = Find7DTDConfigPath();
        string foundMods = null;

        if (!string.IsNullOrEmpty(foundConfig))
        {
            configBasePath = foundConfig;

            // Probeer eerst AppData\7DaysToDie\Mods
            var appDataMods = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "7DaysToDie", "Mods");

            if (Directory.Exists(appDataMods))
            {
                foundMods = appDataMods;
            }
            else
            {
                // Anders Mods-map naast de game-installatie
                var gameDir = Directory.GetParent(Directory.GetParent(foundConfig).FullName)?.FullName; // ...\7 Days To Die
                if (!string.IsNullOrEmpty(gameDir))
                {
                    var modsDir = Path.Combine(gameDir, "Mods");
                    if (Directory.Exists(modsDir))
                        foundMods = modsDir;
                }
            }
        }

        if (!string.IsNullOrEmpty(foundMods))
            modsBasePath = foundMods;

        if (showDialog)
        {
            if (!string.IsNullOrEmpty(foundConfig))
            {
                EditorUtility.DisplayDialog(
                    "7 Days to Die gevonden",
                    $"Config path:\n{foundConfig}\n\nMods path:\n{modsBasePath}",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "7 Days to Die niet automatisch gevonden",
                    "De installatie kon niet automatisch worden gevonden.\n" +
                    "Vul de paden handmatig in of gebruik de Browse-knoppen.",
                    "OK");
            }
        }
    }

    private string Find7DTDConfigPath()
    {
        // 1) Probeer al bestaande path als hij klopt
        if (!string.IsNullOrEmpty(configBasePath) &&
            Directory.Exists(configBasePath) &&
            File.Exists(Path.Combine(configBasePath, "items.xml")))
        {
            return configBasePath;
        }

        // 2) Via Steam registry
        var candidates = new List<string>();
        string steamPath = GetSteamPathFromRegistry();
        if (!string.IsNullOrEmpty(steamPath))
        {
            var commonDefault = Path.Combine(steamPath, "steamapps", "common");
            if (Directory.Exists(commonDefault))
                candidates.Add(commonDefault);

            // Library folders
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(vdf))
                    {
                        var m = Regex.Match(line, "\"path\"\\s*\"([^\"]+)\"");
                        if (m.Success)
                        {
                            var lib = m.Groups[1].Value.Replace("\\\\", "\\");
                            if (Directory.Exists(lib))
                            {
                                var common = Path.Combine(lib, "steamapps", "common");
                                if (Directory.Exists(common))
                                    candidates.Add(common);
                            }
                        }
                    }
                }
                catch { /* ignore */ }
            }
        }

        // 3) Fallback: standaard Steam locaties
        void AddIfExists(string path)
        {
            if (Directory.Exists(path)) candidates.Add(path);
        }

        var pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        AddIfExists(Path.Combine(pfX86, "Steam", "steamapps", "common"));
        AddIfExists(Path.Combine(pf, "Steam", "steamapps", "common"));

        // 4) Doorloop kandidaten en zoek "7 Days To Die"
        foreach (var common in candidates.Distinct())
        {
            try
            {
                var gameDir = Path.Combine(common, "7 Days To Die");
                if (!Directory.Exists(gameDir))
                    continue;

                var cfg = Path.Combine(gameDir, "Data", "Config");
                if (Directory.Exists(cfg) && File.Exists(Path.Combine(cfg, "items.xml")))
                    return cfg;
            }
            catch
            {
                // negeren
            }
        }

        return null;
    }

    private static string GetSteamPathFromRegistry()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (key != null)
                {
                    var path = key.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(path))
                        return path.Replace("/", "\\");
                }
            }
        }
        catch
        {
            // geen Steam of geen Windows
        }

        return null;
    }

    // === Helpers ===

    private static List<int> ParseIntList(string csv)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(csv)) return list;

        foreach (var part in csv.Split(','))
        {
            var trimmed = part.Trim();
            if (int.TryParse(trimmed, out int v))
                list.Add(v);
        }
        return list;
    }

    private static List<string> ParseStringList(string csv)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(csv)) return list;

        foreach (var part in csv.Split(','))
        {
            var trimmed = part.Trim();
            list.Add(trimmed);
        }
        return list;
    }

    private static bool HasModSlots(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        foreach (var part in value.Split(','))
        {
            string t = part.Trim();
            if (int.TryParse(t, out int v) && v > 0)
                return true;
        }
        return false;
    }

    private static string EscapeForXPathName(string name)
    {
        return string.IsNullOrEmpty(name)
            ? name
            : name.Replace("'", "&apos;");
    }
}
