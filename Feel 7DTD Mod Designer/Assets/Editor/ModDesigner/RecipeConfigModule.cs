using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

public class RecipeConfigModule : IConfigModule
{
    public string ModuleName => "Recipes";
    public string FileName => "recipes.xml";

    ModContext ctx;
    string? filePath;
    XDocument? doc;
    bool dirty;

    List<XElement> recipes = new();
    int selected = -1;
    string search = "";
    Vector2 leftScroll, rightScroll;

    HashSet<string> baseItems = new();  // item names from base game (for validation)

    // ---- PATCH STATE ----
    List<XElement> patchOps = new();
    int selectedPatch = -1;                        // legacy index (kept in sync)
    XElement selectedPatchEl = null;               // NEW: stable selection by element
    HashSet<XElement> multiPatchSel = new();       // NEW: multi-select support
    Vector2 patchScroll;

    // UI tuning
    private float patchListHeight = 150f;
    private bool resizingPatchList = false;
    private float startPatchHeight = 150f;
    private float startMouseY = 0f;

    private GUIStyle patchLabelStyle;
    private readonly Color patchSelectedColor = new(1f, 0.6f, 0.2f, 0.35f);

    // Raw XML editor buffer (avoid GC & cursor jumps)
    private XElement _patchEditorBoundEl = null;
    private string _patchEditorText = "";

    // === Static cached font & style voor XML-editor ===
    static readonly string[] kCodeFontCandidates = { "Consolas", "Courier New", "Lucida Console", "Menlo", "Monaco" };
    static Font s_CodeFont;
    static GUIStyle s_XmlTextAreaStyle;
    static bool s_FontHooked;

    public void Initialize(ModContext ctx)
    {
        this.ctx = ctx;
        recipes.Clear(); selected = -1; search = ""; dirty = false;
        doc = null; filePath = null; baseItems.Clear();
        patchOps.Clear(); selectedPatch = -1; selectedPatchEl = null; multiPatchSel.Clear();

        if (!ctx.HasValidMod) return;

        // Load base items from base game (to validate ingredient names)
        var baseItemsPath = Path.Combine(ctx.GameConfigPath ?? "", "items.xml");
        if (File.Exists(baseItemsPath))
        {
            try
            {
                var baseDoc = XDocument.Load(baseItemsPath);
                foreach (var it in baseDoc.Root!.Elements("item"))
                {
                    var nameAttr = it.Attribute("name");
                    if (nameAttr != null) baseItems.Add(nameAttr.Value);
                }
            }
            catch { /* ignore parse errors */ }
        }

        filePath = Path.Combine(ctx.ModConfigPath, "recipes.xml");
        if (!File.Exists(filePath)) return;
        doc = XDocument.Load(filePath);
        Rebuild();
        RebuildPatches();
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


    // ---------- Helpers for patch-layout normalization ----------
    XElement EnsureAppendToRootPatchElement()
    {
        if (doc?.Root == null) return null;
        const string wanted = "/recipes";
        var app = doc.Root.Elements("append").FirstOrDefault(a => (string)a.Attribute("xpath") == wanted);
        if (app == null)
        {
            app = new XElement("append", new XAttribute("xpath", wanted));
            doc.Root.Add(app);
            dirty = true;
        }
        return app;
    }

    void NormalizeDocumentLayout()
    {
        if (doc?.Root == null) return;
        var root = doc.Root;

        // Only for patch-style roots (e.g. <configs>), not when root is <recipes>
        bool usesPatchLayout = !root.Name.LocalName.Equals("recipes", StringComparison.OrdinalIgnoreCase);
        if (!usesPatchLayout) return;

        var appendRoot = EnsureAppendToRootPatchElement();
        if (appendRoot == null) return;

        // Move stray <recipe> nodes into appendRoot (from root and other append blocks)
        foreach (var e in root.Elements("recipe").ToList())
        {
            e.Remove(); appendRoot.Add(e); dirty = true;
        }
        foreach (var app in root.Elements("append").Where(a => a != appendRoot).ToList())
        {
            foreach (var e in app.Elements("recipe").ToList())
            {
                e.Remove(); appendRoot.Add(e); dirty = true;
            }
        }
        // Merge duplicate append blocks pointing to /recipes
        foreach (var dup in root.Elements("append")
                                 .Where(a => a != appendRoot && (string)a.Attribute("xpath") == "/recipes")
                                 .ToList())
        {
            foreach (var child in dup.Elements().ToList())
            {
                child.Remove(); appendRoot.Add(child);
            }
            dup.Remove(); dirty = true;
        }

        // Reorder: all patches first (set/remove/append not /recipes), then the appendRoot container at the end
        var children = root.Elements().ToList();
        var patches = children.Where(e => e.Name == "set" || e.Name == "remove" || (e.Name == "append" && e != appendRoot)).ToList();
        foreach (var el in root.Elements().ToList()) el.Remove();
        foreach (var p in patches) root.Add(p);
        root.Add(appendRoot);
    }

    // ---------- Core list rebuild ----------
    void Rebuild()
    {
        // Selectie behouden op NAAM
        string prevName = (selected >= 0 && selected < recipes.Count)
            ? (string)recipes[selected].Attribute("name")
            : null;

        var all = GetRecipesInDocumentOrder();

        recipes = string.IsNullOrWhiteSpace(search)
            ? all.ToList()
            : all.Where(e => ((string)e.Attribute("name")).ToLowerInvariant()
                     .Contains(search.ToLowerInvariant()))
                 .ToList();

        if (recipes.Count == 0) { selected = -1; return; }

        if (!string.IsNullOrEmpty(prevName))
        {
            int byName = recipes.FindIndex(e =>
                string.Equals((string)e.Attribute("name"), prevName, StringComparison.OrdinalIgnoreCase));
            if (byName >= 0) { selected = byName; return; }
        }

        selected = Mathf.Clamp(selected, 0, recipes.Count - 1);
    }

    // ---------- Left pane ----------
    public void OnGUIList(Rect rect)
    {
        if (patchLabelStyle == null)
        {
            patchLabelStyle = new GUIStyle(EditorStyles.label) { wordWrap = true, clipping = TextClipping.Clip };
        }

        bool useArea = rect.width > 0 && rect.height > 0;
        if (useArea) GUILayout.BeginArea(rect, EditorStyles.helpBox);
        if (!ctx.HasValidMod)
        {
            GUILayout.Label("No mod selected.");
            if (useArea) GUILayout.EndArea();
            return;
        }

        if (ctx?.IsVersionLocked == true)
        {
            EditorGUILayout.HelpBox($"Game version '{ctx.SelectedGameVersion}' is LOCKED. Editing is disabled.", MessageType.Warning);
        }

        // Search + reload
        EditorGUILayout.BeginHorizontal();
        search = EditorGUILayout.TextField(search, (GUIStyle)"SearchTextField");
        if (GUILayout.Button("×", (GUIStyle)"SearchCancelButton", GUILayout.Width(18)))
        { search = ""; Rebuild(); }
        if (GUILayout.Button("Reload", GUILayout.Width(70)))
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) doc = XDocument.Load(filePath);
            Rebuild();
            RebuildPatches();
        }
        EditorGUILayout.EndHorizontal();

        using (new EditorGUI.DisabledGroupScope(ctx?.IsVersionLocked == true))
        {
            // Buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ New"))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("New Recipe"), false, () =>
                {
                    if (doc == null && !EnsureDocumentCreated()) return;
                    if (!EditorPrompt.PromptString("New recipe", "Name:", "newRecipe", out var nm)) return;

                    XElement parent = (doc.Root!.Name.LocalName == "recipes") ? doc.Root : EnsureAppendParent(doc, "/recipes");

                    if (parent.Descendants("recipe").Any(e => (string)e.Attribute("name") == nm))
                    {
                        EditorUtility.DisplayDialog("Already exists", $"Recipe '{nm}' already exists.", "OK");
                    }
                    else
                    {
                        var el = new XElement("recipe", new XAttribute("name", nm), new XAttribute("count", "1"));
                        parent.Add(el);
                        dirty = true; Rebuild(); selected = recipes.IndexOf(el); selectedPatchEl = null; selectedPatch = -1;
                    }
                });
                menu.AddItem(new GUIContent("Append Patch"), false, AddAppendPatch);
                menu.AddItem(new GUIContent("Set Patch"), false, AddSetPatch);
                menu.AddItem(new GUIContent("Remove Patch"), false, AddRemovePatch);
                menu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero));
            }

            if (GUILayout.Button("Import"))
            {
                EditorApplication.delayCall += () => ImportFromBaseRecipes();
            }

            GUI.enabled = selected >= 0 && selected < recipes.Count;
            if (GUILayout.Button("- Delete"))
            {
                var el = recipes[selected];
                string elName = (string?)el.Attribute("name") ?? "(unnamed)";
                if (EditorUtility.DisplayDialog("Delete", $"Delete '{elName}'?", "Yes", "No"))
                { el.Remove(); dirty = true; Rebuild(); selected = -1; }
            }
            if (GUILayout.Button("▲")) MoveRecipe(-1);
            if (GUILayout.Button("▼")) MoveRecipe(+1);
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        // ---- PATCH LIST ----
        GUILayout.Space(4);
        GUILayout.Label("Patches (set / append / remove)", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Normalize layout", GUILayout.Width(140)))
        {
            NormalizeDocumentLayout();
            Rebuild();
            RebuildPatches();
        }
        if (GUILayout.Button("Reload", GUILayout.Width(70)))
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) doc = XDocument.Load(filePath);
            RebuildPatches();
        }
        GUILayout.FlexibleSpace();
        GUI.enabled = multiPatchSel.Count > 0;
        if (GUILayout.Button("▲ Move", GUILayout.Width(80))) MoveSelectedPatches(-1);
        if (GUILayout.Button("▼ Move", GUILayout.Width(80))) MoveSelectedPatches(+1);
        if (GUILayout.Button("Delete", GUILayout.Width(80))) DeleteSelectedPatches();
        GUI.enabled = true;
        if (GUILayout.Button("Select all", GUILayout.Width(90))) multiPatchSel = new HashSet<XElement>(patchOps);
        if (GUILayout.Button("None", GUILayout.Width(70))) multiPatchSel.Clear();
        EditorGUILayout.EndHorizontal();

        patchScroll = GUILayout.BeginScrollView(patchScroll, GUILayout.Height(patchListHeight));

        float rowHeight = EditorGUIUtility.singleLineHeight * 2.1f;
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
                EditorGUI.DrawRect(rowRect, patchSelectedColor);
            else if (isMultiSelected)
                EditorGUI.DrawRect(rowRect, new Color(0.2f, 0.7f, 1f, 0.20f));

            // Multi-select checkbox
            Rect checkRect = new Rect(rowRect.x + 6, rowRect.y + (rowHeight - 16f) / 2, 16, 16);
            bool before = isMultiSelected;
            bool after = GUI.Toggle(checkRect, before, GUIContent.none);
            if (after != before)
            {
                if (after) multiPatchSel.Add(patch); else multiPatchSel.Remove(patch);
                GUI.changed = true;
            }

            // Clickable label (whole row)
            Rect labelRect = new Rect(rowRect.x + 28, rowRect.y + 2, rowRect.width - 32, rowHeight - 4);
            GUI.Label(labelRect, new GUIContent(label, xp), patchLabelStyle);

            if (Event.current.type == EventType.MouseDown &&
                rowRect.Contains(Event.current.mousePosition) &&
                !checkRect.Contains(Event.current.mousePosition))
            {
                selected = -1; // focus patch inspector
                selectedPatchEl = patch;
                selectedPatch = i;

                // Reset editor buffer binding
                _patchEditorBoundEl = null;
                _patchEditorText = "";
                GUI.FocusControl(null);
                EditorWindow.focusedWindow?.Repaint();
            }
        }

        GUILayout.EndScrollView();

        // Resizable splitter
        Rect splitterRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(4), GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(new Rect(splitterRect.x, splitterRect.y + splitterRect.height / 2 - 1, splitterRect.width, 2), new Color(1f, 0.5f, 0f, 0.6f));
        EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeVertical);
        if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition))
        {
            resizingPatchList = true; startPatchHeight = patchListHeight; startMouseY = GUIUtility.GUIToScreenPoint(Event.current.mousePosition).y; Event.current.Use();
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
        { resizingPatchList = false; Event.current.Use(); }

        // Recipes list
        GUILayout.Space(6);
        GUILayout.Label("Recipes", EditorStyles.boldLabel);
        leftScroll = GUILayout.BeginScrollView(leftScroll, GUILayout.ExpandHeight(true));
        var recipeNames = recipes.Select(r => r.Attribute("name")?.Value ?? "(unnamed)").ToArray();
        int newSelRecipe = GUILayout.SelectionGrid(selected, recipeNames, 1, "OL Box");
        if (newSelRecipe != selected)
        { selected = newSelRecipe; selectedPatchEl = null; selectedPatch = -1; }
        GUILayout.EndScrollView();

        if (Event.current.type is EventType.MouseUp or EventType.MouseDown || GUI.changed)
            EditorWindow.focusedWindow?.Repaint();

        if (useArea) GUILayout.EndArea();
    }

    // ---------- Right pane (Inspector) ----------
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

        // Keep selectedPatchEl fresh if list was rebuilt
        if (selectedPatchEl != null && !patchOps.Contains(selectedPatchEl) && selectedPatch >= 0 && selectedPatch < patchOps.Count)
            selectedPatchEl = patchOps[selectedPatch];

        // Patch inspector takes precedence
        if (selectedPatchEl != null && patchOps.Contains(selectedPatchEl))
        {
            rightScroll = GUILayout.BeginScrollView(rightScroll);
            var el = selectedPatchEl;
            EditorGUILayout.LabelField($"Patch: <{el.Name.LocalName}>", EditorStyles.boldLabel);

            // Bind buffer once per selection
            if (_patchEditorBoundEl != el)
            {
                _patchEditorBoundEl = el;
                _patchEditorText = el.ToString(SaveOptions.None);
            }

            var xmlStyle = GetXmlTextAreaStyle();

            // Safety: zorg dat eerdere GUI-kleuren (alpha!) niet doorsijpelen
            var prevCol = GUI.color;
            var prevCC = GUI.contentColor;
            var prevBG = GUI.backgroundColor;
            GUI.color = Color.white;
            GUI.contentColor = Color.white;
            GUI.backgroundColor = Color.white;

            string changed = EditorGUILayout.TextArea(_patchEditorText, xmlStyle,
                GUILayout.ExpandHeight(true), GUILayout.MinHeight(240));

            GUI.color = prevCol;
            GUI.contentColor = prevCC;
            GUI.backgroundColor = prevBG;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply", GUILayout.Width(90)))
            {
                try
                {
                    var repl = XElement.Parse(_patchEditorText);
                    el.ReplaceWith(repl);
                    dirty = true;
                    RebuildPatches();
                    selectedPatchEl = repl;
                    selectedPatch = patchOps.IndexOf(repl);
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
                el.Remove();
                dirty = true;
                RebuildPatches();
                selectedPatchEl = null; selectedPatch = -1;
                _patchEditorBoundEl = null; _patchEditorText = "";
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Save recipes.xml", GUILayout.Width(160))) Save();
            EditorGUILayout.EndHorizontal();

            GUILayout.EndScrollView();
            if (useArea) GUILayout.EndArea();
            return;
        }

        // Recipe inspector
        if (selected < 0 || selected >= recipes.Count)
        {
            GUILayout.Label("Select a recipe or a patch from the list on the left…");
            if (useArea) GUILayout.EndArea();
            return;
        }

        var r = recipes[selected];
        rightScroll = GUILayout.BeginScrollView(rightScroll);

        GUILayout.Label("Attributes", EditorStyles.boldLabel);
        foreach (var at in r.Attributes().ToList())
        {
            string newVal = EditorGUILayout.TextField(at.Name.LocalName, at.Value);
            if (newVal != at.Value) { at.Value = newVal; dirty = true; }
        }
        if (GUILayout.Button("+ Add attribute"))
        {
            if (EditorPrompt.PromptString("Add attribute", "Name:", "craft_area", out var aname))
            { r.SetAttributeValue(aname, ""); dirty = true; }
        }

        GUILayout.Space(8);
        GUILayout.Label("Ingredients", EditorStyles.boldLabel);
        var ings = r.Elements("ingredient").ToList();
        for (int i = 0; i < ings.Count; i++)
        {
            var ing = ings[i];
            EditorGUILayout.BeginHorizontal();
            string nm = ing.Attribute("name")?.Value ?? "";
            string cnt = ing.Attribute("count")?.Value ?? "1";
            bool exists = string.IsNullOrEmpty(nm) || baseItems.Contains(nm);
            var origColor = GUI.color; if (!exists) GUI.color = Color.red;
            string newNm = EditorGUILayout.TextField("name", nm);
            GUI.color = origColor;
            string newCnt = EditorGUILayout.TextField("count", cnt, GUILayout.Width(100));
            if (newNm != nm) { ing.SetAttributeValue("name", newNm); dirty = true; }
            if (newCnt != cnt) { ing.SetAttributeValue("count", newCnt); dirty = true; }
            if (GUILayout.Button("▲", GUILayout.Width(28))) { MoveSibling(ing, -1); }
            if (GUILayout.Button("▼", GUILayout.Width(28))) { MoveSibling(ing, +1); }
            if (GUILayout.Button("-", GUILayout.Width(28))) { ing.Remove(); dirty = true; EditorGUILayout.EndHorizontal(); continue; }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ Add ingredient"))
        { r.Add(new XElement("ingredient", new XAttribute("name", ""), new XAttribute("count", "1"))); dirty = true; }
        if (GUILayout.Button("+ Add other child"))
        {
            if (EditorPrompt.PromptString("New element", "Tag name:", "requirement", out var tag))
            { r.Add(new XElement(tag)); dirty = true; }
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(10);
        if (GUILayout.Button("Save recipes.xml")) Save();

        GUILayout.EndScrollView();
        if (useArea) GUILayout.EndArea();
    }

    // ---------- Patches ----------
    void RebuildPatches()
    {
        if (doc?.Root == null)
        {
            patchOps = new();
            selectedPatch = -1;
            return;
        }

        const string containerXpath = "/recipes";

        patchOps = doc.Root
            .Elements() // << alleen top-level
            .Where(e =>
                string.Equals(e.Name.LocalName, "set", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Name.LocalName, "remove", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(e.Name.LocalName, "append", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals((string)e.Attribute("xpath"), containerXpath, StringComparison.OrdinalIgnoreCase)))
            .Where(e => e.Attribute("xpath") != null)
            .ToList();

        selectedPatch = (patchOps.Count > 0)
            ? Mathf.Clamp(selectedPatch, 0, patchOps.Count - 1)
            : -1;
    }


    void SyncPatchSelection()
    {
        // prune multi-select of removed nodes
        multiPatchSel.RemoveWhere(p => !patchOps.Contains(p));

        // keep main selection stable
        if (selectedPatchEl != null)
        {
            int idx = patchOps.IndexOf(selectedPatchEl);
            selectedPatch = (idx >= 0) ? idx : -1;
            if (selectedPatch < 0) selectedPatchEl = null;
        }
        else
        {
            selectedPatchEl = (selectedPatch >= 0 && selectedPatch < patchOps.Count) ? patchOps[selectedPatch] : null;
        }
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
        foreach (var el in patchOps.Where(p => multiPatchSel.Contains(p)).ToList()) el.Remove();
        multiPatchSel.Clear(); selectedPatchEl = null; selectedPatch = -1; dirty = true; RebuildPatches();
        EditorWindow.focusedWindow?.Repaint();
    }

    void MoveSiblingGeneric(XElement el, int delta)
    {
        var parent = el?.Parent; if (parent == null) return;
        var siblings = parent.Elements().ToList();
        int idx = siblings.IndexOf(el);
        int newIdx = Mathf.Clamp(idx + delta, 0, siblings.Count - 1);
        if (newIdx == idx) return;
        var anchor = siblings[newIdx];
        el.Remove();
        if (newIdx < idx) anchor.AddBeforeSelf(el); else anchor.AddAfterSelf(el);
    }

    void AddRemovePatch()
    {
        if (doc?.Root == null) return;
        if (!EditorPrompt.PromptString("New <remove> patch", "XPath:", "/recipes/recipe[@name='torch']/ingredient[@name='wood']", out var xp)) return;
        var el = new XElement("remove", new XAttribute("xpath", xp));
        doc.Root.AddFirst(el); dirty = true; RebuildPatches();
        selectedPatchEl = el; selectedPatch = patchOps.IndexOf(el); selected = -1;
    }

    void AddSetPatch()
    {
        if (doc?.Root == null) return;
        if (!EditorPrompt.PromptString("New <set> patch", "XPath (e.g. .../@value):", "/recipes/recipe[@name='torch']/@count", out var xp)) return;
        if (!EditorPrompt.PromptString("New <set> patch", "Value:", "1", out var val)) return;
        var el = new XElement("set", new XAttribute("xpath", xp)) { Value = val ?? "" };
        doc.Root.AddFirst(el); dirty = true; RebuildPatches();
        selectedPatchEl = el; selectedPatch = patchOps.IndexOf(el); selected = -1;
    }

    void AddAppendPatch()
    {
        if (doc?.Root == null) return;
        const string def = "/recipes";
        if (!EditorPrompt.PromptString("New <append> patch", "XPath:", def, out var xp)) return;
        var el = new XElement("append", new XAttribute("xpath", xp));
        if (!string.Equals(xp, def, StringComparison.OrdinalIgnoreCase)) el.Add(new XComment(" Add child nodes here "));
        doc.Root.AddFirst(el); dirty = true;

        // select only non-root appends; root-append is a container and hidden in list
        selectedPatchEl = string.Equals(xp, def, StringComparison.OrdinalIgnoreCase) ? null : el;
        NormalizeDocumentLayout();
        Rebuild();
        RebuildPatches();
        selected = -1;
    }

    // ---------- Misc helpers ----------
    string Truncate(string s, int maxLen) => string.IsNullOrEmpty(s) || s.Length <= maxLen ? s : s.Substring(0, maxLen) + "…";

    void MoveRecipe(int delta)
    {
        if (selected < 0 || selected >= recipes.Count) return;
        var el = recipes[selected];
        var list = doc!.Descendants("recipe").Where(e => e.Parent == el.Parent).ToList();
        int idx = list.IndexOf(el);
        int newIdx = Mathf.Clamp(idx + delta, 0, list.Count - 1);
        if (newIdx == idx) return;
        el.Remove();
        if (newIdx >= list.Count) el.Parent!.Add(el); else list[newIdx].AddBeforeSelf(el);
        dirty = true; Rebuild(); selected = recipes.IndexOf(el);
    }

    void MoveSibling(XElement el, int delta)
    {
        var siblings = el.Parent!.Elements(el.Name).ToList();
        int idx = siblings.IndexOf(el);
        int newIdx = Mathf.Clamp(idx + delta, 0, siblings.Count - 1);
        if (newIdx == idx) return;
        el.Remove();
        if (newIdx >= siblings.Count) el.Parent.Add(el); else siblings[newIdx].AddBeforeSelf(el);
        dirty = true;
    }

    public void Save()
    {
        if (!ctx.HasValidMod) return;

        if (ctx.IsVersionLocked)
        {
            EditorUtility.DisplayDialog("Locked",
                $"Cannot save: game version '{ctx.SelectedGameVersion}' is locked. Unlock in the top bar.", "OK");
            return;
        }

        if (doc != null) NormalizeDocumentLayout();
        if (doc == null || filePath == null || !dirty) return;
        doc.Save(filePath);
        dirty = false;
        AssetDatabase.Refresh();
    }

    void ImportFromBaseRecipes()
    {
        if (ctx == null || string.IsNullOrEmpty(ctx.GameConfigPath))
        { EditorUtility.DisplayDialog("Game Config", "Please set the Game Config Folder first.", "OK"); return; }
        var basePath = Path.Combine(ctx.GameConfigPath, "recipes.xml");
        if (!File.Exists(basePath))
        { EditorUtility.DisplayDialog("File not found", $"Could not find recipes.xml at:\n{basePath}", "OK"); return; }

        if (doc == null && !EnsureDocumentCreated()) return;
        if (doc.Root == null) doc.Add(new XElement("recipes"));

        var baseDoc = XDocument.Load(basePath);
        var baseEntries = baseDoc.Descendants("recipe").Where(e => e.Attribute("name") != null).ToList();
        var names = baseEntries.Select(e => (string)e.Attribute("name")).OrderBy(s => s).ToArray();
        if (names.Length == 0)
        { EditorUtility.DisplayDialog("No entries", "No <recipe> entries found in recipes.xml.", "OK"); return; }

        int pickIndex = EntryXmlModule.SimpleListPicker.Show("Import from base game", "Choose a recipe to copy", names);
        if (pickIndex < 0) return;
        string nm = names[pickIndex];
        var src = baseEntries.First(e => (string?)e.Attribute("name") == nm);

        XElement parent = doc.Root.Name.LocalName.Equals("recipes", StringComparison.OrdinalIgnoreCase) ? doc.Root : EnsureAppendParent(doc, "/recipes");

        if (doc.Descendants("recipe").Any(e => (string)e.Attribute("name") == nm))
        {
            if (!EditorUtility.DisplayDialog("Already exists", $"Recipe '{nm}' already exists in this mod.\nAdd anyway?", "Yes", "No")) return;
        }

        parent.Add(new XElement(src));
        dirty = true; Rebuild(); selected = recipes.FindIndex(e => (string?)e.Attribute("name") == nm);
        EditorWindow.focusedWindow?.Repaint();
    }

    IEnumerable<XElement> GetRecipesInDocumentOrder()
    {
        if (doc?.Root == null) return Enumerable.Empty<XElement>();

        bool IsUnderRootOrAppend(XElement e)
            => e.Ancestors().Any(a =>
                   string.Equals(a.Name.LocalName, "recipes", StringComparison.OrdinalIgnoreCase) ||
                   (string.Equals(a.Name.LocalName, "append", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)a.Attribute("xpath"), "/recipes", StringComparison.OrdinalIgnoreCase)));

        return doc.Root
            .DescendantNodesAndSelf()
            .OfType<XElement>()
            .Where(e => string.Equals(e.Name.LocalName, "recipe", StringComparison.OrdinalIgnoreCase) &&
                        e.Attribute("name") != null &&
                        IsUnderRootOrAppend(e));
    }

    bool EnsureDocumentCreated()
    {
        if (doc != null) return true;
        if (ctx == null || !ctx.HasValidMod) return false;
        if (string.IsNullOrEmpty(filePath)) filePath = Path.Combine(ctx.ModConfigPath, "recipes.xml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? "");
            doc = new XDocument(new XElement("recipes"));
            doc.Save(filePath);
            dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("Creation failed", $"Could not create recipes.xml:\n{ex.Message}", "OK");
            return false;
        }
    }

    // Shared helper from your original file
    static XElement EnsureAppendParent(XDocument d, string xpath)
    {
        var app = d.Root!.Descendants("append").FirstOrDefault(a => (string?)a.Attribute("xpath") == xpath);
        if (app == null)
        {
            app = new XElement("append", new XAttribute("xpath", xpath));
            d.Root.Add(app);
        }
        return app;
    }
}
