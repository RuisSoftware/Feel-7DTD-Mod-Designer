using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

public class LocalizationModule : IConfigModule
{
    public string ModuleName => "Localization";

    ModContext ctx;
    List<string> filteredKeys = new();
    int selected = -1;
    string search = "";
    Vector2 keysScroll;

    // Start is called before the first frame update
    public void Initialize(ModContext ctx)
    {
        this.ctx = ctx;
        string locPath = Path.Combine(ctx.ModConfigPath, "Localization.txt");
        ctx.LocalizationEntries.Clear();

        if (!File.Exists(locPath))
            return;

        var lines = File.ReadAllLines(locPath);
        if (lines.Length == 0) return;

        // Header parsen met CSV-parser i.p.v. naive split
        List<string> headerCols = ParseCsvLine(lines[0]);
        // Standaardkolommen:
        int keyIndex = headerCols.FindIndex(c => c.Equals("Key", StringComparison.OrdinalIgnoreCase));
        int sourceIndex = headerCols.FindIndex(c => c.Equals("Source", StringComparison.OrdinalIgnoreCase));
        int contextIndex = headerCols.FindIndex(c => c.Equals("Context", StringComparison.OrdinalIgnoreCase));
        int changesIndex = headerCols.FindIndex(c => c.Equals("Changes", StringComparison.OrdinalIgnoreCase));

        // Alle overige kolommen behandelen als dynamische talen
        var languageCols = new List<(int idx, string name)>();
        for (int i = 0; i < headerCols.Count; i++)
        {
            if (i == keyIndex || i == sourceIndex || i == contextIndex || i == changesIndex) continue;
            languageCols.Add((i, headerCols[i]));
        }

        for (int li = 1; li < lines.Length; li++)
        {
            var row = ParseCsvLine(lines[li]);
            if (row.Count == 0) continue;

            string key = (keyIndex >= 0 && keyIndex < row.Count) ? row[keyIndex] : "";
            if (string.IsNullOrWhiteSpace(key)) continue;

            var entry = new LocalizationEntry
            {
                Key = key,
                Source = (sourceIndex >= 0 && sourceIndex < row.Count) ? row[sourceIndex] : "",
                Context = (contextIndex >= 0 && contextIndex < row.Count) ? row[contextIndex] : "",
                Changes = (changesIndex >= 0 && changesIndex < row.Count) ? row[changesIndex] : "New"
            };

            foreach (var (idx, langName) in languageCols)
            {
                if (idx < 0 || idx >= row.Count) continue;
                // Quotes strippen
                string val = row[idx].Trim('\"');
                entry.Languages[langName] = val;
            }

            ctx.LocalizationEntries[key] = entry;
        }
    }

    List<string> ParseCsvLine(string csvLine)
    {
        // Robuuste parser: behoudt lege trailing velden en ondersteunt "" binnen quotes
        var result = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < csvLine.Length; i++)
        {
            char c = csvLine[i];
            if (c == '\"')
            {
                if (inQuotes && i + 1 < csvLine.Length && csvLine[i + 1] == '\"')
                {
                    // Escaped quote
                    current.Append('\"');
                    i++; // skip next quote
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        // Altijd laatste veld pushen (ook als leeg) zodat trailing kolommen blijven bestaan
        result.Add(current.ToString());
        return result;
    }

    // LocalizationModule.cs - updated OnGUIList() function
    public void OnGUIList(Rect rect)
    {
        if (ctx.LocalizationEntries == null)
        {
            GUILayout.Label("No localization available.");
            return;
        }
        // Search bar
        search = EditorGUILayout.TextField(search, (GUIStyle)"SearchTextField");
        if (GUILayout.Button("×", (GUIStyle)"SearchCancelButton", GUILayout.Width(18)))
        {
            search = "";
        }
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ New"))
        {
            string newKey;
            if (EditorPrompt.PromptString("New Key", "Key name:", "NewKey", out newKey))
            {
                if (!ctx.LocalizationEntries.ContainsKey(newKey))
                {
                    ctx.LocalizationEntries[newKey] = new LocalizationEntry
                    {
                        Key = newKey,
                        Source = "",
                        Context = "",
                        Changes = "New",
                        English = "",
                        Dutch = ""
                    };
                    selected = ctx.LocalizationEntries.Keys.ToList().IndexOf(newKey);
                }
                else
                {
                    EditorUtility.DisplayDialog("Already exists", $"Key '{newKey}' already exists.", "OK");
                }
            }
        }
        if (selected >= 0 && selected < filteredKeys.Count && GUILayout.Button("- Delete"))
        {
            string keyToRemove = filteredKeys[selected];
            if (EditorUtility.DisplayDialog("Delete", $"Delete localization '{keyToRemove}'?", "Yes", "No"))
            {
                ctx.LocalizationEntries.Remove(keyToRemove);
                selected = -1;
            }
        }
        EditorGUILayout.EndHorizontal();

        // Filter keys by search term
        var allKeys = ctx.LocalizationEntries.Keys.OrderBy(k => k).ToList();
        filteredKeys = string.IsNullOrEmpty(search)
                           ? allKeys
                           : allKeys.Where(k => k.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        // Keys list
        keysScroll = GUILayout.BeginScrollView(keysScroll);
        int newSel = GUILayout.SelectionGrid(selected, filteredKeys.ToArray(), 1, "OL Box");
        if (newSel != selected) selected = newSel;
        GUILayout.EndScrollView();
    }

    // LocalizationModule.cs - updated OnGUIInspector() function
    public void OnGUIInspector(Rect rect)
    {
        if (selected >= 0 && selected < filteredKeys.Count)
        {
            string key = filteredKeys[selected];
            var entry = ctx.LocalizationEntries[key];

            EditorGUILayout.LabelField("Key", key);
            entry.Source = EditorGUILayout.TextField("Source", entry.Source);
            entry.Context = EditorGUILayout.TextField("Context", entry.Context);
            entry.Changes = EditorGUILayout.TextField("Changes", entry.Changes);

            GUILayout.Space(6);
            EditorGUILayout.LabelField("Translations", EditorStyles.boldLabel);

            var langs = entry.Languages.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            string renameFrom = null, renameTo = null, removeLang = null;

            foreach (var lang in langs)
            {
                EditorGUILayout.BeginHorizontal();
                string newLangName = EditorGUILayout.TextField(lang, GUILayout.Width(160));
                string oldVal = entry.Languages[lang];
                string newVal = EditorGUILayout.TextField(oldVal);
                if (newVal != oldVal) entry.Languages[lang] = newVal;
                if (newLangName != lang && !string.IsNullOrWhiteSpace(newLangName) &&
                    !entry.Languages.ContainsKey(newLangName))
                {
                    renameFrom = lang; renameTo = newLangName;
                }
                if (GUILayout.Button("-", GUILayout.Width(24))) removeLang = lang;
                EditorGUILayout.EndHorizontal();
            }

            if (renameFrom != null)
            {
                var v = entry.Languages[renameFrom];
                entry.Languages.Remove(renameFrom);
                entry.Languages[renameTo] = v;
            }
            if (removeLang != null) entry.Languages.Remove(removeLang);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add language", GUILayout.Width(150)))
            {
                if (EditorPrompt.PromptString("New language", "Language name (e.g. German):", "German", out var lname))
                {
                    if (!entry.Languages.ContainsKey(lname)) entry.Languages[lname] = "";
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            ctx.LocalizationEntries[key] = entry;
        }
        else
        {
            EditorGUILayout.HelpBox("Select a key on the left to edit.", MessageType.Info);
        }
    }


    public void Save()
    {
    }

}

public static class LocalizationIO
{
    // Schrijf alle ctx.LocalizationEntries naar Config/Localization.txt
    // – Header bevat ALLE talen die ergens voorkomen
    // – Missende talen per rij krijgen lege cellen ("")
    // – Bewaart trailing lege kolommen (door expliciet te schrijven)
    public static void Write(ModContext ctx)
    {
        if (ctx?.LocalizationEntries == null) return;

        string locPathPreferred = Path.Combine(ctx.ModConfigPath, "Localization.txt");
        string locPathAltLower = Path.Combine(ctx.ModConfigPath, "localizations.txt"); // compat, indien gewenst

        // Verzamel alle talen
        var allLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ctx.LocalizationEntries.Values)
            foreach (var k in e.Languages.Keys)
                allLangs.Add(k);

        // Volgorde: English, Dutch, daarna alfabetisch de rest
        var orderedLangs = new List<string>();
        if (allLangs.Contains("English")) orderedLangs.Add("English");
        if (allLangs.Contains("Dutch")) orderedLangs.Add("Dutch");
        orderedLangs.AddRange(allLangs
            .Where(l => !l.Equals("English", StringComparison.OrdinalIgnoreCase) &&
                        !l.Equals("Dutch", StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase));

        // Header
        var header = new List<string> { "Key", "Source", "Context", "Changes" };
        header.AddRange(orderedLangs);
        var lines = new List<string> { string.Join(",", header) };

        // Rows
        foreach (var entry in ctx.LocalizationEntries.Values.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            var cells = new List<string>
            {
                Csv(entry.Key),
                Csv(entry.Source),
                Csv(entry.Context),
                Csv(string.IsNullOrEmpty(entry.Changes) ? "New" : entry.Changes)
            };

            foreach (var lang in orderedLangs)
            {
                entry.Languages.TryGetValue(lang, out var txt);
                cells.Add(Csv(txt ?? "")); // altijd cel toevoegen, ook als leeg
            }

            lines.Add(string.Join(",", cells));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(locPathPreferred)!);
        File.WriteAllLines(locPathPreferred, lines);

        // Optioneel: schrijf ook naar 'localizations.txt' als die al bestaat,
        // of als je dat formaat wilt blijven ondersteunen:
        if (File.Exists(locPathAltLower))
            File.WriteAllLines(locPathAltLower, lines);
    }

    // CSV-escape: omring met quotes en escape "" binnen tekst
    static string Csv(string s)
    {
        if (s == null) s = "";
        s = s.Replace("\"", "\"\"");
        return $"\"{s}\"";
    }
}
