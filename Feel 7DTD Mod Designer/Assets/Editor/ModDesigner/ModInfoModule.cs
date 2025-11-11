// Assets/Editor/ModInfoModule.cs
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
    XElement modInfo = null!;  // Will point to the <ModInfo> element or root <xml>
    bool dirty;
    Vector2 scroll;
    XElement container = null!;

    public void Initialize(ModContext ctx)
    {
        this.ctx = ctx;
        filePath = null;
        doc = null;
        container = null;
        dirty = false;
        if (!ctx.HasValidMod) return;

        // Determine ModInfo.xml location (either in /XML or root)
        var xmlFolder = Path.Combine(ctx.ModFolder, "XML");
        var xmlModInfo = Path.Combine(xmlFolder, "ModInfo.xml");
        var rootModInfo = Path.Combine(ctx.ModFolder, "ModInfo.xml");

        if (File.Exists(xmlModInfo)) filePath = xmlModInfo;
        else if (File.Exists(rootModInfo)) filePath = rootModInfo;
        else filePath = xmlModInfo; // default to create in /XML

        EnsureFileExists(filePath,
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<xml>
  <Name value=""New Mod""/>
  <DisplayName value=""New Mod""/>
  <Description value="""" />
  <Author value=""You""/>
  <Version value=""1.0.0""/>
  <Website value="""" />
</xml>");

        doc = XDocument.Load(filePath);

        // 'container' is the element containing mod info fields (either <ModInfo> or root <xml>)
        container = doc.Root?.Element("ModInfo") ?? doc.Root!;
        modInfo = container;
    }

    void EnsureFileExists(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
        if (!File.Exists(path))
            File.WriteAllText(path, content);
    }

    // ModInfoModule.cs - updated OnGUIList() function
    public void OnGUIList(Rect rect)
    {
        bool useArea = rect.width > 0 && rect.height > 0;
        if (useArea) GUILayout.BeginArea(rect, EditorStyles.helpBox);
        GUILayout.Label("ModInfo", EditorStyles.boldLabel);
        GUILayout.Label("Edit the mod metadata on the right.", EditorStyles.wordWrappedMiniLabel);
        if (useArea) GUILayout.EndArea();
    }


    // ModInfoModule.cs - updated OnGUIInspector() function
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
        if (modInfo == null)
        {
            GUILayout.Label("ModInfo not found.");
            if (useArea) GUILayout.EndArea();
            return;
        }

        scroll = GUILayout.BeginScrollView(scroll);
        // Standard fields
        DrawValue("Name");
        DrawValue("Description");
        DrawValue("Author");
        DrawValue("Version");
        DrawValue("Website");

        GUILayout.Space(8);
        GUILayout.Label("Other fields", EditorStyles.boldLabel);
        foreach (var el in modInfo.Elements().ToList())
        {
            if (new HashSet<string> { "Name", "Description", "Author", "Version", "Website" }.Contains(el.Name.LocalName))
                continue;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(el.Name.LocalName, GUILayout.Width(140));
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
                if (EditorUtility.DisplayDialog("Remove", $"Remove field '{el.Name.LocalName}'?", "Yes", "No"))
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
                var newEl = new XElement(fieldName, new XAttribute("value", ""));
                modInfo.Add(newEl);
                dirty = true;
            }
        }

        GUILayout.Space(8);
        if (GUILayout.Button("Save ModInfo.xml"))
        {
            Save();
        }
        GUILayout.EndScrollView();
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

    public void Save()
    {
        if (!ctx.HasValidMod || doc == null || filePath == null) return;
        if (!dirty) return;
        doc.Save(filePath);
        dirty = false;
        AssetDatabase.Refresh();
    }
}
