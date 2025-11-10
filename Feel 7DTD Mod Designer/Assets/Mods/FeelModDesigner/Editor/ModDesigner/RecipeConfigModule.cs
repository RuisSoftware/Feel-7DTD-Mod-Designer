// Assets/Editor/RecipeConfigModule.cs
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

    List<XElement> patchOps = new();
    int selectedPatch = -1;
    Vector2 patchScroll;
    // RecipeConfigModule.cs - add this field in class definition
    private float patchListHeight = 150f;

    // Add these fields to RecipeConfigModule
    private bool resizingPatchList = false;
    private float startPatchHeight = 150f;
    private float startMouseY = 0f;

    private GUIStyle patchLabelStyle;
    private Color patchSelectedColor = new Color(1f, 0.6f, 0.2f, 0.4f); // oranje tint


    public void Initialize(ModContext ctx)
    {
        this.ctx = ctx;
        recipes.Clear(); selected = -1; search = ""; dirty = false;
        doc = null; filePath = null; baseItems.Clear();
        patchOps.Clear(); selectedPatch = -1;

        if (!ctx.HasValidMod) return;

        // Load base items from base game (to validate ingredient names)
        var baseItemsPath = Path.Combine(ctx.GameConfigPath ?? "", "items.xml");
        if (File.Exists(baseItemsPath))
        {
            var baseDoc = XDocument.Load(baseItemsPath);
            foreach (var it in baseDoc.Root!.Elements("item"))
            {
                var nameAttr = it.Attribute("name");
                if (nameAttr != null) baseItems.Add(nameAttr.Value);
            }
        }

        filePath = Path.Combine(ctx.ModConfigPath, "recipes.xml");
        if (!File.Exists(filePath)) return;
        doc = XDocument.Load(filePath);
        Rebuild();
        RebuildPatches();
    }

    void EnsureFileExists(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    void Rebuild()
    {
        var all = doc?.Descendants("recipe").Where(e => e.Attribute("name") != null).ToList() ?? new List<XElement>();
        if (string.IsNullOrWhiteSpace(search))
            recipes = all;
        else
        {
            var lc = search.ToLowerInvariant();
            recipes = all.Where(e => ((string)e.Attribute("name")!).ToLowerInvariant().Contains(lc)).ToList();
        }
        if (selected >= recipes.Count) selected = recipes.Count - 1;
        if (recipes.Count == 0) selected = -1;
    }

    // RecipeConfigModule.cs - updated OnGUIList() function
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

        // Search bar and reload
        EditorGUILayout.BeginHorizontal();
        search = EditorGUILayout.TextField(search, (GUIStyle)"SearchTextField");
        if (GUILayout.Button("×", (GUIStyle)"SearchCancelButton", GUILayout.Width(18)))
        {
            search = "";
            Rebuild();
        }
        if (GUILayout.Button("Reload", GUILayout.Width(70)))
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                doc = XDocument.Load(filePath);
            Rebuild();
            RebuildPatches();
        }
        EditorGUILayout.EndHorizontal();

        // Buttons
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ New"))
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("New Recipe"), false, () =>
            {
                if (EditorPrompt.PromptString("New recipe", "Name:", "newRecipe", out var nm))
                {
                    if (doc == null) return;
                    XElement parent = (doc.Root!.Name.LocalName == "recipes") ? doc.Root : EnsureAppendParent(doc, "/recipes");
                    if (parent.Descendants("recipe").Any(e => (string)e.Attribute("name") == nm))
                    {
                        EditorUtility.DisplayDialog("Already exists", $"Recipe '{nm}' already exists.", "OK");
                    }
                    else
                    {
                        var el = new XElement("recipe", new XAttribute("name", nm), new XAttribute("count", "1"));
                        parent.Add(el);
                        dirty = true;
                        Rebuild();
                        selected = recipes.IndexOf(el);
                        selectedPatch = -1;
                    }
                }
            });
            menu.AddItem(new GUIContent("Append Patch"), false, () =>
            {
                if (doc?.Root == null) return;
                string def = "/recipes";
                if (!EditorPrompt.PromptString("New <append> patch", "XPath:", def, out var xp)) return;
                var el = new XElement("append", new XAttribute("xpath", xp));
                if (!string.Equals(xp, "/recipes", StringComparison.OrdinalIgnoreCase))
                    el.Add(new XComment(" Add child nodes here "));
                doc.Root.AddFirst(el);
                dirty = true;
                RebuildPatches();
                selectedPatch = patchOps.IndexOf(el);
                selected = -1;
            });
            menu.AddItem(new GUIContent("Set Patch"), false, () =>
            {
                if (doc?.Root == null) return;
                if (!EditorPrompt.PromptString("New <set> patch", "XPath (e.g. .../@value):", "/recipes/recipe[@name='torch']/@count", out var xp)) return;
                if (!EditorPrompt.PromptString("New <set> patch", "Value:", "1", out var val)) return;
                var el = new XElement("set", new XAttribute("xpath", xp)) { Value = val ?? "" };
                doc.Root.AddFirst(el);
                dirty = true;
                RebuildPatches();
                selectedPatch = patchOps.IndexOf(el);
                selected = -1;
            });
            menu.AddItem(new GUIContent("Remove Patch"), false, () =>
            {
                if (doc?.Root == null) return;
                if (!EditorPrompt.PromptString("New <remove> patch", "XPath:", "/recipes/recipe[@name='torch']/ingredient[@name='wood']", out var xp)) return;
                var el = new XElement("remove", new XAttribute("xpath", xp));
                doc.Root.AddFirst(el);
                dirty = true;
                RebuildPatches();
                selectedPatch = patchOps.IndexOf(el);
                selected = -1;
            });
            menu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero));
        }

        if (GUILayout.Button("Import"))
        {
            ImportFromBaseRecipes();
        }

        GUI.enabled = selected >= 0 && selected < recipes.Count;
        if (GUILayout.Button("- Delete"))
        {
            var el = recipes[selected];
            string elName = (string?)el.Attribute("name") ?? "(unnamed)";
            if (EditorUtility.DisplayDialog("Delete", $"Delete '{elName}'?", "Yes", "No"))
            {
                el.Remove();
                dirty = true;
                Rebuild();
                selected = -1;
            }
        }
        if (GUILayout.Button("▲")) MoveRecipe(-1);
        if (GUILayout.Button("▼")) MoveRecipe(+1);
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        // Patch list
        GUILayout.Space(4);
        GUILayout.Label("Patches (set / append / remove)", EditorStyles.boldLabel);
        RebuildPatches();
        patchScroll = GUILayout.BeginScrollView(patchScroll, GUILayout.Height(patchListHeight));

        float rowHeight = EditorGUIUtility.singleLineHeight * 2f;
        for (int i = 0; i < patchOps.Count; i++)
        {
            var patch = patchOps[i];
            string xp = (string?)patch.Attribute("xpath") ?? "(xpath?)";
            string label = $"{patch.Name.LocalName}  {Truncate(xp, 60)}";
            GUIContent content = new GUIContent(label, xp); // hover geeft hele xpath

            Rect rowRect = GUILayoutUtility.GetRect(content, patchLabelStyle, GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight));

            if (i == selectedPatch)
                EditorGUI.DrawRect(rowRect, patchSelectedColor);

            Rect toggleRect = new Rect(rowRect.x + 6, rowRect.y + (rowHeight - 16f) / 2, 16, 16);
            bool newSelected = GUI.Toggle(toggleRect, i == selectedPatch, GUIContent.none, EditorStyles.radioButton);
            if (newSelected && i != selectedPatch)
            {
                selectedPatch = i;
                selected = -1;
                GUI.FocusControl(null);
                EditorWindow.focusedWindow?.Repaint();
            }

            Rect labelRect = new Rect(rowRect.x + 26, rowRect.y + 2, rowRect.width - 30, rowHeight - 4);
            GUI.Label(labelRect, content, patchLabelStyle);
        }

        GUILayout.EndScrollView();

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
            patchListHeight = Mathf.Clamp(startPatchHeight + delta, 60f, 500f); // eventueel max dynamisch maken
            Event.current.Use();
            EditorWindow.focusedWindow?.Repaint();
        }

        if (resizingPatchList && Event.current.type == EventType.MouseUp)
        {
            resizingPatchList = false;
            Event.current.Use();
        }

        // Recipe list
        GUILayout.Space(6);
        GUILayout.Label("Recipes", EditorStyles.boldLabel);
        leftScroll = GUILayout.BeginScrollView(leftScroll, GUILayout.ExpandHeight(true));
        var recipeNames = recipes.Select(r => r.Attribute("name")?.Value ?? "(unnamed)").ToArray();
        int newSelRecipe = GUILayout.SelectionGrid(selected, recipeNames, 1, "OL Box");
        if (newSelRecipe != selected)
        {
            selected = newSelRecipe;
            selectedPatch = -1;
        }
        GUILayout.EndScrollView();

        if (Event.current.type is EventType.MouseUp or EventType.MouseDown || GUI.changed)
            EditorWindow.focusedWindow?.Repaint();

        if (useArea) GUILayout.EndArea();
    }
    string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
        return s.Substring(0, maxLen) + "…";
    }

    void MoveRecipe(int delta)
    {
        if (selected < 0 || selected >= recipes.Count) return;
        var el = recipes[selected];
        var list = doc!.Root!.Elements("recipe").ToList();
        int idx = list.IndexOf(el);
        int newIdx = Mathf.Clamp(idx + delta, 0, list.Count - 1);
        if (newIdx == idx) return;
        el.Remove();
        if (newIdx >= list.Count) doc.Root.Add(el);
        else list[newIdx].AddBeforeSelf(el);
        dirty = true;
        Rebuild();
        selected = recipes.IndexOf(el);
    }

    // RecipeConfigModule.cs - updated OnGUIInspector() function
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

        // If a recipe is selected, clear patch selection
        if (selected >= 0 && selected < recipes.Count)
            selectedPatch = -1;

        // Patch inspector view
        if (selectedPatch >= 0 && selectedPatch < patchOps.Count)
        {
            var el = patchOps[selectedPatch];
            rightScroll = GUILayout.BeginScrollView(rightScroll);
            EditorGUILayout.LabelField($"Patch: <{el.Name.LocalName}>", EditorStyles.boldLabel);
            string raw = el.ToString(SaveOptions.None);
            var xmlStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
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
                    selectedPatch = patchOps.IndexOf(repl);
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
                selectedPatch = Mathf.Clamp(selectedPatch, 0, patchOps.Count - 1);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Save recipes.xml", GUILayout.Width(160))) Save();
            EditorGUILayout.EndHorizontal();
            GUILayout.EndScrollView();
            if (useArea) GUILayout.EndArea();
            return;
        }

        // Recipe inspector view
        if (selected < 0 || selected >= recipes.Count)
        {
            GUILayout.Label("Select a recipe or a patch from the list on the left…");
            if (useArea) GUILayout.EndArea();
            return;
        }

        var r = recipes[selected];
        rightScroll = GUILayout.BeginScrollView(rightScroll);

        // Attributes section
        GUILayout.Label("Attributes", EditorStyles.boldLabel);
        foreach (var at in r.Attributes().ToList())
        {
            string newVal = EditorGUILayout.TextField(at.Name.LocalName, at.Value);
            if (newVal != at.Value)
            {
                at.Value = newVal;
                dirty = true;
            }
        }
        if (GUILayout.Button("+ Add attribute"))
        {
            if (EditorPrompt.PromptString("Add attribute", "Name:", "craft_area", out var aname))
            {
                r.SetAttributeValue(aname, "");
                dirty = true;
            }
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
            var origColor = GUI.color;
            if (!exists) GUI.color = Color.red;
            string newNm = EditorGUILayout.TextField("name", nm);
            GUI.color = origColor;
            string newCnt = EditorGUILayout.TextField("count", cnt, GUILayout.Width(100));
            if (newNm != nm) { ing.SetAttributeValue("name", newNm); dirty = true; }
            if (newCnt != cnt) { ing.SetAttributeValue("count", newCnt); dirty = true; }
            if (GUILayout.Button("▲", GUILayout.Width(28))) { MoveSibling(ing, -1); }
            if (GUILayout.Button("▼", GUILayout.Width(28))) { MoveSibling(ing, +1); }
            if (GUILayout.Button("-", GUILayout.Width(28)))
            {
                ing.Remove(); dirty = true;
                EditorGUILayout.EndHorizontal();
                continue;
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ Add ingredient"))
        {
            r.Add(new XElement("ingredient", new XAttribute("name", ""), new XAttribute("count", "1")));
            dirty = true;
        }
        if (GUILayout.Button("+ Add other child"))
        {
            if (EditorPrompt.PromptString("New element", "Tag name:", "requirement", out var tag))
            {
                r.Add(new XElement(tag));
                dirty = true;
            }
        }
        EditorGUILayout.EndHorizontal();

        GUILayout.Space(10);
        if (GUILayout.Button("Save recipes.xml")) Save();

        GUILayout.EndScrollView();
        if (useArea) GUILayout.EndArea();
    }

    void RebuildPatches()
    {
        if (doc == null)
        {
            patchOps = new List<XElement>();
            selectedPatch = -1;
            return;
        }

        patchOps = doc.Root?.Elements()
            .Where(e => e.Name == "set" || e.Name == "remove" || e.Name == "append")
            .ToList() ?? new List<XElement>();

        selectedPatch = patchOps.Count > 0 ? Mathf.Clamp(selectedPatch, 0, patchOps.Count - 1) : -1;
    }

    void MoveSibling(XElement el, int delta)
    {
        var siblings = el.Parent!.Elements(el.Name).ToList();
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
        if (!ctx.HasValidMod || doc == null || filePath == null) return;
        if (!dirty) return;
        doc.Save(filePath);
        dirty = false;
    }

    // RecipeConfigModule.cs - updated ImportFromBaseRecipes() function
    void ImportFromBaseRecipes()
    {
        if (ctx == null || string.IsNullOrEmpty(ctx.GameConfigPath))
        {
            EditorUtility.DisplayDialog("Game Config", "Please set the Game Config Folder first.", "OK");
            return;
        }
        var basePath = Path.Combine(ctx.GameConfigPath, "recipes.xml");
        if (!File.Exists(basePath))
        {
            EditorUtility.DisplayDialog("File not found", $"Could not find recipes.xml at:\n{basePath}", "OK");
            return;
        }

        var baseDoc = XDocument.Load(basePath);
        var baseEntries = baseDoc.Descendants("recipe").Where(e => e.Attribute("name") != null).ToList();
        var names = baseEntries.Select(e => (string)e.Attribute("name")).OrderBy(s => s).ToArray();
        if (names.Length == 0)
        {
            EditorUtility.DisplayDialog("No entries", "No <recipe> entries found in recipes.xml.", "OK");
            return;
        }

        int pickIndex = EntryXmlModule.SimpleListPicker.Show("Import from base game", "Choose a recipe to copy", names);
        if (pickIndex < 0) return;
        string nm = names[pickIndex];
        var src = baseEntries.First(e => (string?)e.Attribute("name") == nm);

        XElement parent = (doc!.Root!.Name.LocalName == "recipes") ? doc.Root : EnsureAppendParent(doc, "/recipes");
        if (doc.Descendants("recipe").Any(e => (string)e.Attribute("name") == nm))
        {
            if (!EditorUtility.DisplayDialog("Already exists", $"Recipe '{nm}' already exists in this mod.\nAdd anyway?", "Yes", "No"))
                return;
        }
        parent.Add(new XElement(src));
        dirty = true;
        Rebuild();
        selected = recipes.FindIndex(e => (string?)e.Attribute("name") == nm);
    }


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
