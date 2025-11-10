// Assets/Editor/EntryXmlModule.cs
using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

public class EntryXmlModule : IConfigModule
{
    public string ModuleName => moduleName;
    public string FileName => fileName;

    // --- config ---
    readonly string moduleName;
    readonly string fileName;   // e.g. "blocks.xml", "items.xml"
    readonly string rootTag;    // e.g. "blocks", "items"
    readonly string entryTag;   // e.g. "block", "item"

    // --- state ---
    ModContext ctx;
    string filePath;
    XDocument doc;
    bool dirty;
    bool localeDirty;

    List<XElement> entries = new();
    int selectedIndex = -1;
    string search = "";
    Vector2 leftScroll, rightScroll;

    // foldout per element
    readonly Dictionary<XElement, bool> foldouts = new();

    // --- patches (set/append/remove) ---
    List<XElement> patchOps = new();
    int selPatch = -1;
    Vector2 patchScroll;

    // EntryXmlModule.cs - add this field in class definition
    private float patchListHeight = 150f;

    private bool resizingPatchList = false;
    private float startMouseY = 0f;
    private float startPatchHeight = 150f;

    private GUIStyle patchLabelStyle;
    private Color patchSelectedColor = new Color(1f, 0.6f, 0.2f, 0.35f);



    public bool HasNoEntries
    {
        get { return entries == null || entries.Count == 0; }
    }

    public EntryXmlModule(string moduleName, string fileName, string entryTag, string rootTagOverride = null)
    {
        this.moduleName = moduleName;
        this.fileName = fileName;
        this.entryTag = entryTag;
        this.rootTag = rootTagOverride ?? Path.GetFileNameWithoutExtension(fileName);
    }

    public void Initialize(ModContext ctx)
    {
        this.ctx = ctx;
        entries.Clear(); selectedIndex = -1; search = ""; dirty = false;
        doc = null; filePath = null; foldouts.Clear();
        patchOps.Clear(); selPatch = -1;

        if (!ctx.HasValidMod) return;

        filePath = Path.Combine(ctx.ModConfigPath, fileName);
        if (!File.Exists(filePath)) return;

        doc = XDocument.Load(filePath);
        RebuildEntries();
        RebuildPatches();
    }

    XElement EnsureAppendToRootPatchElement() // NEW
    {
        if (doc?.Root == null) return null;
        string wanted = "/" + rootTag;
        var app = doc.Root.Elements("append").FirstOrDefault(a => (string)a.Attribute("xpath") == wanted);
        if (app == null)
        {
            app = new XElement("append", new XAttribute("xpath", wanted));
            doc.Root.Add(app);
            dirty = true;
        }
        return app;
    }

    void NormalizeDocumentLayout() // NEW
    {
        if (doc?.Root == null) return;
        var root = doc.Root;

        // Alleen normaliseren bij patch-stijl bestanden (root != rootTag), b.v. <configs> … 
        bool usesPatchLayout = !root.Name.LocalName.Equals(rootTag, StringComparison.OrdinalIgnoreCase);
        if (!usesPatchLayout) return;

        var appendRoot = EnsureAppendToRootPatchElement();
        if (appendRoot == null) return;

        // 1) Verplaats alle entries naar appendRoot
        // 1a. direct onder root
        foreach (var e in root.Elements(entryTag).ToList())
        {
            e.Remove();
            appendRoot.Add(e);
            dirty = true;
        }
        // 1b. entries die per ongeluk in andere append-blokken staan
        foreach (var app in root.Elements("append").Where(a => a != appendRoot).ToList())
        {
            foreach (var e in app.Elements(entryTag).ToList())
            {
                e.Remove();
                appendRoot.Add(e);
                dirty = true;
            }
        }
        // 1c. merge dubbele appends naar dezelfde xpath
        foreach (var dup in root.Elements("append")
                                 .Where(a => a != appendRoot && (string)a.Attribute("xpath") == "/" + rootTag)
                                 .ToList())
        {
            foreach (var child in dup.Elements().ToList())
            {
                child.Remove();
                appendRoot.Add(child);
            }
            dup.Remove();
            dirty = true;
        }

        // 2) Zet patches (remove/set/append != /rootTag) bovenaan, daarna de appendRoot
        var children = root.Elements().ToList();
        var patches = children.Where(e =>
                e.Name.LocalName == "remove" ||
                e.Name.LocalName == "set" ||
                (e.Name.LocalName == "append" && e != appendRoot))
            .ToList();

        // alles even lostrekken
        foreach (var el in root.Elements().ToList()) el.Remove();

        // Opmerking: comments bovenin blijven staan (we herplaatsen alleen elements)
        foreach (var p in patches) root.Add(p);     // originele volgorde behouden
        root.Add(appendRoot);                       // container met alle <block> / <item>
    }

    void EnsureFileExists(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    void RebuildEntries()
    {
        if (doc == null || entryTag == null)
        {
            entries = new List<XElement>();
            selectedIndex = -1;
            return;
        }

        var all = doc.Descendants(entryTag)
                     .Where(e => e.Attribute("name") != null)
                     .OrderBy(e => (string)e.Attribute("name"))
                     .ToList();

        if (string.IsNullOrWhiteSpace(search))
            entries = all;
        else
        {
            var lc = search.ToLowerInvariant();
            entries = all.Where(e => ((string)e.Attribute("name")).ToLowerInvariant().Contains(lc)).ToList();
        }

        if (entries.Count == 0) selectedIndex = -1;
        else if (selectedIndex < 0) selectedIndex = 0;
        else if (selectedIndex >= entries.Count) selectedIndex = entries.Count - 1;
    }

    // ------------------- LIST PANE -------------------
    // EntryXmlModule.cs - updated OnGUIList() function
    public void OnGUIList(Rect rect)
    {

        if (patchLabelStyle == null)
        {
            patchLabelStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                clipping = TextClipping.Clip
            };
        }

        bool useArea = rect.width > 0 && rect.height > 0;
        if (useArea) GUILayout.BeginArea(rect, EditorStyles.helpBox);

        if (!ctx.HasValidMod)
        {
            GUILayout.Label("No mod selected.");
            if (useArea) GUILayout.EndArea();
            return;
        }

        // File path status
        EditorGUILayout.LabelField(
            "File",
            string.IsNullOrEmpty(filePath) ? fileName : filePath,
            EditorStyles.miniLabel
        );

        // Search bar and reload button
        EditorGUILayout.BeginHorizontal();
        search = EditorGUILayout.TextField(search, (GUIStyle)"SearchTextField");
        if (GUILayout.Button("×", (GUIStyle)"SearchCancelButton", GUILayout.Width(18)))
        {
            search = "";
            RebuildEntries();
        }
        if (GUILayout.Button("Reload", GUILayout.Width(70)))
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try { doc = XDocument.Load(filePath); }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("Load failed", ex.Message, "OK");
                    doc = null;
                }
            }
            else
            {
                doc = null;
            }
            RebuildEntries();
            RebuildPatches();
        }
        EditorGUILayout.EndHorizontal();

        // Top buttons: New (with submenu) + Import
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ New"))
        {
            var menu = new GenericMenu();
            if (entryTag != null)
            {
                string entryTypeName = char.ToUpper(entryTag[0]) + entryTag.Substring(1);
                menu.AddItem(new GUIContent($"New {entryTypeName}"), false, () =>
                {
                    if (EditorPrompt.PromptString($"New {entryTag}", "Name:", "newEntry", out var nm))
                    {
                        if (doc == null && !EnsureDocumentCreated()) return;
                        if (doc.Descendants(entryTag).Any(e => (string)e.Attribute("name") == nm))
                        {
                            EditorUtility.DisplayDialog("Already exists", $"{entryTag} '{nm}' already exists.", "OK");
                        }
                        else
                        {
                            var parent = GetDefaultParentForNewEntry();
                            var el = new XElement(entryTag, new XAttribute("name", nm));
                            parent.Add(el);
                            dirty = true;
                            RebuildEntries();
                            selectedIndex = entries.IndexOf(el);
                            selPatch = -1;
                        }
                    }
                });
            }
            menu.AddItem(new GUIContent("Append Patch"), false, AddAppendPatch);
            menu.AddItem(new GUIContent("Set Patch"), false, AddSetPatch);
            menu.AddItem(new GUIContent("Remove Patch"), false, AddRemovePatch);
            menu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero));
        }

        if (entryTag != null && GUILayout.Button("Import"))
        {
            if (doc == null && !EnsureDocumentCreated()) { /* noop */ }
            else EditorApplication.delayCall += ImportFromBaseGame;
        }
        GUI.enabled = selectedIndex >= 0 && selectedIndex < entries.Count;
        if (GUILayout.Button("- Delete"))
        {
            var el = entries.ElementAtOrDefault(selectedIndex);
            string elName = (string?)el?.Attribute("name") ?? "(unnamed)";
            if (el != null && EditorUtility.DisplayDialog("Delete", $"Delete '{elName}'?", "Yes", "No"))
            {
                string nameKey = (string)el.Attribute("name")!;
                XElement descProp = el.Element("property");
                string descKey = descProp != null && (string)descProp.Attribute("name") == "DescriptionKey"
                                    ? (string)descProp.Attribute("value") : null;

                el.Remove();
                dirty = true;
                RebuildEntries();
                selectedIndex = -1;

                ctx.LocalizationEntries.Remove(nameKey);
                if (!string.IsNullOrEmpty(descKey)) ctx.LocalizationEntries.Remove(descKey);
            }
        }
        if (GUILayout.Button("▲")) { MoveEntry(-1); }
        if (GUILayout.Button("▼")) { MoveEntry(+1); }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        // Patch list (top section)
        GUILayout.Space(4);
        GUILayout.Label("Patches (set / append / remove)", EditorStyles.boldLabel);

        RebuildPatches();
        patchScroll = GUILayout.BeginScrollView(patchScroll, GUILayout.Height(patchListHeight));

        float rowHeight = EditorGUIUtility.singleLineHeight * 2.2f;
        for (int i = 0; i < patchOps.Count; i++)
        {
            var patch = patchOps[i];
            string xp = (string?)patch.Attribute("xpath") ?? "(xpath?)";
            string label = $"{patch.Name.LocalName}  {Truncate(xp, 60)}";
            GUIContent content = new GUIContent(label, xp);

            Rect rowRect = GUILayoutUtility.GetRect(content, patchLabelStyle, GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight));
            if (i == selPatch)
                EditorGUI.DrawRect(rowRect, patchSelectedColor);

            Rect toggleRect = new Rect(rowRect.x + 6, rowRect.y + (rowHeight - 16f) / 2, 16, 16);
            bool newSelected = GUI.Toggle(toggleRect, i == selPatch, GUIContent.none, EditorStyles.radioButton);
            if (newSelected && i != selPatch)
            {
                selPatch = i;
                selectedIndex = -1;
                GUI.FocusControl(null);
                EditorWindow.focusedWindow?.Repaint();
            }

            Rect labelRect = new Rect(rowRect.x + 26, rowRect.y + 2, rowRect.width - 30, rowHeight - 4);
            GUI.Label(labelRect, content, patchLabelStyle);
        }

        GUILayout.EndScrollView();


        if (selectedIndex >= 0 && selectedIndex < entries.Count)
        {
            Rect last = GUILayoutUtility.GetLastRect();
            EditorGUI.DrawRect(last, new Color(0f, 0f, 0f, 0.1f));
            GUI.Label(last, new GUIContent("", "Klik op een entry om die te bewerken — deselecteer eerst om patches te activeren"));
        }

        // Draggable splitter for patch list height
        Rect splitterRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(4), GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(new Rect(splitterRect.x, splitterRect.y + splitterRect.height / 2 - 1, splitterRect.width, 2), new Color(1f, 0.5f, 0f, 0.6f));
        EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeVertical);
        // Splitter gedrag (patchListHeight drag)
        if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition))
        {
            resizingPatchList = true;
            startPatchHeight = patchListHeight;
            startMouseY = GUIUtility.GUIToScreenPoint(Event.current.mousePosition).y;
            Event.current.Use();
        }

        if (resizingPatchList && Event.current.type == EventType.MouseDrag)
        {
            float currentMouseY = GUIUtility.GUIToScreenPoint(Event.current.mousePosition).y;
            float delta = currentMouseY - startMouseY;
            patchListHeight = Mathf.Clamp(startPatchHeight + delta, 60f, 500f);
            EditorWindow.focusedWindow?.Repaint();

            // alleen Use() als muis boven de splitter zit
            if (splitterRect.Contains(Event.current.mousePosition))
                Event.current.Use();
        }

        if (resizingPatchList && Event.current.type == EventType.MouseUp)
        {
            resizingPatchList = false;
            Event.current.Use();
        }

        // Entry list (bottom section)
        GUILayout.Space(6);
        GUILayout.Label(entryTag != null ? $"{entryTag}s" : "Entries", EditorStyles.boldLabel);

        if (doc == null)
        {
            EditorGUILayout.HelpBox(
                $"{fileName} does not exist in this mod yet.\nClick '+ New' (or 'Import') to create the file now.",
                MessageType.Info
            );
        }

        leftScroll = GUILayout.BeginScrollView(leftScroll, GUILayout.ExpandHeight(true));
        Color accentColor = new Color(1f, 0.6f, 0.2f);
        for (int i = 0; i < entries.Count; i++)
        {
            string entryName = entries[i].Attribute("name")?.Value ?? "(unnamed)";
            GUI.backgroundColor = (i == selectedIndex ? accentColor : Color.white);
            if (GUILayout.Button(entryName, "OL Box", GUILayout.ExpandWidth(true)))
            {
                selectedIndex = i;
                selPatch = -1;
                EditorWindow.focusedWindow?.Repaint();
            }
            GUI.backgroundColor = Color.white;
        }
        GUILayout.EndScrollView();

        if (entries.Count == 0)
        {
            string shown = !string.IsNullOrEmpty(filePath) ? Path.GetFileName(filePath) : fileName;
            EditorGUILayout.HelpBox(
                $"No <{entryTag}> entries found in {shown}.\nThis file may only contain patches or is empty.",
                MessageType.Info
            );
        }

        if (Event.current.type == EventType.MouseUp || Event.current.type == EventType.MouseDown || GUI.changed)
            EditorWindow.focusedWindow?.Repaint();

        if (useArea) GUILayout.EndArea();
    }


    void MoveEntry(int delta)
    {
        if (selectedIndex < 0 || selectedIndex >= entries.Count) return;
        var el = entries[selectedIndex];
        if (el == null) return;

        var siblings = el.Parent.Elements(el.Name).ToList(); // siblings within the same parent
        int idx = siblings.IndexOf(el);
        int newIdx = Mathf.Clamp(idx + delta, 0, siblings.Count - 1);
        if (newIdx == idx) return;

        el.Remove();
        if (newIdx >= siblings.Count) el.Parent.Add(el);
        else siblings[newIdx].AddBeforeSelf(el);

        dirty = true;
        RebuildEntries();
        selectedIndex = entries.IndexOf(el);
    }

    // ------------------- INSPECTOR PANE -------------------
    // EntryXmlModule.cs - updated OnGUIInspector() function
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

        // If an entry is selected, ensure patch selection is cleared
        if (selectedIndex >= 0 && selectedIndex < entries.Count)
            selPatch = -1;

        // Show patch inspector if a patch is selected
        if (selPatch >= 0 && selPatch < patchOps.Count)
        {
            rightScroll = GUILayout.BeginScrollView(rightScroll);
            var el = patchOps[selPatch];
            EditorGUILayout.LabelField($"Patch: <{el.Name.LocalName}>", EditorStyles.boldLabel);

            string raw = el.ToString(SaveOptions.None);
            var xmlStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            // Use monospaced font for XML display if available
            Font codeFont = Font.CreateDynamicFontFromOSFont(new string[] { "Consolas", "Courier New", "Lucida Console", "Menlo", "Monaco" }, 12);
            if (codeFont) xmlStyle.font = codeFont;
            xmlStyle.richText = false;
            string changed = EditorGUILayout.TextArea(raw, xmlStyle, GUILayout.ExpandHeight(true), GUILayout.MinHeight(240));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply", GUILayout.Width(90)))
            {
                try
                {
                    var repl = XElement.Parse(changed);
                    el.ReplaceWith(repl);
                    dirty = true;
                    RebuildPatches();
                    selPatch = patchOps.IndexOf(repl);
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("XML error", ex.Message, "OK");
                }
            }
            if (GUILayout.Button("Remove", GUILayout.Width(90)))
            {
                el.Remove();
                dirty = true;
                RebuildPatches();
                selPatch = Mathf.Clamp(selPatch, 0, patchOps.Count - 1);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button($"Save {fileName}", GUILayout.Width(160))) Save();
            EditorGUILayout.EndHorizontal();

            GUILayout.EndScrollView();
            if (useArea) GUILayout.EndArea();
            return; // Do not show entry UI when a patch is selected
        }

        // Entry inspector
        if (selectedIndex < 0 || selectedIndex >= entries.Count)
        {
            GUILayout.Label($"Select a {entryTag} or a patch from the list on the left…");
            if (useArea) GUILayout.EndArea();
            return;
        }

        var elEntry = entries[selectedIndex];
        rightScroll = GUILayout.BeginScrollView(rightScroll);

        // Name field and reorder buttons
        EditorGUILayout.BeginHorizontal();
        string nm = elEntry.Attribute("name")?.Value ?? "";
        string newName = EditorGUILayout.TextField($"{entryTag} name", nm);
        if (newName != nm && !string.IsNullOrWhiteSpace(newName))
        {
            // Update name and localization keys
            elEntry.SetAttributeValue("name", newName);
            if (ctx.LocalizationEntries.TryGetValue(nm, out var nameEntry))
            {
                ctx.LocalizationEntries.Remove(nm);
                nameEntry.Key = newName;
                ctx.LocalizationEntries[newName] = nameEntry;
            }
            var descProp = elEntry.Elements("property").FirstOrDefault(p => (string)p.Attribute("name") == "DescriptionKey");
            if (descProp != null)
            {
                string oldDescKey = (string)descProp.Attribute("value");
                string expectedOldKey = nm + "Desc";
                if (oldDescKey == expectedOldKey)
                {
                    string newDescKey = newName + "Desc";
                    descProp.SetAttributeValue("value", newDescKey);
                    dirty = true;
                    if (ctx.LocalizationEntries.TryGetValue(oldDescKey, out var descEntry))
                    {
                        ctx.LocalizationEntries.Remove(oldDescKey);
                        descEntry.Key = newDescKey;
                        ctx.LocalizationEntries[newDescKey] = descEntry;
                    }
                }
            }
            dirty = true;
            RebuildEntries();
            selectedIndex = entries.FindIndex(e => (string?)e.Attribute("name") == newName);
        }

        // --- Icon preview & acties ---
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("📸 Generate/Update CustomIcon from prefab", GUILayout.Height(24)))
        {
            MakeCustomIconFromModel(elEntry);
        }
        if (GUILayout.Button("📂 Open Icons Folder", GUILayout.Width(130)))
        {
            string atlasDir = Path.Combine(ctx.ModFolder, "XML", "UIAtlases", "ItemIconAtlas");
            Directory.CreateDirectory(atlasDir);
            EditorUtility.RevealInFinder(atlasDir);
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.BeginVertical("box");
        Texture2D preview;
        string iconOrigin;
        bool hasIcon = TryResolveIconForEntry(elEntry, out preview, out iconOrigin);

        if (hasIcon && preview != null)
        {
            EditorGUILayout.LabelField($"Icon Preview  ({iconOrigin})");
            GUILayout.Label(preview, GUILayout.Width(80), GUILayout.Height(80));
        }
        else
        {
            EditorGUILayout.LabelField("Icon Preview");
            EditorGUILayout.HelpBox(
                "Geen icon gevonden. Maak een CustomIcon via 📸 of plaats een icon in je mod atlas (XML/UIAtlases/ItemIconAtlas), "
                + "of gebruik een base-game icon in Data/ItemIcons.", MessageType.Info);
        }

        // Toon/maak eventueel de CustomIcon property om snel te kunnen zetten
        var iconProp = elEntry.Elements("property").FirstOrDefault(p => (string?)p.Attribute("name") == "CustomIcon");
        string curIconName = iconProp?.Attribute("value")?.Value ?? "";
        string newIconName = EditorGUILayout.TextField("CustomIcon", curIconName);
        if (newIconName != curIconName)
        {
            if (iconProp == null)
            {
                iconProp = new XElement("property",
                    new XAttribute("name", "CustomIcon"),
                    new XAttribute("value", newIconName));
                elEntry.Add(iconProp);
            }
            else
            {
                iconProp.SetAttributeValue("value", newIconName);
            }
            dirty = true;
        }
        GUILayout.EndVertical();

        //if (GUILayout.Button("▲", GUILayout.Width(28))) MoveEntry(-1);
        //if (GUILayout.Button("▼", GUILayout.Width(28))) MoveEntry(+1);
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(6);

        // Localization section (only for blocks/items)
        if ((fileName.Equals("blocks.xml", StringComparison.OrdinalIgnoreCase) ||
             fileName.Equals("items.xml", StringComparison.OrdinalIgnoreCase)) &&
            ctx.LocalizationEntries != null)
        {
            DrawLocalizationForEntry(elEntry);
        }

        EditorGUILayout.LabelField("Properties", EditorStyles.boldLabel);

        // Direct properties
        var props = elEntry.Elements("property").Where(p => p.Attribute("class") == null).ToList();
        DrawPropertyList(props, elEntry);

        GUILayout.Space(8);
        // Property groups
        var groups = elEntry.Elements("property").Where(p => p.Attribute("class") != null).ToList();
        if (groups.Count > 0)
            GUILayout.Label("Property Groups", EditorStyles.boldLabel);
        foreach (var grp in groups.ToList())
        {
            if (!foldouts.ContainsKey(grp)) foldouts[grp] = true;
            string className = grp.Attribute("class")?.Value ?? "";
            string title = $"class=\"{className}\"";
            foldouts[grp] = EditorGUILayout.Foldout(foldouts[grp], title, true);
            if (foldouts[grp])
            {
                EditorGUI.indentLevel++;
                string newCls = EditorGUILayout.TextField("class", className);
                if (newCls != className)
                {
                    grp.SetAttributeValue("class", newCls);
                    dirty = true;
                }

                var innerProps = grp.Elements("property").ToList();
                DrawPropertyList(innerProps, grp);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("+ Add property to group"))
                {
                    grp.Add(new XElement("property",
                        new XAttribute("name", "NewProperty"),
                        new XAttribute("value", "")));
                    dirty = true;
                }
                if (GUILayout.Button("- Remove group"))
                {
                    if (EditorUtility.DisplayDialog("Remove", $"Remove group {title}?", "Yes", "No"))
                    {
                        grp.Remove();
                        dirty = true;
                    }
                }
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }
            GUILayout.Space(4);
        }

        // Other child elements
        var others = elEntry.Elements().Where(x => x.Name != "property").ToList();
        if (others.Count > 0)
        {
            GUILayout.Space(8);
            EditorGUILayout.LabelField("Other child elements (advanced)", EditorStyles.boldLabel);
            foreach (var o in others.ToList())
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"<{o.Name.LocalName}>", GUILayout.Width(150));
                if (GUILayout.Button("Edit XML"))
                {
                    string raw = o.ToString(SaveOptions.DisableFormatting);
                    if (EditorPrompt.PromptString($"Edit <{o.Name.LocalName}>", "Raw XML:", raw, out var changed))
                    {
                        try
                        {
                            var repl = XElement.Parse(changed);
                            o.ReplaceWith(repl);
                            dirty = true;
                        }
                        catch (Exception ex)
                        {
                            EditorUtility.DisplayDialog("XML error", ex.Message, "OK");
                        }
                    }
                }
                if (GUILayout.Button("▲", GUILayout.Width(28))) { MoveSibling(o, -1); }
                if (GUILayout.Button("▼", GUILayout.Width(28))) { MoveSibling(o, +1); }
                if (GUILayout.Button("- Remove", GUILayout.Width(80)))
                {
                    o.Remove();
                    dirty = true;
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        // Add element buttons
        GUILayout.Space(8);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ Add property"))
        {
            elEntry.Add(new XElement("property", new XAttribute("name", "NewProperty"), new XAttribute("value", "")));
            dirty = true;
        }
        if (GUILayout.Button("+ Add property group"))
        {
            elEntry.Add(new XElement("property", new XAttribute("class", "Action0")));
            dirty = true;
        }
        if (GUILayout.Button("+ Add other element"))
        {
            if (EditorPrompt.PromptString("New element", "Tag name:", "drop", out var tag))
            {
                elEntry.Add(new XElement(tag));
                dirty = true;
            }
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(10);
        if (GUILayout.Button($"Save {fileName}")) Save();

        GUILayout.EndScrollView();
        if (useArea) GUILayout.EndArea();
    }



    void DrawPropertyList(List<XElement> list, XElement parent)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();

            string nameAttr = p.Attribute("name")?.Value ?? "";
            string valueAttr = p.Attribute("value")?.Value ?? "";

            string newName = EditorGUILayout.TextField(nameAttr, GUILayout.MinWidth(120));
            string newValue = EditorGUILayout.TextField(valueAttr);

            if (newName != nameAttr) { p.SetAttributeValue("name", newName); dirty = true; }
            if (newValue != valueAttr) { p.SetAttributeValue("value", newValue); dirty = true; }

            if (GUILayout.Button("▲", GUILayout.Width(28))) { MoveSibling(p, -1); }
            if (GUILayout.Button("▼", GUILayout.Width(28))) { MoveSibling(p, +1); }

            if ((string)p.Attribute("name") == "Model" && ctx?.ModFolder != null)
            {
                if (GUILayout.Button("👁", GUILayout.Width(28)))
                {
                    string modelVal = p.Attribute("value")?.Value;
                    var match = System.Text.RegularExpressions.Regex.Match(modelVal ?? "", @"\?(.+)$");
                    if (match.Success)
                    {
                        string prefabName = match.Groups[1].Value;
                        string prefabsFolder = Path.Combine(ctx.ModFolder, "Prefabs");
                        if (Directory.Exists(prefabsFolder))
                        {
                            string[] matches = Directory.GetFiles(prefabsFolder, prefabName + ".prefab", SearchOption.AllDirectories);
                            if (matches.Length > 0)
                            {
                                string assetPath = ModDesignerWindow.SystemPathToAssetPath(matches[0]);
                                var prefab = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                                if (prefab != null) { Selection.activeObject = prefab; EditorGUIUtility.PingObject(prefab); }
                                else EditorUtility.DisplayDialog("Prefab niet geladen", $"Prefab '{prefabName}' kon niet worden geladen.", "OK");
                            }
                            else EditorUtility.DisplayDialog("Niet gevonden", $"Prefab '{prefabName}.prefab' niet gevonden in:\n{prefabsFolder}", "OK");
                        }
                    }
                }
                if (GUILayout.Button("📸", GUILayout.Width(28))) { MakeCustomIconFromModel(parent); }
            }

            bool remove = GUILayout.Button("-", GUILayout.Width(28));
            EditorGUILayout.EndHorizontal();

            if (remove)
            {
                p.Remove();
                dirty = true;
                EditorGUILayout.EndVertical(); // <- sluit de box ook bij verwijderen
                i--; // index corrigeren na remove
                continue;
            }

            // overige attributen (param1 etc.)
            foreach (var attr in p.Attributes().Where(a => a.Name != "name" && a.Name != "value").ToList())
            {
                string newVal = EditorGUILayout.TextField(attr.Name.LocalName, attr.Value);
                if (newVal != attr.Value) { p.SetAttributeValue(attr.Name, newVal); dirty = true; }
            }

            EditorGUILayout.EndVertical();
        }
    }

    void MoveSibling(XElement el, int delta)
    {
        var siblings = el.Parent.Elements(el.Name).ToList();
        int idx = siblings.IndexOf(el);
        int newIdx = Mathf.Clamp(idx + delta, 0, siblings.Count - 1);
        if (newIdx == idx) return;
        el.Remove();
        if (newIdx >= siblings.Count) el.Parent.Add(el);
        else siblings[newIdx].AddBeforeSelf(el);
        dirty = true;
    }

    public void Save()
    {
        if (!ctx.HasValidMod) return;

        bool wroteAnything = false;

        // CHANGED: altijd eerst de documentstructuur normaliseren
        if (doc != null) NormalizeDocumentLayout();

        // XML opslaan als aangepast
        if (doc != null && filePath != null && dirty)
        {
            doc.Save(filePath);
            wroteAnything = true;
            dirty = false;
        }

        bool shouldWriteLoc = (localeDirty ||
            fileName.Equals("blocks.xml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("items.xml", StringComparison.OrdinalIgnoreCase))
            && ctx.LocalizationEntries != null
            && ctx.LocalizationEntries.Count > 0;

        if (shouldWriteLoc)
        {
            LocalizationIO.Write(ctx);
            wroteAnything = true;
            localeDirty = false;
        }

        if (wroteAnything)
            AssetDatabase.Refresh();
    }

    // EntryXmlModule.cs - updated AddRemovePatch() function
    void AddRemovePatch()
    {
        if (doc?.Root == null) return;
        if (!EditorPrompt.PromptString("New <remove> patch", "XPath:", "/blocks/block[@name='solarbank']/drop[@event='Harvest']", out var xp)) return;
        var el = new XElement("remove", new XAttribute("xpath", xp));
        doc.Root.AddFirst(el);
        dirty = true;
        RebuildPatches();
        selPatch = patchOps.IndexOf(el);
    }


    // EntryXmlModule.cs - updated AddSetPatch() function
    void AddSetPatch()
    {
        if (doc?.Root == null) return;
        if (!EditorPrompt.PromptString("New <set> patch", "XPath (e.g. .../@value):", "/blocks/block[@name='solarbank']/property[@name='MaxPower']/@value", out var xp)) return;
        if (!EditorPrompt.PromptString("New <set> patch", "Value:", "216", out var val)) return;
        var el = new XElement("set", new XAttribute("xpath", xp)) { Value = val ?? "" };
        doc.Root.AddFirst(el);
        dirty = true;
        RebuildPatches();
        selPatch = patchOps.IndexOf(el);
    }


    // EntryXmlModule.cs - updated AddAppendPatch() function
    void AddAppendPatch()
    {
        if (doc?.Root == null) return;
        string def = "/" + rootTag;
        if (!EditorPrompt.PromptString("New <append> patch", "XPath:", def, out var xp)) return;
        var el = new XElement("append", new XAttribute("xpath", xp));
        if (!string.Equals(xp, def, StringComparison.OrdinalIgnoreCase))
            el.Add(new XComment(" Add child nodes here "));
        doc.Root.AddFirst(el);
        dirty = true;
        // If this is the root append container, normalize layout and refresh entries
        if (string.Equals(xp, def, StringComparison.OrdinalIgnoreCase))
        {
            NormalizeDocumentLayout();
            RebuildEntries();
            RebuildPatches();
            selPatch = -1;
        }
        else
        {
            RebuildPatches();
            selPatch = patchOps.IndexOf(el);
        }
    }


    // ------------------- PATCH EDITOR -------------------
    void RebuildPatches()
    {
        if (doc == null)
        {
            patchOps = new List<XElement>();
            selPatch = -1;
            return;
        }

        string containerXpath = "/" + rootTag;

        // Behoud originele XML volgorde, filter alleen de append-container naar rootTag
        patchOps = doc.Root.Descendants()
            .Where(e =>
                e.Name == "set" ||
                e.Name == "remove" ||
                (e.Name == "append" &&
                 !string.Equals((string)e.Attribute("xpath"), containerXpath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Behoud huidige selectie indien mogelijk
        selPatch = patchOps.Count > 0 ? Mathf.Clamp(selPatch, 0, patchOps.Count - 1) : -1;
    }


    string MakePatchLabel(XElement e)
    {
        string xpath = (string?)e.Attribute("xpath") ?? "(xpath?)";
        if (e.Name == "set") return $"set     {Truncate(xpath, 60)}";
        if (e.Name == "append") return $"append  {Truncate(xpath, 46)}  [{e.Elements().FirstOrDefault()?.Name.LocalName ?? ""}]";
        if (e.Name == "remove") return $"remove  {Truncate(xpath, 60)}";
        return e.Name.LocalName;
    }

    // EntryXmlModule.cs - updated OnGUIPatches() function (if used separately for patch editing)
    public void OnGUIPatches(Rect rect)
    {
        bool useArea = rect.width > 0 && rect.height > 0;
        if (useArea) GUILayout.BeginArea(rect, EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Patches (set / append / remove)", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();

        if (GUILayout.Button("+ remove", GUILayout.Width(90))) AddRemovePatch();
        if (GUILayout.Button("+ set", GUILayout.Width(70))) AddSetPatch();
        if (GUILayout.Button("+ append", GUILayout.Width(90))) AddAppendPatch();

        if (GUILayout.Button("Normalize layout", GUILayout.Width(140)))
        {
            NormalizeDocumentLayout();
            RebuildEntries();
            RebuildPatches();
        }

        if (GUILayout.Button("Reload", GUILayout.Width(70)))
        {
            doc = XDocument.Load(filePath);
            RebuildPatches();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        // Left: patch list
        EditorGUILayout.BeginVertical(GUILayout.Width(360), GUILayout.ExpandHeight(true));
        patchScroll = GUILayout.BeginScrollView(patchScroll, GUILayout.ExpandHeight(true));
        var patchGUI = patchOps.Select(e => new GUIContent(MakePatchLabel(e), (string?)e.Attribute("xpath") ?? "")).ToArray();
        int newSel = GUILayout.SelectionGrid(selPatch, patchGUI, 1, "OL Box");
        if (newSel != selPatch) selPatch = newSel;
        GUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        GUILayout.Space(6);

        // Right: raw XML editor for selected patch
        EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        if (selPatch >= 0 && selPatch < patchOps.Count)
        {
            var el = patchOps[selPatch];
            string raw = el.ToString(SaveOptions.None);
            var xmlStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            Font codeFont = Font.CreateDynamicFontFromOSFont(new string[] { "Consolas", "Courier New", "Lucida Console", "Menlo", "Monaco" }, 12);
            if (codeFont) xmlStyle.font = codeFont;
            xmlStyle.richText = false;

            EditorGUILayout.LabelField($"<{el.Name.LocalName}>", EditorStyles.boldLabel);
            string changed = EditorGUILayout.TextArea(raw, xmlStyle, GUILayout.ExpandHeight(true), GUILayout.MinHeight(240));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply"))
            {
                try
                {
                    var repl = XElement.Parse(changed);
                    el.ReplaceWith(repl);
                    dirty = true;
                    RebuildPatches();
                    selPatch = patchOps.IndexOf(repl);
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("XML error", ex.Message, "OK");
                }
            }
            if (GUILayout.Button("Remove", GUILayout.Width(80)))
            {
                el.Remove();
                dirty = true;
                RebuildPatches();
                selPatch = Mathf.Clamp(selPatch, 0, patchOps.Count - 1);
            }
            EditorGUILayout.EndHorizontal();
        }
        else
        {
            EditorGUILayout.HelpBox("Select a patch on the left.", MessageType.Info);
        }
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();

        if (useArea) GUILayout.EndArea();
    }


    string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
        return s.Substring(0, maxLen) + "…";
    }

    // ------------------- IMPORTER -------------------
    // EntryXmlModule.cs - updated ImportFromBaseGame() function
    void ImportFromBaseGame()
    {
        if (ctx == null || string.IsNullOrEmpty(ctx.GameConfigPath))
        {
            EditorUtility.DisplayDialog("Game Config", "Please set the Game Config Folder first.", "OK");
            return;
        }
        var basePath = Path.Combine(ctx.GameConfigPath, fileName);
        if (!File.Exists(basePath))
        {
            EditorUtility.DisplayDialog("File not found", $"Could not find {fileName} at:\n{basePath}", "OK");
            return;
        }

        var baseDoc = XDocument.Load(basePath);
        if (entryTag == null)
        {
            EditorUtility.DisplayDialog("Not available", $"Cannot import entries for {fileName}.", "OK");
            return;
        }
        var baseEntries = baseDoc.Descendants(entryTag)
                                 .Where(e => e.Attribute("name") != null)
                                 .ToList();
        if (baseEntries.Count == 0)
        {
            EditorUtility.DisplayDialog("No entries", $"No <{entryTag}> entries found in {fileName}.", "OK");
            return;
        }

        var names = baseEntries.Select(e => (string)e.Attribute("name")).OrderBy(s => s).ToArray();
        int chosen = SimpleListPicker.Show("Import from base game", $"Choose a {entryTag} to copy", names);
        if (chosen < 0) return;
        string pick = names[chosen];
        var source = baseEntries.First(e => (string)e.Attribute("name") == pick);

        if (doc.Descendants(entryTag).Any(e => (string)e.Attribute("name") == pick))
        {
            if (!EditorUtility.DisplayDialog("Already exists",
                    $"{entryTag} '{pick}' already exists in this mod.\nAdd anyway?", "Yes", "No"))
            {
                return;
            }
        }

        var parent = GetDefaultParentForNewEntry();
        parent.Add(new XElement(source));
        dirty = true;
        RebuildEntries();
        selectedIndex = entries.FindIndex(e => (string?)e.Attribute("name") == pick);
    }


    // Simple modal list picker for import selection
    public class SimpleListPicker : EditorWindow
    {
        int _sel = -1;
        string[] _options = Array.Empty<string>();
        string _titleText = "";
        string _subtitle = "";
        Vector2 _scroll;
        Action<int>? _onClose;

        public static int Show(string title, string subtitle, string[] options)
        {
            int result = -1;
            var win = CreateInstance<SimpleListPicker>();
            win._titleText = title;
            win._subtitle = subtitle;
            win._options = options;
            win._onClose = i => result = i;
            // Center the window
            win.position = new Rect(Screen.width / 2f, Screen.height / 2f, 380, 420);
            win.ShowModalUtility();
            return result;
        }

        void OnGUI()
        {
            GUILayout.Label(_titleText, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(_subtitle))
                GUILayout.Label(_subtitle, EditorStyles.wordWrappedMiniLabel);
            GUILayout.Space(4);

            _scroll = GUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _options.Length; i++)
            {
                if (GUILayout.Toggle(_sel == i, _options[i], "Button"))
                    _sel = i;
            }
            GUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel"))
            {
                _onClose?.Invoke(-1);
                Close();
            }
            GUI.enabled = _sel >= 0;
            if (GUILayout.Button("OK"))
            {
                _onClose?.Invoke(_sel);
                Close();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }
    }

    GameObject FindPrefabFromModel(XElement entry, out string prefabName)
    {
        prefabName = null;

        // Zoek <property name="Model" value="...?...PrefabName">
        var modelProp = entry.Elements("property").FirstOrDefault(p => (string)p.Attribute("name") == "Model");
        var modelVal = modelProp?.Attribute("value")?.Value;
        if (!string.IsNullOrEmpty(modelVal))
        {
            var m = Regex.Match(modelVal, @"\?(.+)$"); // alles na '?'
            if (m.Success)
            {
                prefabName = m.Groups[1].Value;
                string prefabsFolder = Path.Combine(ctx.ModFolder, "Prefabs");
                if (Directory.Exists(prefabsFolder))
                {
                    string[] matches = Directory.GetFiles(prefabsFolder, prefabName + ".prefab", SearchOption.AllDirectories);
                    if (matches.Length > 0)
                    {
                        string assetPath = ModDesignerWindow.SystemPathToAssetPath(matches[0]);
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                        if (prefab != null) return prefab;
                    }
                }
            }
        }
        return null;
    }

    // EntryXmlModule.cs - updated MakeCustomIconFromModel() function
    void MakeCustomIconFromModel(XElement entry)
    {
        if (ctx?.ModFolder == null)
        {
            EditorUtility.DisplayDialog("No mod", "No valid mod is selected.", "OK");
            return;
        }

        // 1) Find prefab based on Model property, or ask user to pick one
        string prefabName;
        GameObject prefab = FindPrefabFromModel(entry, out prefabName);
        if (prefab == null)
        {
            if (!EditorUtility.DisplayDialog("Prefab not found",
                "No prefab was found via the Model property.\nChoose a prefab manually?", "Choose...", "Cancel"))
                return;
            string picked = EditorUtility.OpenFilePanel("Select prefab", Path.Combine(ctx.ModFolder, "Prefabs"), "prefab");
            if (string.IsNullOrEmpty(picked)) return;
            string assetPath = ModDesignerWindow.SystemPathToAssetPath(picked);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Load failed", "Could not load prefab.", "OK");
                return;
            }
            prefabName = Path.GetFileNameWithoutExtension(picked);
        }

        // 2) Determine icon file name (default = entry name)
        string defaultIconName = entry.Attribute("name")?.Value ?? prefabName ?? prefab.name;
        if (!EditorPrompt.PromptString("Icon file name", "Name (without .png):", defaultIconName, out string iconName))
            return;

        // 3) Determine output folder for icon
        string atlasDir = Path.Combine(ctx.ModFolder, "XML", "UIAtlases", "ItemIconAtlas");
        Directory.CreateDirectory(atlasDir);
        string outPng = Path.Combine(atlasDir, iconName + ".png");

        // 4) Generate screenshot
        bool ok = ScreenshotPrefabs.TryMakePrefabIcon(prefab, outPng, 512);
        if (!ok)
        {
            EditorUtility.DisplayDialog("Failed", "Screenshot could not be taken.", "OK");
            return;
        }

        // 5) Set or create CustomIcon property
        var iconProp = entry.Elements("property").FirstOrDefault(p => (string)p.Attribute("name") == "CustomIcon");
        if (iconProp == null)
        {
            iconProp = new XElement("property", new XAttribute("name", "CustomIcon"), new XAttribute("value", iconName));
            entry.Add(iconProp);
        }
        else
        {
            iconProp.SetAttributeValue("value", iconName);
        }

        dirty = true;
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Success",
            $"Icon saved to:\n{outPng}\n\nCustomIcon has been set to '{iconName}'.", "OK");
    }


    // ========= EntryXmlModule: Localization UI helpers =========

    void EnsureDefaultLanguages(LocalizationEntry le)
    {
        if (!le.Languages.ContainsKey("English")) le.Languages["English"] = "";
        if (!le.Languages.ContainsKey("Dutch")) le.Languages["Dutch"] = "";
    }

    LocalizationEntry GetOrCreateLoc(string key)
    {
        if (ctx.LocalizationEntries.TryGetValue(key, out var le) && le != null)
            return le;

        // Alleen voor NIEUWE entries defaults zetten
        le = new LocalizationEntry
        {
            Key = key,
            Source = (entryTag == "block" ? "blocks" : "items"),
            Context = "",
            Changes = "New"
        };
        EnsureDefaultLanguages(le); // hier ok: alleen bij net nieuw
        ctx.LocalizationEntries[key] = le;
        return le;
    }

    bool DrawTranslationsUIForKey(string key, string title)
    {
        if (string.IsNullOrEmpty(key)) return false;

        var le = GetOrCreateLoc(key);
        bool changed = false;

        GUILayout.BeginVertical("box");
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

        // Stabiele volgorde van talen
        var langKeys = le.Languages.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var lang in langKeys)
        {
            EditorGUILayout.BeginHorizontal();

            // Taalnaam bewerkbaar
            string newLang = EditorGUILayout.TextField(lang, GUILayout.Width(140));
            string oldVal = le.Languages[lang];
            string newVal = EditorGUILayout.TextField(oldVal);

            if (newVal != oldVal) { le.Languages[lang] = newVal; changed = true; }

            // Hernoem taal-key als de labelnaam is aangepast
            if (!string.Equals(newLang, lang, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(newLang) &&
                !le.Languages.ContainsKey(newLang))
            {
                le.Languages.Remove(lang);
                le.Languages[newLang] = oldVal;
                changed = true;
            }

            if (GUILayout.Button("-", GUILayout.Width(24)))
            {
                le.Languages.Remove(lang);
                changed = true;
                EditorGUILayout.EndHorizontal();
                continue;
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ Add language", GUILayout.Width(150)))
        {
            if (EditorPrompt.PromptString("Nieuwe taal", "Taalnaam (bv. English, Dutch, German):", "German", out var lname))
            {
                if (!le.Languages.ContainsKey(lname)) { le.Languages[lname] = ""; changed = true; }
            }
        }
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        GUILayout.EndVertical();

        if (changed) { dirty = true; localeDirty = true; } // markeer ook localeDirty
        return changed;
    }

    void DrawNameTranslations(XElement entry)
    {
        string internalName = entry.Attribute("name")?.Value ?? "";
        if (string.IsNullOrEmpty(internalName)) return;

        DrawTranslationsUIForKey(internalName, "Name translations");
    }

    void DrawDescriptionTranslations(XElement entry)
    {
        string internalName = entry.Attribute("name")?.Value ?? "";
        if (string.IsNullOrEmpty(internalName)) return;

        var descProp = entry.Elements("property").FirstOrDefault(p => (string)p.Attribute("name") == "DescriptionKey");
        string descKey = descProp?.Attribute("value")?.Value;

        // Nog geen DescriptionKey -> toon hint + knop
        if (string.IsNullOrEmpty(descKey))
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label("Geen DescriptionKey gevonden.");
            if (GUILayout.Button("+ Maak DescriptionKey", GUILayout.Width(180)))
            {
                descKey = internalName + "Desc";
                entry.Add(new XElement("property",
                    new XAttribute("name", "DescriptionKey"),
                    new XAttribute("value", descKey)));
                dirty = true;
            }
            EditorGUILayout.EndHorizontal();
        }

        if (!string.IsNullOrEmpty(descKey))
        {
            DrawTranslationsUIForKey(descKey, $"Description translations  ({descKey})");
        }
    }

    void DrawLocalizationForEntry(XElement entry)
    {
        GUILayout.Space(6);
        EditorGUILayout.LabelField("Localization", EditorStyles.boldLabel);
        DrawNameTranslations(entry);
        GUILayout.Space(4);
        DrawDescriptionTranslations(entry);
    }

    // EntryXmlModule.cs - updated EnsureDocumentCreated() function
    bool EnsureDocumentCreated()
    {
        if (doc != null) return true;
        if (ctx == null || !ctx.HasValidMod) return false;
        if (string.IsNullOrEmpty(filePath))
            filePath = Path.Combine(ctx.ModConfigPath, fileName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? "");
            var xdoc = new XDocument(new XElement(rootTag));
            xdoc.Save(filePath);
            doc = xdoc;
            RebuildEntries();
            RebuildPatches();
            dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("Creation failed",
                $"Could not create {fileName}:\n{ex.Message}", "OK");
            return false;
        }
    }
    XElement GetDefaultParentForNewEntry()
    {
        if (doc?.Root == null) return null;
        if (doc.Root.Name.LocalName == rootTag)
            return doc.Root;

        var app = doc.Root.Elements("append")
                          .FirstOrDefault(e => string.Equals((string)e.Attribute("xpath"), "/" + rootTag, StringComparison.OrdinalIgnoreCase));
        if (app != null)
            return app;

        // fallback
        return doc.Root;
    }

    // === Icon helpers ===
    static readonly string[] _iconExts = new[] { ".png", ".jpg", ".jpeg" };

    string? GetCustomIconNameFrom(XElement e)
    {
        return e.Elements("property")
                .FirstOrDefault(p => (string?)p.Attribute("name") == "CustomIcon")
                ?.Attribute("value")?.Value;
    }

    string? GetExtendsTarget(XElement entry)
    {
        // 7DTD gebruikt meestal het attribuut 'extends'; fallback property desnoods
        return (string?)entry.Attribute("extends")
            ?? entry.Elements("property").FirstOrDefault(p => (string?)p.Attribute("name") == "Extends")
                  ?.Attribute("value")?.Value;
    }

    bool TryFindIconInModAtlas(string iconName, out Texture2D tex, out string usedPath)
    {
        tex = null; usedPath = "";
        if (ctx?.ModFolder == null) return false;

        string atlasDir = Path.Combine(ctx.ModFolder, "XML", "UIAtlases", "ItemIconAtlas");
        if (!Directory.Exists(atlasDir)) return false;

        foreach (var f in Directory.EnumerateFiles(atlasDir))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (!_iconExts.Contains(ext)) continue;

            if (string.Equals(Path.GetFileNameWithoutExtension(f), iconName, StringComparison.OrdinalIgnoreCase))
            {
                string assetPath = ModDesignerWindow.SystemPathToAssetPath(f);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var t = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (t != null)
                    {
                        tex = t; usedPath = f;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    Texture2D LoadTextureAbsolute(string file)
    {
        var data = File.ReadAllBytes(file);
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        t.LoadImage(data);
        t.name = Path.GetFileNameWithoutExtension(file);
        t.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        return t;
    }

    bool TryFindIconInDefaultItemIcons(string iconName, out Texture2D tex, out string usedPath)
    {
        tex = null; usedPath = "";

        // 1) Afleiden vanaf GameConfigPath → .../Data/ItemIcons
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(ctx?.GameConfigPath))
        {
            var dataDir = Directory.GetParent(ctx.GameConfigPath);
            if (dataDir != null)
                dirs.Add(Path.Combine(dataDir.FullName, "ItemIcons"));
        }

        // 2) Hardcoded Steam-locaties als fallback
        dirs.Add(@"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die\Data\ItemIcons");
        dirs.Add(@"C:\Program Files\Steam\steamapps\common\7 Days To Die\Data\ItemIcons");

        foreach (var dir in dirs.Distinct().Where(Directory.Exists))
        {
            foreach (var ext in _iconExts)
            {
                string p = Path.Combine(dir, iconName + ext);
                if (File.Exists(p))
                {
                    tex = LoadTextureAbsolute(p);
                    usedPath = p;
                    return true;
                }
            }
        }
        return false;
    }

    XElement? FindEntryByName(string name, XDocument docLike)
    {
        return docLike?.Descendants(entryTag)
                       .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
    }

    bool TryResolveIconForEntry(XElement entry, out Texture2D tex, out string origin)
    {
        tex = null; origin = "";

        // A) eigen CustomIcon
        string? iconName = GetCustomIconNameFrom(entry);
        if (!string.IsNullOrEmpty(iconName))
        {
            if (TryFindIconInModAtlas(iconName, out tex, out var p1))
            {
                origin = $"CustomIcon '{iconName}' (mod atlas)";
                return true;
            }
            // Soms heet het icoon hetzelfde in default ItemIcons
            if (TryFindIconInDefaultItemIcons(iconName, out tex, out var p2))
            {
                origin = $"CustomIcon '{iconName}' (default ItemIcons)";
                return true;
            }
        }

        // B) geërfde CustomIcon via extends-keten (max 8 stappen)
        string? extTarget = GetExtendsTarget(entry);
        XDocument? baseDoc = null;
        int guard = 0;

        while (!string.IsNullOrEmpty(extTarget) && guard++ < 8)
        {
            // 1) Zoek in huidig mod-document
            var parent = FindEntryByName(extTarget, doc);

            // 2) Zo niet, dan in base game config
            if (parent == null && !string.IsNullOrEmpty(ctx?.GameConfigPath))
            {
                string basePath = Path.Combine(ctx.GameConfigPath, fileName);
                if (File.Exists(basePath))
                {
                    baseDoc ??= XDocument.Load(basePath);
                    parent = FindEntryByName(extTarget, baseDoc);
                }
            }

            if (parent == null) break;

            var parentIcon = GetCustomIconNameFrom(parent);
            if (!string.IsNullOrEmpty(parentIcon))
            {
                if (TryFindIconInModAtlas(parentIcon, out tex, out var p3))
                {
                    origin = $"Inherited '{parentIcon}' from '{extTarget}' (mod atlas)";
                    return true;
                }
                if (TryFindIconInDefaultItemIcons(parentIcon, out tex, out var p4))
                {
                    origin = $"Inherited '{parentIcon}' from '{extTarget}' (default ItemIcons)";
                    return true;
                }
            }

            // volgende schakel opzoeken
            extTarget = GetExtendsTarget(parent);
        }

        // C) default game icon op naam proberen (entry name en evt. iconName)
        var candidates = new List<string>();
        var selfName = entry.Attribute("name")?.Value;
        if (!string.IsNullOrEmpty(selfName)) candidates.Add(selfName);
        if (!string.IsNullOrEmpty(iconName)) candidates.Add(iconName);
        if (!string.IsNullOrEmpty(extTarget)) candidates.Add(extTarget);

        foreach (var n in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryFindIconInDefaultItemIcons(n, out tex, out var p5))
            {
                origin = $"Default game icon '{n}'";
                return true;
            }
        }

        return false;
    }



}
