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
    XElement selectedPatchEl = null;                 // NEW: stabiele referentie naar geselecteerde patch
    HashSet<XElement> multiPatchSel = new();         // NEW: multi-select set
    Vector2 patchScroll;

    // EntryXmlModule.cs - add this field in class definition
    private float patchListHeight = 150f;

    private bool resizingPatchList = false;
    private float startMouseY = 0f;
    private float startPatchHeight = 150f;

    private GUIStyle patchLabelStyle;
    private Color patchSelectedColor = new Color(1f, 0.6f, 0.2f, 0.35f);
    // === Static cached font & style voor XML-editor ===
    static readonly string[] kCodeFontCandidates = { "Consolas", "Courier New", "Lucida Console", "Menlo", "Monaco" };
    static Font s_CodeFont;
    static GUIStyle s_XmlTextAreaStyle;
    static bool s_FontHooked;

    // EntryXmlModule.cs (class-scope)
    XElement _patchEditorBoundEl = null;
    string _patchEditorText = "";


    HashSet<string> _knownNames = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _knownItems = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _knownBlocks = new(StringComparer.OrdinalIgnoreCase);
    string[] _knownNamesArr = Array.Empty<string>();

    // Batch icon settings
    int batchIconSize = 512;
    float batchYaw = 45f;
    float batchPitch = 25f;
    bool batchOverwrite = true;             // bestaande PNG's overschrijven
    bool batchOnlyWithCustomIcon = true;    // per request: alleen entries met CustomIcon
                                            
    // Hardcoded defaults (voor reset-knop)
    const int DEFAULT_ICON_SIZE = 512;
    const float DEFAULT_YAW = 45f;
    const float DEFAULT_PITCH = 25f;

    // === Icon cache (reduceert disk-I/O en repaints) ===
    static readonly System.Collections.Generic.Dictionary<string, Texture2D> _iconTexCache =
        new(System.StringComparer.OrdinalIgnoreCase);
    static readonly System.Collections.Generic.Dictionary<string, string> _iconOriginCache =
        new(System.StringComparer.OrdinalIgnoreCase);
    static readonly System.Collections.Generic.HashSet<string> _iconMissingCache =
        new(System.StringComparer.OrdinalIgnoreCase);

    XDocument _baseGameDoc = null;
    string _baseGameDocPath = null;
    Dictionary<string, XElement> _baseGameIndex = null;

    void ClearIconCache(string name = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            _iconTexCache.Clear();
            _iconOriginCache.Clear();
            _iconMissingCache.Clear();
        }
        else
        {
            _iconTexCache.Remove(name);
            _iconOriginCache.Remove(name);
            _iconMissingCache.Remove(name);
        }
    }

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

    static void EnsureCodeFont()
    {
        if (s_CodeFont == null)
        {
            try
            {
                s_CodeFont = Font.CreateDynamicFontFromOSFont(kCodeFontCandidates, 12);
                if (s_CodeFont != null) s_CodeFont.hideFlags = HideFlags.DontSave;
            }
            catch { /* laat fallback op default font */ }
        }
        if (!s_FontHooked)
        {
            Font.textureRebuilt += OnFontRebuilt;
            s_FontHooked = true;
        }
    }

    static void OnFontRebuilt(Font f)
    {
        // Zorg dat het veld opnieuw getekend wordt zodra de atlas is herbouwd
        EditorApplication.delayCall += () => EditorWindow.focusedWindow?.Repaint();
    }

    GUIStyle GetXmlTextAreaStyle()
    {
        if (s_XmlTextAreaStyle != null) return s_XmlTextAreaStyle;

        EnsureCodeFont();

        var st = new GUIStyle(EditorStyles.textArea)
        {
            wordWrap = true,
            richText = false,
            font = s_CodeFont ?? EditorStyles.textArea.font,
            alignment = TextAnchor.UpperLeft
        };

        // Forceer zichtbare tekstkleur in alle skins (Pro/Personal) en states
        Color baseCol = EditorStyles.label.normal.textColor;
        st.normal.textColor = baseCol;
        st.active.textColor = baseCol;
        st.focused.textColor = baseCol;
        st.hover.textColor = baseCol;
        st.onNormal.textColor = baseCol;
        st.onActive.textColor = baseCol;
        st.onFocused.textColor = baseCol;
        st.onHover.textColor = baseCol;

        s_XmlTextAreaStyle = st;
        return s_XmlTextAreaStyle;
    }


    public void Initialize(ModContext ctx)
    {
        this.ctx = ctx;
        entries.Clear(); selectedIndex = -1; search = ""; dirty = false;
        doc = null; filePath = null; foldouts.Clear();
        patchOps.Clear(); selPatch = -1;

        InvalidateBaseGameCaches(); // <-- NIEUW

        if (!ctx.HasValidMod) return;

        filePath = Path.Combine(ctx.ModConfigPath, fileName);
        if (!File.Exists(filePath)) return;

        doc = XDocument.Load(filePath);
        RebuildEntries();
        RebuildPatches();
        BuildValidNameIndex();
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
        string prevName = (selectedIndex >= 0 && selectedIndex < entries.Count)
            ? (string)entries[selectedIndex].Attribute("name")
            : null;

        var all = GetEntriesInDocumentOrder();
        entries = string.IsNullOrWhiteSpace(search)
            ? all.ToList()
            : all.Where(e => ((string)e.Attribute("name")).ToLowerInvariant().Contains(search.ToLowerInvariant())).ToList();

        if (entries.Count == 0) { selectedIndex = -1; return; }

        if (!string.IsNullOrEmpty(prevName))
        {
            int byName = entries.FindIndex(e => string.Equals((string)e.Attribute("name"), prevName, StringComparison.OrdinalIgnoreCase));
            if (byName >= 0) { selectedIndex = byName; return; }
        }
        selectedIndex = Mathf.Clamp(selectedIndex, 0, entries.Count - 1);
    }


    IEnumerable<XElement> GetEntriesInDocumentOrder()
    {
        if (doc?.Root == null || entryTag == null)
            return Enumerable.Empty<XElement>();

        // 1) Directe root-layout: <blocks> / <items>
        if (doc.Root.Name.LocalName.Equals(rootTag, StringComparison.OrdinalIgnoreCase))
            return doc.Root.Elements(entryTag).Where(e => e.Attribute("name") != null);

        // 2) Patch-layout: pak de append-container met xpath="/<rootTag>"
        var container = doc.Root.Elements("append")
            .FirstOrDefault(a => string.Equals((string)a.Attribute("xpath"), "/" + rootTag, StringComparison.OrdinalIgnoreCase));

        if (container != null)
            return container.Elements(entryTag).Where(e => e.Attribute("name") != null);

        // 3) Fallback (zou zelden nodig moeten zijn)
        return doc.Descendants(entryTag).Where(e => e.Attribute("name") != null);
    }


    // ------------------- LIST PANE -------------------
    // EntryXmlModule.cs - updated OnGUIList() function
    // --- REPLACE COMPLETELY ---
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
            InvalidateBaseGameCaches(); // <-- NIEUW
            RebuildEntries();
            RebuildPatches();
            BuildValidNameIndex();
        }
        EditorGUILayout.EndHorizontal();

        // Top buttons: New (with submenu) + Import + Delete + Move
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
                            selectedPatchEl = null;
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

        // ---- PATCH LIST (clickable rows + multi-select) ----
        GUILayout.Space(4);
        GUILayout.Label("Patches (set / append / remove)", EditorStyles.boldLabel);

        RebuildPatches();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Normalize layout", GUILayout.Width(140)))
        {
            NormalizeDocumentLayout();
            RebuildEntries();
            RebuildPatches();
        }
        if (GUILayout.Button("Reload", GUILayout.Width(70)))
        {
            doc = XDocument.Load(filePath);
            InvalidateBaseGameCaches(); // <-- NIEUW
            RebuildPatches();
        }
        GUILayout.FlexibleSpace();
        GUI.enabled = multiPatchSel.Count > 0;
        if (GUILayout.Button("▲ Move", GUILayout.Width(80))) MoveSelectedPatches(-1);
        if (GUILayout.Button("▼ Move", GUILayout.Width(80))) MoveSelectedPatches(+1);
        if (GUILayout.Button("Delete", GUILayout.Width(80))) DeleteSelectedPatches();
        GUI.enabled = true;
        if (GUILayout.Button("Select all", GUILayout.Width(90)))
            multiPatchSel = new HashSet<XElement>(patchOps);
        if (GUILayout.Button("None", GUILayout.Width(70)))
            multiPatchSel.Clear();
        EditorGUILayout.EndHorizontal();

        patchScroll = GUILayout.BeginScrollView(patchScroll, GUILayout.Height(patchListHeight));

        float rowHeight = EditorGUIUtility.singleLineHeight * 2.2f;
        for (int i = 0; i < patchOps.Count; i++)
        {
            var patch = patchOps[i];
            string xp = (string?)patch.Attribute("xpath") ?? "(xpath?)";
            int childCount = (patch.Name == "append") ? patch.Elements().Count() : 0;

            string label = patch.Name.LocalName switch
            {
                "set" => $"set     {Truncate(xp, 70)}",
                "remove" => $"remove  {Truncate(xp, 70)}",
                "append" => $"append  {Truncate(xp, 54)}  {(childCount > 0 ? $"[{childCount} child{(childCount == 1 ? "" : "ren")}]" : "")}",
                _ => $"{patch.Name.LocalName}  {Truncate(xp, 70)}"
            };

            Rect rowRect = GUILayoutUtility.GetRect(new GUIContent(label), patchLabelStyle,
                                                    GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight));

            bool isMainSelected = (selectedPatchEl == patch);
            bool isMultiSelected = multiPatchSel.Contains(patch);

            if (isMainSelected)
                EditorGUI.DrawRect(rowRect, new Color(1f, 0.6f, 0.2f, 0.35f)); // main select
            else if (isMultiSelected)
                EditorGUI.DrawRect(rowRect, new Color(0.2f, 0.7f, 1f, 0.20f));  // multi select

            // Multi-select checkbox
            Rect checkRect = new Rect(rowRect.x + 6, rowRect.y + (rowHeight - 16f) / 2, 16, 16);
            bool before = isMultiSelected;
            bool after = GUI.Toggle(checkRect, before, GUIContent.none);
            if (after != before)
            {
                if (after) multiPatchSel.Add(patch);
                else multiPatchSel.Remove(patch);
                GUI.changed = true;
            }

            // Clickable label (whole row)
            Rect labelRect = new Rect(rowRect.x + 28, rowRect.y + 2, rowRect.width - 32, rowHeight - 4);
            GUI.Label(labelRect, new GUIContent(label, xp), patchLabelStyle);

            if (Event.current.type == EventType.MouseDown &&
                rowRect.Contains(Event.current.mousePosition) &&
                !checkRect.Contains(Event.current.mousePosition))
            {
                // <<< BELANGRIJK: deselecteer entry zodat patch-editor zichtbaar wordt >>>
                selectedIndex = -1;
                selectedPatchEl = patch;
                selPatch = i;
                GUI.FocusControl(null);
                EditorWindow.focusedWindow?.Repaint();
                Event.current.Use();
            }
        }

        GUILayout.EndScrollView();

        // Splitter to resize patch list
        Rect splitterRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(4), GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(new Rect(splitterRect.x, splitterRect.y + splitterRect.height / 2 - 1, splitterRect.width, 2), new Color(1f, 0.5f, 0f, 0.6f));
        EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeVertical);
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
            Event.current.Use();
        }
        if (resizingPatchList && Event.current.type == EventType.MouseUp)
        {
            resizingPatchList = false;
            Event.current.Use();
        }

        if (selectedIndex >= 0 && selectedIndex < entries.Count)
        {
            Rect last = GUILayoutUtility.GetLastRect();
            EditorGUI.DrawRect(last, new Color(0f, 0f, 0f, 0.1f));
            GUI.Label(last, new GUIContent("", "Klik op een entry om die te bewerken — deselecteer eerst om patches te activeren"));
        }

        // ---- ENTRY LIST ----
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
                selectedPatchEl = null;
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
        if (el?.Parent == null) return;

        var siblings = el.Parent.Elements(el.Name).ToList();
        if (siblings.Count <= 1) return;

        int idx = siblings.IndexOf(el);
        int newIdx = Mathf.Clamp(idx + delta, 0, siblings.Count - 1);
        if (newIdx == idx) return;

        var anchor = siblings[newIdx]; // referentie blijft geldig na Remove()
        el.Remove();

        if (newIdx < idx)  // omhoog
            anchor.AddBeforeSelf(el);
        else               // omlaag
            anchor.AddAfterSelf(el);

        dirty = true;

        RebuildEntries();
        selectedIndex = entries.IndexOf(el);
        EditorWindow.focusedWindow?.Repaint();
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

        // ---- PATCH INSPECTOR FIRST (heeft voorrang boven entry UI) ----
        // herstel referentie als de node door normaliseren/saven is vervangen
        if (selectedPatchEl != null && !patchOps.Contains(selectedPatchEl) && selPatch >= 0 && selPatch < patchOps.Count)
            selectedPatchEl = patchOps[selPatch];

        if (selectedPatchEl != null && patchOps.Contains(selectedPatchEl))
        {
            rightScroll = GUILayout.BeginScrollView(rightScroll);
            var el = selectedPatchEl;

            EditorGUILayout.LabelField($"Patch: <{el.Name.LocalName}>", EditorStyles.boldLabel);

            string raw = el.ToString(SaveOptions.None);
            // Bind buffer als selectie wisselt
            if (_patchEditorBoundEl != selectedPatchEl)
            {
                _patchEditorBoundEl = selectedPatchEl;
                _patchEditorText = selectedPatchEl.ToString(SaveOptions.None);
            }

            var xmlStyle = GetXmlTextAreaStyle();

            // veilige kleuren reset (zoals je al deed)
            var prevCol = GUI.color; var prevCC = GUI.contentColor; var prevBG = GUI.backgroundColor;
            GUI.color = GUI.contentColor = GUI.backgroundColor = Color.white;

            // gebruik de buffer i.p.v. el.ToString()
            string _newText = EditorGUILayout.TextArea(_patchEditorText, xmlStyle, GUILayout.ExpandHeight(true), GUILayout.MinHeight(240));
            if (!ReferenceEquals(_newText, _patchEditorText)) _patchEditorText = _newText;

            GUI.color = prevCol; GUI.contentColor = prevCC; GUI.backgroundColor = prevBG;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply", GUILayout.Width(90)))
            {
                try
                {
                    var repl = XElement.Parse(_patchEditorText);
                    selectedPatchEl.ReplaceWith(repl);
                    dirty = true;
                    RebuildPatches();
                    selectedPatchEl = repl;
                    selPatch = patchOps.IndexOf(repl);
                    _patchEditorBoundEl = repl;
                    _patchEditorText = repl.ToString(SaveOptions.None);
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("XML error", ex.Message, "OK");
                }
            }
            if (GUILayout.Button("Remove", GUILayout.Width(90)))
            {
                selectedPatchEl.Remove();
                dirty = true;
                RebuildPatches();
                selectedPatchEl = null;
                selPatch = -1;
                _patchEditorBoundEl = null;
                _patchEditorText = "";
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button($"Save {fileName}", GUILayout.Width(160))) Save();
            EditorGUILayout.EndHorizontal();


            GUILayout.EndScrollView();
            if (useArea) GUILayout.EndArea();
            return; // niets anders tonen wanneer een patch geselecteerd is
        }

        // ---- ENTRY INSPECTOR ----
        if (selectedIndex < 0 || selectedIndex >= entries.Count)
        {
            GUILayout.Label($"Select a {entryTag} or a patch from the list on the left…");
            if (useArea) GUILayout.EndArea();
            return;
        }

        var elEntry = entries[selectedIndex];
        rightScroll = GUILayout.BeginScrollView(rightScroll);

        GUILayout.BeginVertical("box");
        // Block Name
        GUILayout.Label("Base Settings", EditorStyles.boldLabel);
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

        // Icon preview
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

        GUILayout.Space(6);

        // Name field and reorder buttons
        EditorGUILayout.BeginHorizontal();

        // --- Batch: generate icons voor meerdere entries ---
        GUILayout.BeginVertical("box");
        GUILayout.Label("Icon generation", EditorStyles.boldLabel);

        batchIconSize = EditorGUILayout.IntSlider("Icon size (px)", batchIconSize, 64, 1024);
        EditorGUILayout.BeginHorizontal();
        batchYaw = EditorGUILayout.FloatField("Yaw (°)", batchYaw);
        batchPitch = EditorGUILayout.FloatField("Pitch (°)", batchPitch);

        // ↺ Reset-knop naar de vooraf gecodeerde defaults
        if (GUILayout.Button("↺ Reset to defaults (512 / 45° / 25°)", GUILayout.Height(22)))
        {
            batchIconSize = DEFAULT_ICON_SIZE;
            batchYaw = DEFAULT_YAW;
            batchPitch = DEFAULT_PITCH;
            EditorWindow.focusedWindow?.Repaint();
        }
        EditorGUILayout.EndHorizontal();

        using (new EditorGUI.DisabledGroupScope(doc == null || ctx?.ModFolder == null))
        {
            // --- Icon preview & acties ---
            if (GUILayout.Button("📂 Open Icons Folder", GUILayout.Width(130)))
            {
                string atlasDir = Path.Combine(ctx.ModFolder, "XML", "UIAtlases", "ItemIconAtlas");
                Directory.CreateDirectory(atlasDir);
                EditorUtility.RevealInFinder(atlasDir);
            }
            if (GUILayout.Button("📸 Generate/Update CustomIcon for this prefab", GUILayout.Height(24)))
            {
                MakeCustomIconFromModel(elEntry);
            }

            GUILayout.Label("Batch generation", EditorStyles.boldLabel);
            batchOverwrite = EditorGUILayout.ToggleLeft("Overwrite existing PNGs", batchOverwrite);
            batchOnlyWithCustomIcon = EditorGUILayout.ToggleLeft("Only entries with CustomIcon", batchOnlyWithCustomIcon);
            if (GUILayout.Button("🖼 Batch generate icons for this mod", GUILayout.Height(26)))
            {
                GenerateIconsForEntries(batchIconSize, batchYaw, batchPitch, batchOverwrite, batchOnlyWithCustomIcon);
            }
        }
        GUILayout.EndVertical();

        //if (GUILayout.Button("▲", GUILayout.Width(28))) MoveEntry(-1);
        //if (GUILayout.Button("▼", GUILayout.Width(28))) MoveEntry(+1);
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(6);

        GUILayout.BeginVertical("box");

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

        GUILayout.EndVertical();

        GUILayout.Space(6);

        GUILayout.BeginVertical("box");

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

        GUILayout.EndVertical();

        GUILayout.Space(10);
        if (GUILayout.Button($"Save {fileName}")) Save();

        GUILayout.EndScrollView();
        if (useArea) GUILayout.EndArea();
    }

    void GenerateIconsForEntries(int size, float yawDeg, float pitchDeg, bool overwrite, bool onlyWithCustomIcon)
    {
        if (doc == null || ctx?.ModFolder == null) return;

        string atlasDir = Path.Combine(ctx.ModFolder, "XML", "UIAtlases", "ItemIconAtlas");
        Directory.CreateDirectory(atlasDir);

        // Verzamel targets
        var all = doc.Descendants(entryTag).Where(e => e.Attribute("name") != null).ToList();
        var targets = new List<XElement>();
        foreach (var e in all)
        {
            string iconName = GetCustomIconNameFrom(e) ?? "";
            if (onlyWithCustomIcon)
            {
                if (!string.IsNullOrWhiteSpace(iconName)) targets.Add(e);
            }
            else
            {
                targets.Add(e);
            }
        }

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("Batch icons", onlyWithCustomIcon
                ? "Geen entries met CustomIcon gevonden."
                : "Geen entries gevonden.", "OK");
            return;
        }

        int done = 0, skipped = 0, failed = 0;
        var sbLog = new System.Text.StringBuilder();

        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var entry = targets[i];
                string entryName = entry.Attribute("name")?.Value ?? "(unnamed)";

                EditorUtility.DisplayProgressBar("Generating icons", $"{entryName}  ({i + 1}/{targets.Count})", (float)(i + 1) / targets.Count);

                // Prefab vinden via Model-property
                string prefabName;
                var prefab = FindPrefabFromModel(entry, out prefabName);
                if (prefab == null)
                {
                    failed++;
                    sbLog.AppendLine($"[MISS PREFAB] {entryName} (Model niet gevonden)");
                    continue;
                }

                // Iconnaam bepalen
                string iconName = GetCustomIconNameFrom(entry);
                if (string.IsNullOrWhiteSpace(iconName))
                {
                    // Alleen vullen als we niet beperken op 'alleen met CustomIcon'
                    if (onlyWithCustomIcon)
                    {
                        skipped++;
                        sbLog.AppendLine($"[SKIP NO ICON] {entryName} (geen CustomIcon)");
                        continue;
                    }
                    iconName = entryName; // fallback
                                          // Schrijf 'm weg zodat UI consistent blijft
                    entry.Add(new XElement("property", new XAttribute("name", "CustomIcon"), new XAttribute("value", iconName)));
                    dirty = true;
                }

                string outPng = Path.Combine(atlasDir, iconName + ".png");
                if (!overwrite && File.Exists(outPng))
                {
                    skipped++;
                    sbLog.AppendLine($"[SKIP EXISTS] {entryName} -> {iconName}.png");
                    continue;
                }

                bool ok = ScreenshotPrefabs.TryMakePrefabIcon(prefab, outPng, size, yawDeg, pitchDeg);
                if (ok)
                {
                    done++;
                    sbLog.AppendLine($"[OK] {entryName} -> {iconName}.png");
                }
                else
                {
                    failed++;
                    sbLog.AppendLine($"[FAIL] {entryName} -> {iconName}.png");
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        // Eventueel localization/icon-props wegschrijven
        Save();
        ClearIconCache();

        string summary =
            $"Total: {targets.Count}\n" +
            $"Generated: {done}\n" +
            $"Skipped: {skipped}\n" +
            $"Failed: {failed}\n\n" +
            $"Icons folder:\n{atlasDir}\n\n" +
            $"Details:\n{sbLog}";

        // Toon compacte samenvatting; volledige log in console
        Debug.Log(summary);
        EditorUtility.DisplayDialog("Batch icons complete", $"Generated: {done}\nSkipped: {skipped}\nFailed: {failed}", "OK");

        // Refresh UI
        AssetDatabase.Refresh();
        EditorWindow.focusedWindow?.Repaint();
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

            // -- linkerveld: property-naam
            string newName = EditorGUILayout.TextField(nameAttr, GUILayout.MinWidth(120));

            // Bepaal of dit een tint-property is (gebruik de actuele invoernaam)
            bool isTintProp =
                string.Equals(newName, "CustomIconTint", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(newName, "TintColor", StringComparison.OrdinalIgnoreCase);

            // -- waardev feld: met gekleurde tekst indien hex kleur
            string newValue;
            if (isTintProp && TryParseHtmlColor(valueAttr, out var tintCol))
            {
                // kleine kleur-swatch vóór de tekstfield (optioneel, handig in 1 oogopslag)
                var swRect = GUILayoutUtility.GetRect(18, 18, GUILayout.Width(18), GUILayout.Height(18));
                EditorGUI.DrawRect(swRect, tintCol);

                // gekleurde textfield
                var tintedStyle = MakeTintedTextFieldStyle(tintCol);
                newValue = EditorGUILayout.TextField(valueAttr, tintedStyle);
            }
            else
            {
                newValue = EditorGUILayout.TextField(valueAttr);
            }

            // schrijf wijzigingen terug
            if (newName != nameAttr) { p.SetAttributeValue("name", newName); dirty = true; }
            if (newValue != valueAttr) { p.SetAttributeValue("value", newValue); dirty = true; }
            if (string.Equals(newName, "CanPickup", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(newValue, "true", StringComparison.OrdinalIgnoreCase) &&
                p.Attribute("param1") == null)
            {
                p.SetAttributeValue("param1", "");
                dirty = true;
            }

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
                            if (matches.Length == 0)
                                matches = Directory.GetFiles(prefabsFolder, "*" + prefabName + "*.prefab", SearchOption.AllDirectories);

                            if (matches.Length > 0)
                            {
                                string assetPath = ModDesignerWindow.SystemPathToAssetPath(matches[0]);
                                if (!string.IsNullOrEmpty(assetPath))
                                {
                                    var prefab = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                                    if (prefab != null) { Selection.activeObject = prefab; EditorGUIUtility.PingObject(prefab); }
                                    else EditorUtility.DisplayDialog("Prefab niet geladen", $"Prefab '{prefabName}' kon niet worden geladen.", "OK");
                                }
                                else
                                {
                                    // Buiten Assets: toon uitleg en open de map
                                    EditorUtility.DisplayDialog(
                                        "Prefab buiten Assets",
                                        $"De prefab staat buiten de Unity-projectmap:\n{matches[0]}\n\n" +
                                        "Unity kan deze niet als Asset openen. Verplaats de prefab naar 'Assets/' of verwijs " +
                                        "naar een prefab die wel in het project staat.",
                                        "OK");
                                    EditorUtility.RevealInFinder(matches[0]);
                                }
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
                bool isCanPickupParam1 =
                    string.Equals(newName, "CanPickup", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(attr.Name.LocalName, "param1", StringComparison.OrdinalIgnoreCase);

                if (isCanPickupParam1)
                {
                    // Als leeg, zet éénmalig default naar eigen entry-naam (failsafe)
                    if (string.IsNullOrEmpty(attr.Value))
                    {
                        string own = parent?.Attribute("name")?.Value ?? "";
                        if (!string.IsNullOrEmpty(own))
                        {
                            p.SetAttributeValue(attr.Name, own);
                            dirty = true;
                        }
                    }

                    EditorGUILayout.BeginHorizontal();

                    string currentParam = p.Attribute("param1")?.Value ?? "";
                    bool known = string.IsNullOrEmpty(currentParam) || _knownNames.Contains(currentParam);

                    var prev = GUI.color;
                    if (!known) GUI.color = Color.red;

                    string newParam = EditorGUILayout.TextField("param1", currentParam, GUILayout.ExpandWidth(true));
                    GUI.color = prev;

                    // Quick set naar eigen naam
                    if (GUILayout.Button("Use entry name", GUILayout.Width(120)))
                    {
                        string own = parent?.Attribute("name")?.Value ?? "";
                        if (!string.IsNullOrEmpty(own)) newParam = own;
                    }

                    // Snelle, gefilterde picker
                    if (GUILayout.Button("Pick…", GUILayout.Width(60)))
                    {
                        string picked = FastNamePicker.Show(
                            title: "Pick item/block",
                            subtitle: "Type om te filteren • max 200 per pagina",
                            allNames: _knownNamesArr,
                            isItem: s => _knownItems.Contains(s),
                            isBlock: s => _knownBlocks.Contains(s),
                            prefill: newParam
                        );
                        if (!string.IsNullOrEmpty(picked)) newParam = picked;
                    }

                    if (newParam != currentParam) { p.SetAttributeValue(attr.Name, newParam); dirty = true; }

                    EditorGUILayout.EndHorizontal();

                    if (!known && !string.IsNullOrEmpty(newParam))
                        EditorGUILayout.HelpBox("Naam niet gevonden in base game of huidige mod.", MessageType.Warning);

                    continue; // skip default rendering
                }

                // default rendering voor andere attributen
                string newVal = EditorGUILayout.TextField(attr.Name.LocalName, attr.Value);
                if (newVal != attr.Value) { p.SetAttributeValue(attr.Name, newVal); dirty = true; }
            }


            EditorGUILayout.EndVertical();
        }
    }

    void MoveSibling(XElement el, int delta)
    {
        if (el?.Parent == null) return;

        var siblings = el.Parent.Elements(el.Name).ToList();
        if (siblings.Count <= 1) return;

        int idx = siblings.IndexOf(el);
        int newIdx = Mathf.Clamp(idx + delta, 0, siblings.Count - 1);
        if (newIdx == idx) return;

        var anchor = siblings[newIdx];
        el.Remove();

        if (newIdx < idx)  // omhoog
            anchor.AddBeforeSelf(el);
        else               // omlaag
            anchor.AddAfterSelf(el);

        dirty = true;
        EditorWindow.focusedWindow?.Repaint();
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
        selectedPatchEl = el;
        RebuildPatches();
    }

    void AddSetPatch()
    {
        if (doc?.Root == null) return;
        if (!EditorPrompt.PromptString("New <set> patch", "XPath (e.g. .../@value):", $"/{rootTag}/{entryTag}[@name='example']/property[@name='Max']/@value", out var xp)) return;
        if (!EditorPrompt.PromptString("New <set> patch", "Value:", "1", out var val)) return;
        var el = new XElement("set", new XAttribute("xpath", xp)) { Value = val ?? "" };
        doc.Root.AddFirst(el);
        dirty = true;
        selectedPatchEl = el;
        RebuildPatches();
    }

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

        // selecteer alleen niet-root appends (root-append is puur container en staat niet in de lijst)
        selectedPatchEl = string.Equals(xp, def, StringComparison.OrdinalIgnoreCase) ? null : el;

        NormalizeDocumentLayout();
        RebuildEntries();
        RebuildPatches();
    }


    // ------------------- PATCH EDITOR -------------------
    void RebuildPatches()
    {
        if (doc == null || doc.Root == null)
        {
            patchOps = new List<XElement>();
            selPatch = -1;
            selectedPatchEl = null;
            multiPatchSel.Clear();
            return;
        }

        string containerXpath = "/" + rootTag;

        // Show ALL patches, including the append to "/<rootTag>"
        patchOps = doc.Root
            .Descendants()
            .Where(e =>
                e.Name == "set" ||
                e.Name == "remove" ||
                (e.Name == "append" &&
                 !string.Equals((string)e.Attribute("xpath"), containerXpath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        SyncPatchSelection();
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
            var xmlStyle = GetXmlTextAreaStyle();
            var prevCol = GUI.color;
            var prevCC = GUI.contentColor;
            var prevBG = GUI.backgroundColor;
            GUI.color = GUI.contentColor = GUI.backgroundColor = Color.white;

            string changed = EditorGUILayout.TextArea(raw, xmlStyle,
                GUILayout.ExpandHeight(true), GUILayout.MinHeight(240));

            GUI.color = prevCol; GUI.contentColor = prevCC; GUI.backgroundColor = prevBG;

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

        // 1) Probeer een model-achtige waarde op dit entry of via extends-keten te vinden
        string modelVal = ResolveModelValueWithExtends(entry);
        if (string.IsNullOrEmpty(modelVal))
            return null;

        // 2) Haal een prefabnaam uit de value (werkt voor "#@modfolder:Resources/Bundle.unity3d?MyPrefab",
        //    "Assets/.../MyPrefab.prefab", of alleen "MyPrefab")
        string shortName = ExtractPrefabName(modelVal);
        if (string.IsNullOrEmpty(shortName))
            return null;

        prefabName = shortName;

        // 3) Zoek de prefab als asset in het project
        //    a) Eerst exacte bestandsnaam via name: filter (snelste en meest precies)
        var exact = AssetDatabase.FindAssets($"t:Prefab name:{shortName}");
        var pick = PickBestPrefabPath(exact, shortName);
        if (pick != null) return AssetDatabase.LoadAssetAtPath<GameObject>(pick);

        //    b) Iets ruimer zoeken op tekst
        var loose = AssetDatabase.FindAssets($"t:Prefab {shortName}");
        pick = PickBestPrefabPath(loose, shortName);
        if (pick != null) return AssetDatabase.LoadAssetAtPath<GameObject>(pick);

        // 4) Fallback: handmatig onder de mod/Prefabs map zoeken (filesystem)
        try
        {
            string prefabsFolder = Path.Combine(ctx.ModFolder, "Prefabs");
            if (Directory.Exists(prefabsFolder))
            {
                // probeer exact
                var matches = Directory.GetFiles(prefabsFolder, shortName + ".prefab", SearchOption.AllDirectories);
                if (matches.Length == 0)
                {
                    // en als laatste redmiddel: bevat naam (kan false positives geven)
                    matches = Directory.GetFiles(prefabsFolder, "*" + shortName + "*.prefab", SearchOption.AllDirectories);
                }
                if (matches.Length > 0)
                {
                    string assetPath = ModDesignerWindow.SystemPathToAssetPath(matches[0]);
                    if (!string.IsNullOrEmpty(assetPath))
                        return AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                }
            }
        }
        catch { /* ignore */ }

        return null;
    }

    // Zoekt een modelwaarde (Model/Meshfile) in entry of via extends-keten (eerst mod, dan base game)
    string ResolveModelValueWithExtends(XElement entry)
    {
        // 1) Probeer eerst op het entry zelf (incl. groups)
        if (TryGetModelLikeValue(entry, out var val))
            return val;

        // 2) Volg extends-keten, maar gebruik cached base game doc + index
        string target = GetExtendsTarget(entry);
        int guard = 0;

        while (!string.IsNullOrEmpty(target) && guard++ < 12)
        {
            // in huidig doc
            var parent = FindEntryByName(target, doc);

            // anders in base game (cached + O(1) lookup)
            if (parent == null)
                parent = FindInBaseGameByName(target);

            if (parent == null)
                break;

            if (TryGetModelLikeValue(parent, out val))
                return val;

            target = GetExtendsTarget(parent);
        }

        return null;
    }

    // Leest 'Model' of 'Meshfile' uit alle descendant <property>-nodes (dus ook binnen groups)
    bool TryGetModelLikeValue(XElement entry, out string value)
    {
        value = null;
        foreach (var p in entry.Descendants("property"))
        {
            var name = (string)p.Attribute("name");
            if (string.IsNullOrEmpty(name)) continue;

            if (string.Equals(name, "Model", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Meshfile", StringComparison.OrdinalIgnoreCase))
            {
                var v = (string)p.Attribute("value");
                if (!string.IsNullOrEmpty(v))
                {
                    value = v;
                    return true;
                }
            }
        }
        return false;
    }

    // Haal prefabnaam uit een Model/Meshfile value
    string ExtractPrefabName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        // Als er een '?' in zit, neem alles na de laatste '?'
        int q = raw.LastIndexOf('?');
        string tail = (q >= 0) ? raw.Substring(q + 1) : raw;

        // Strip eventuele pad + extensie en "(Clone)"
        string name = Path.GetFileNameWithoutExtension(tail)?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (name.EndsWith("(Clone)", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "(Clone)".Length).Trim();

        return name;
    }

    // Kies de beste prefab-asset uit een lijst GUIDs: eerst binnen deze mod, dan eerste de beste
    string PickBestPrefabPath(string[] guids, string shortName)
    {
        if (guids == null || guids.Length == 0) return null;

        string modAssetRoot = ModDesignerWindow.SystemPathToAssetPath(ctx.ModFolder)?.TrimEnd('/') ?? "";

        // 1) exacte bestandsnaam binnen de mod
        foreach (var g in guids)
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            if (!string.IsNullOrEmpty(modAssetRoot) && p.StartsWith(modAssetRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(p), shortName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
        }
        // 2) exacte bestandsnaam ergens
        foreach (var g in guids)
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            if (string.Equals(Path.GetFileNameWithoutExtension(p), shortName, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        // 3) anders: eerste binnen de mod
        foreach (var g in guids)
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            if (!string.IsNullOrEmpty(modAssetRoot) && p.StartsWith(modAssetRoot, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        // 4) laatste redmiddel
        return AssetDatabase.GUIDToAssetPath(guids[0]);
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

        // 2) Determine icon file name (default = existing CustomIcon -> entry name)
        string existingCustom = GetCustomIconNameFrom(entry);
        string defaultIconName = !string.IsNullOrEmpty(existingCustom)
            ? existingCustom
            : (entry.Attribute("name")?.Value ?? prefabName ?? prefab.name);

        if (!EditorPrompt.PromptString("Icon file name", "Name (without .png):", defaultIconName, out string iconName))
            return;

        // 3) Determine output folder for icon
        string atlasDir = Path.Combine(ctx.ModFolder, "XML", "UIAtlases", "ItemIconAtlas");
        Directory.CreateDirectory(atlasDir);
        string outPng = Path.Combine(atlasDir, iconName + ".png");

        // 4) Generate screenshot
        // Gebruik de UI-instellingen van deze module (zelfde als batch)
        int size = Mathf.Clamp(batchIconSize, 32, 2048);
        float yaw = batchYaw;
        float pitch = batchPitch;

        // Optioneel: respecteer ook de overwrite toggle van batch (handig en verwacht)
        if (!batchOverwrite && File.Exists(outPng))
        {
            EditorUtility.DisplayDialog("Icon bestaat al",
                $"'{iconName}.png' bestaat al en 'Overwrite existing PNGs' staat uit.\n" +
                "Zet Overwrite aan of kies een andere naam.", "OK");
            return;
        }

        bool ok = ScreenshotPrefabs.TryMakePrefabIcon(prefab, outPng, size, yaw, pitch);

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

        ClearIconCache(iconName);   // zorg dat preview meteen je nieuwe PNG ziet
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
            GUILayout.Label("No DescriptionKey found.");
            if (GUILayout.Button("+ Make DescriptionKey", GUILayout.Width(180)))
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
        if (ctx?.ModFolder == null || string.IsNullOrEmpty(iconName)) return false;

        string atlasDir = Path.Combine(ctx.ModFolder, "XML", "UIAtlases", "ItemIconAtlas");
        if (!Directory.Exists(atlasDir)) return false;

        foreach (var ext in _iconExts)
        {
            string sys = Path.Combine(atlasDir, iconName + ext);
            if (!File.Exists(sys)) continue;

            // Probeer eerst als Asset (wanneer het pad onder Assets/ valt)
            string assetPath = ModDesignerWindow.SystemPathToAssetPath(sys);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (t != null) { tex = t; usedPath = sys; return true; }
            }

            // Valt niet onder Assets? -> rechtstreeks vanaf disk laden (preview)
            try
            {
                tex = LoadTextureAbsolute(sys);   // maakt een losse Texture2D (DontSave)
                usedPath = sys;
                return tex != null;
            }
            catch { /* ignore */ }
        }
        return false;
    }


    bool TryResolveIconByNameCached(string name, string labelForOrigin, out Texture2D tex, out string origin)
    {
        tex = null; origin = "";
        if (string.IsNullOrEmpty(name)) return false;

        // Cache hits
        if (_iconTexCache.TryGetValue(name, out tex))
        {
            origin = _iconOriginCache.TryGetValue(name, out var o) ? o : labelForOrigin;
            return true;
        }
        if (_iconMissingCache.Contains(name))
            return false;

        // Probeer mod-atlas
        if (TryFindIconInModAtlas(name, out tex, out var p1))
        {
            _iconTexCache[name] = tex;
            _iconOriginCache[name] = $"{labelForOrigin} (mod atlas)";
            origin = _iconOriginCache[name];
            return true;
        }
        // Probeer default ItemIcons
        if (TryFindIconInDefaultItemIcons(name, out tex, out var p2))
        {
            _iconTexCache[name] = tex;
            _iconOriginCache[name] = $"{labelForOrigin} (default ItemIcons)";
            origin = _iconOriginCache[name];
            return true;
        }

        // Negatief cachen om herhaalde I/O te vermijden
        _iconMissingCache.Add(name);
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

        // A) Expliciete CustomIcon
        string iconName = GetCustomIconNameFrom(entry);
        if (!string.IsNullOrEmpty(iconName))
            return TryResolveIconByNameCached(iconName, $"CustomIcon '{iconName}'", out tex, out origin);

        // B) Geërfde CustomIcon via extends-keten (cached base doc + index)
        string extTarget = GetExtendsTarget(entry);
        string firstExtTarget = extTarget; // voor fallback label
        int guard = 0;

        while (!string.IsNullOrEmpty(extTarget) && guard++ < 8)
        {
            XElement parent = FindEntryByName(extTarget, doc) ?? FindInBaseGameByName(extTarget);
            if (parent == null) break;

            var parentIcon = GetCustomIconNameFrom(parent);
            if (!string.IsNullOrEmpty(parentIcon))
            {
                if (TryResolveIconByNameCached(parentIcon, $"Inherited '{parentIcon}' from '{extTarget}'", out tex, out origin))
                    return true;
            }
            extTarget = GetExtendsTarget(parent);
        }

        // C) Default game icon op basis van entry- of (eerste) extends-naam
        var candidates = new List<string>();
        var selfName = entry.Attribute("name")?.Value;
        if (!string.IsNullOrEmpty(selfName)) candidates.Add(selfName);
        if (!string.IsNullOrEmpty(firstExtTarget)) candidates.Add(firstExtTarget);

        foreach (var n in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryResolveIconByNameCached(n, $"Default game icon '{n}'", out tex, out origin))
                return true;
        }

        return false;
    }


    // === Color helpers ===
    bool TryParseHtmlColor(string s, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (!s.StartsWith("#")) s = "#" + s;          // sta ook "RRGGBB" toe
                                                      // Unity accepteert #RGB, #RRGGBB, #RRGGBBAA
        return ColorUtility.TryParseHtmlString(s, out c);
    }

    GUIStyle MakeTintedTextFieldStyle(Color c)
    {
        var style = new GUIStyle(EditorStyles.textField);
        style.normal.textColor = c;
        style.focused.textColor = c;
        style.active.textColor = c;
        style.hover.textColor = c;
        style.onNormal.textColor = c;
        style.onFocused.textColor = c;
        style.onActive.textColor = c;
        style.onHover.textColor = c;
        return style;
    }

    void BuildValidNameIndex()
    {
        _knownNames.Clear();
        _knownItems.Clear();
        _knownBlocks.Clear();

        void AddFromFileSafe(string path, string tag)
        {
            try
            {
                if (!File.Exists(path)) return;
                var xd = XDocument.Load(path);
                foreach (var e in xd.Root?.Elements(tag) ?? Enumerable.Empty<XElement>())
                {
                    var n = (string?)e.Attribute("name");
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (tag == "item") _knownItems.Add(n);
                    else if (tag == "block") _knownBlocks.Add(n);
                    _knownNames.Add(n);
                }
            }
            catch { /* negeer parse errors */ }
        }

        // Base game
        if (!string.IsNullOrEmpty(ctx?.GameConfigPath))
        {
            AddFromFileSafe(Path.Combine(ctx.GameConfigPath, "items.xml"), "item");
            AddFromFileSafe(Path.Combine(ctx.GameConfigPath, "blocks.xml"), "block");
        }

        // Eigen mod
        if (!string.IsNullOrEmpty(ctx?.ModConfigPath))
        {
            AddFromFileSafe(Path.Combine(ctx.ModConfigPath, "items.xml"), "item");
            AddFromFileSafe(Path.Combine(ctx.ModConfigPath, "blocks.xml"), "block");
        }

        // Huidig in-memory doc meenemen
        try
        {
            foreach (var e in doc?.Descendants() ?? Enumerable.Empty<XElement>())
            {
                if (e.Attribute("name") == null) continue;
                if (e.Name.LocalName is "item" or "block")
                {
                    var n = (string?)e.Attribute("name");
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (e.Name.LocalName == "item") _knownItems.Add(n);
                    else _knownBlocks.Add(n);
                    _knownNames.Add(n);
                }
            }
        }
        catch { }

        _knownNamesArr = _knownNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // NEW helpers (add these in the class body)
    void SyncPatchSelection()
    {
        // prune multi-select of removed nodes
        multiPatchSel.RemoveWhere(p => !patchOps.Contains(p));

        // keep main selection stable
        if (selectedPatchEl != null)
        {
            int idx = patchOps.IndexOf(selectedPatchEl);
            selPatch = (idx >= 0) ? idx : -1;
            if (selPatch < 0) selectedPatchEl = null;
        }
        else
        {
            selectedPatchEl = (selPatch >= 0 && selPatch < patchOps.Count) ? patchOps[selPatch] : null;
        }
    }

    void MoveSiblingGeneric(XElement el, int delta)
    {
        var parent = el?.Parent;
        if (parent == null) return;

        var siblings = parent.Elements().ToList();
        int idx = siblings.IndexOf(el);
        int newIdx = Mathf.Clamp(idx + delta, 0, siblings.Count - 1);
        if (newIdx == idx) return;

        var anchor = siblings[newIdx];
        el.Remove();

        if (newIdx < idx) anchor.AddBeforeSelf(el);
        else anchor.AddAfterSelf(el);
    }

    void MoveSelectedPatches(int delta)
    {
        if (multiPatchSel.Count == 0) return;
        var list = patchOps.Where(p => multiPatchSel.Contains(p)).ToList();
        if (delta > 0) list.Reverse(); // move down from bottom up
        foreach (var el in list) MoveSiblingGeneric(el, delta);
        dirty = true;
        RebuildPatches();
        EditorWindow.focusedWindow?.Repaint();
    }

    void DeleteSelectedPatches()
    {
        if (multiPatchSel.Count == 0) return;
        foreach (var el in patchOps.Where(p => multiPatchSel.Contains(p)).ToList())
            el.Remove();

        multiPatchSel.Clear();
        selectedPatchEl = null;
        selPatch = -1;
        dirty = true;
        RebuildPatches();
        EditorWindow.focusedWindow?.Repaint();
    }

    void InvalidateBaseGameCaches()
    {
        _baseGameDoc = null;
        _baseGameDocPath = null;
        _baseGameIndex = null;
    }

    XDocument GetBaseGameDocCached()
    {
        if (_baseGameDoc != null) return _baseGameDoc;
        if (string.IsNullOrEmpty(ctx?.GameConfigPath)) return null;

        string path = Path.Combine(ctx.GameConfigPath, fileName);
        if (!File.Exists(path)) return null;

        try
        {
            _baseGameDoc = XDocument.Load(path);
            _baseGameDocPath = path;
        }
        catch
        {
            _baseGameDoc = null;
        }
        return _baseGameDoc;
    }

    void EnsureBaseGameIndex()
    {
        if (_baseGameIndex != null) return;
        var bd = GetBaseGameDocCached();
        _baseGameIndex = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        if (bd?.Root == null) return;

        foreach (var e in bd.Descendants(entryTag))
        {
            var n = (string)e.Attribute("name");
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (!_baseGameIndex.ContainsKey(n)) _baseGameIndex[n] = e;
        }
    }

    XElement FindInBaseGameByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        EnsureBaseGameIndex();
        _baseGameIndex.TryGetValue(name, out var el);
        return el;
    }


}
