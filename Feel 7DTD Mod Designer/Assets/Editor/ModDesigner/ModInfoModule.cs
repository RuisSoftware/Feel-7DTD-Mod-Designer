using UnityEditor;
using UnityEngine;
using System.Xml.Linq;
using System.IO;
using System.Linq;
using System.Collections.Generic;

public class ModInfoModule : IConfigModule
{
    public string ModuleName => "ModInfo";

    ModContext ctx;
    string? filePath;
    XDocument? doc;
    XElement container = null!;
    bool dirty;
    Vector2 scroll;

    public void Initialize(ModContext ctx)
    {
        this.ctx = ctx;
        filePath = null;
        doc = null;
        container = null!;
        dirty = false;

        if (!ctx.HasValidMod) return;

        // --- Version-aware padbepaling ---
        // Prefer: XML/<GameVersion>/ModInfo.xml
        var xmlRoot = Path.Combine(ctx.ModFolder, "XML");
        string? gv = string.IsNullOrEmpty(ctx.SelectedGameVersion) ? null : ctx.SelectedGameVersion;

        if (!string.IsNullOrEmpty(gv))
        {
            string gvRoot = Path.Combine(xmlRoot, gv);
            string gvFile = Path.Combine(gvRoot, "ModInfo.xml");
            EnsureVersionAwareFile(gvFile, xmlRoot, ctx.ModFolder, ctx.ModName, gv, ctx.ModVersion);
            filePath = gvFile;
        }
        else
        {
            // legacy/fallback: XML/ModInfo.xml -> root/ModInfo.xml -> create minimal
            var xmlModInfo = Path.Combine(xmlRoot, "ModInfo.xml");
            var rootModInfo = Path.Combine(ctx.ModFolder, "ModInfo.xml");
            filePath = File.Exists(xmlModInfo) ? xmlModInfo
                     : File.Exists(rootModInfo) ? rootModInfo
                     : xmlModInfo;

            EnsureFileExists(filePath,
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<xml>
  <Name value=""{ctx.ModName}""/>
  <DisplayName value=""{ctx.ModName}""/>
  <Description value="""" />
  <Author value=""You""/>
  <Version value=""{ComposeFullVersionString(ctx.SelectedGameVersion, ctx.ModVersion)}""/>
  <Website value="""" />
</xml>");
        }

        doc = XDocument.Load(filePath!);
        container = doc.Root?.Element("ModInfo") ?? doc.Root!;
        if (container == null)
        {
            // maak een <xml> met velden als er iets heel geks in het bestand staat
            doc = XDocument.Parse("<xml/>");
            container = doc.Root!;
            EnsureField("Name", ctx.ModName);
            EnsureField("DisplayName", ctx.ModName);
            EnsureField("Description", "");
            EnsureField("Author", "You");
            EnsureField("Website", "");
            // Version niet forceren hier; tonen we read-only vanuit manifest
            dirty = true;
        }
    }

    // Probeert te kopiëren vanaf meest nabije bron naar gv-bestand; zo niet, maakt minimale file
    void EnsureVersionAwareFile(string targetGvFile, string xmlRoot, string modRoot, string modName, string gameVersion, string modVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetGvFile) ?? string.Empty);
        if (File.Exists(targetGvFile)) return;

        // Kandidaten: XML/ModInfo.xml, root/ModInfo.xml
        string fromXml = Path.Combine(xmlRoot, "ModInfo.xml");
        string fromRoot = Path.Combine(modRoot, "ModInfo.xml");

        if (File.Exists(fromXml)) { File.Copy(fromXml, targetGvFile, true); return; }
        if (File.Exists(fromRoot)) { File.Copy(fromRoot, targetGvFile, true); return; }

        // Fallback: minimale ModInfo
        File.WriteAllText(targetGvFile,
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<xml>
  <Name value=""{modName}""/>
  <DisplayName value=""{modName}""/>
  <Description value="""" />
  <Author value=""You""/>
  <Version value=""{ComposeFullVersionString(gameVersion, modVersion)}""/>
  <Website value="""" />
</xml>");
    }

    void EnsureFileExists(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
        if (!File.Exists(path))
            File.WriteAllText(path, content);
    }

    public void OnGUIList(Rect rect)
    {
        bool useArea = rect.width > 0 && rect.height > 0;
        if (useArea) GUILayout.BeginArea(rect, EditorStyles.helpBox);
        GUILayout.Label("ModInfo", EditorStyles.boldLabel);
        GUILayout.Label("Edit the mod metadata on the right.", EditorStyles.wordWrappedMiniLabel);
        if (useArea) GUILayout.EndArea();
    }

    public void OnGUIInspector(Rect rect)
    {
        bool useArea = rect.width > 0 && rect.height > 0;
        if (useArea) GUILayout.BeginArea(rect, EditorStyles.helpBox);

        if (!ctx.HasValidMod)
        {
            GUILayout.Label("Select a mod on the left.");
            if (useArea) GUILayout.EndArea();
            return;
        }
        if (container == null)
        {
            GUILayout.Label("ModInfo not found.");
            if (useArea) GUILayout.EndArea();
            return;
        }

        // Pad laten zien (handig met versions)
        EditorGUILayout.LabelField("Active ModInfo path:", filePath ?? "(none)", EditorStyles.miniLabel);
        GUILayout.Space(4);

        // --- Read-only versie info uit manifest ---
        using (new EditorGUI.DisabledGroupScope(true))
        {
            EditorGUILayout.TextField(new GUIContent("Game Version"), ctx.SelectedGameVersion ?? "");
            EditorGUILayout.TextField(new GUIContent("Mod Version (manifest)"), ctx.ModVersion ?? "1.0");
            EditorGUILayout.TextField(new GUIContent("Full Version (export)"),
                ComposeFullVersionString(ctx.SelectedGameVersion, ctx.ModVersion));
        }

        GUILayout.Space(8);
        scroll = GUILayout.BeginScrollView(scroll);

        // Als versie gelocked is, disable editen van overige velden
        bool locked = ctx.IsVersionLocked;
        using (new EditorGUI.DisabledGroupScope(locked))
        {
            DrawValue("Name");
            DrawValue("DisplayName");
            DrawValue("Description");
            DrawValue("Author");
            // GEEN DrawValue("Version") meer; die is read-only hierboven
            DrawValue("Website");

            GUILayout.Space(8);
            GUILayout.Label("Other fields", EditorStyles.boldLabel);

            var reserved = _reserved();
            foreach (var el in container.Elements().ToList())
            {
                var name = el.Name.LocalName;
                if (reserved.Contains(name)) continue;

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(name, GUILayout.Width(140));
                string current = el.Attribute("value")?.Value ?? el.Value;
                string next = EditorGUILayout.TextField(current);
                if (next != current)
                {
                    if (el.Attribute("value") != null) el.SetAttributeValue("value", next);
                    else el.Value = next;
                    dirty = true;
                }
                if (GUILayout.Button("-", GUILayout.Width(24)))
                {
                    if (EditorUtility.DisplayDialog("Remove", $"Remove field '{name}'?", "Yes", "No"))
                    {
                        el.Remove();
                        dirty = true;
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            if (GUILayout.Button("+ Add field"))
            {
                if (EditorPrompt.PromptString("New field", "Name:", "CustomField", out var fieldName))
                {
                    if (_reserved().Contains(fieldName))
                    {
                        EditorUtility.DisplayDialog("Reserved", $"'{fieldName}' is reserved and managed elsewhere.", "OK");
                    }
                    else
                    {
                        var newEl = new XElement(fieldName, new XAttribute("value", ""));
                        container.Add(newEl);
                        dirty = true;
                    }
                }
            }
        }

        GUILayout.Space(8);
        if (GUILayout.Button("Save ModInfo.xml"))
        {
            Save();
        }

        GUILayout.EndScrollView();

        if (locked)
            EditorGUILayout.HelpBox("This game version is LOCKED. Unlock it to edit fields.", MessageType.Info);

        if (useArea) GUILayout.EndArea();
    }

    void DrawValue(string tag)
    {
        var el = container.Element(tag);
        if (el == null)
        {
            el = new XElement(tag, new XAttribute("value", ""));
            container.Add(el);
            dirty = true;
        }
        string current = el.Attribute("value")?.Value ?? el.Value;
        string next = EditorGUILayout.TextField(tag, current);
        if (next != current)
        {
            if (el.Attribute("value") != null)
                el.SetAttributeValue("value", next);
            else
                el.Value = next;
            dirty = true;
        }
    }

    // Zorgt dat een veld bestaat en (indien leeg) een startwaarde krijgt.
    void EnsureField(string tag, string defaultValue)
    {
        if (container == null) return;

        var el = container.Element(tag);
        if (el == null)
        {
            el = new XElement(tag, new XAttribute("value", defaultValue ?? ""));
            container.Add(el);
            dirty = true;
            return;
        }

        // Als er al een element is maar zonder value, zet een default
        var attr = el.Attribute("value");
        if (attr != null)
        {
            if (string.IsNullOrEmpty(attr.Value) && !string.IsNullOrEmpty(defaultValue))
            {
                attr.SetValue(defaultValue);
                dirty = true;
            }
        }
        else
        {
            if (string.IsNullOrEmpty(el.Value) && !string.IsNullOrEmpty(defaultValue))
            {
                el.Value = defaultValue;
                dirty = true;
            }
        }
    }


    public void Save()
    {
        if (!ctx.HasValidMod || doc == null || filePath == null) return;
        if (!dirty) return;
        doc.Save(filePath);
        dirty = false;
        AssetDatabase.Refresh();
    }

    static string ComposeFullVersionString(string gameVersion, string modVersion)
    {
        gameVersion = (gameVersion ?? "").Trim('.');
        modVersion = (modVersion ?? "").Trim('.');
        if (string.IsNullOrEmpty(gameVersion)) return string.IsNullOrEmpty(modVersion) ? "1.0" : modVersion;
        if (string.IsNullOrEmpty(modVersion)) return gameVersion;
        return gameVersion + "." + modVersion;
    }

    static HashSet<string> _reserved() =>
        new HashSet<string> { "Name", "DisplayName", "Description", "Author", "Version", "Website", "ModInfo" };
}
