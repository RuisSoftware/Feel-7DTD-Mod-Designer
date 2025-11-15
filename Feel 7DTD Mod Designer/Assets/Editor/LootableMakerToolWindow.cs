// Assets/Editor/LootableMakerToolWindow.cs
// Lootable Maker – category Select/Deselect/Invert, clear "no lootable yet" status,
// stable IMGUI, ignored-tag skip, validation & autocomplete, performant icon loading.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using Microsoft.Win32;

public class LootableMakerToolWindow : EditorWindow
{
    // --- Paths ---
    private string configBasePath = Path.Combine(
        "D:\\", "Programs", "Steam", "steamapps", "common",
        "7 Days To Die", "Data", "Config");

    private string modsBasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "7DaysToDie", "Mods");

    private string itemIconsPath => TryResolveItemIcons(configBasePath);

    // --- Mod meta ---
    private string modNameBase = "feel-lootables";
    private string modVersion = "1.0.2";
    private string modAuthor = "You";
    private string modWebsite = "";
    private string modDescription = "Generated with Lootable Maker";
    private string readmeMarkdown =
@"# Lootable Maker
This mod makes selected decorative blocks lootable by appending Class=""Loot"" and generating matching lootcontainers.";

    // --- Scan & filters ---
    private bool autoDetectTried = false;
    private bool scanned = false;

    private string search = "";
    private bool showOnlyWithIcon = false;
    private bool showIcons = true;

    private string skipNameContainsCsv = "";                 // name filters
    private string skipFilterTagsCsv = "SC_terrain,ignored"; // tag filters (default includes 'ignored')

    // Show only entries whose LootList name does NOT exist in vanilla
    private bool onlyMissingLootList = false;

    // --- Loot defaults ---
    private int lootCountDefault = 1;
    private string lootSizeDefault = "2,2";
    private string lootOpenSound = "UseActions/open_garbage";
    private string lootCloseSound = "UseActions/close_garbage";
    private string lootQualityTemplate = "qualBaseTemplate";
    private bool lootDestroyOnClose = false;

    // --- UI state ---
    private Vector2 leftScroll, rightScroll;
    private int selectedIndex = -1;
    private readonly Dictionary<string, bool> groupFoldouts = new();

    // icon throttling
    private const int IconLoadLimitPerFrame = 24;
    private int iconsLoadedThisFrame = 0;

    // --- Data ---
    private readonly List<BlockInfo> candidates = new();
    private readonly Dictionary<string, XElement> blockByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> iconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> validItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> validBlocks = new(StringComparer.OrdinalIgnoreCase);

    // vanilla lootcontainers
    private readonly HashSet<string> lootContainers = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, AutoSuggestState> suggestStates = new();

    [MenuItem("Tools/Feel 7DTD/Lootable Maker")]
    public static void ShowWindow()
    {
        var w = GetWindow<LootableMakerToolWindow>("Lootable Maker");
        w.minSize = new Vector2(1020, 620);
    }

    private void OnGUI()
    {
        iconsLoadedThisFrame = 0;
        TryAutoDetectOnce();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Lootable Maker", EditorStyles.boldLabel);

        DrawPathsAndMeta();
        EditorGUILayout.Space(6);
        DrawFiltersAndDefaults();
        EditorGUILayout.Space(6);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan non-lootable blocks", GUILayout.Height(26))) Scan();

            GUI.enabled = scanned && candidates.Any(c => c.selected);
            if (GUILayout.Button("Generate Mod From Selection", GUILayout.Height(26))) GenerateMod();
            GUI.enabled = true;

            GUILayout.FlexibleSpace();
            showIcons = GUILayout.Toggle(showIcons, "Show icons", GUILayout.Width(100));
            showOnlyWithIcon = GUILayout.Toggle(showOnlyWithIcon, "Only with icon", GUILayout.Width(120));
            onlyMissingLootList = GUILayout.Toggle(onlyMissingLootList, "Only missing LootList", GUILayout.Width(170));
            GUILayout.Space(6);
            search = GUILayout.TextField(search, GUILayout.Width(260));
        }

        // Legend
        EditorGUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawPill("NOT LOOTABLE (vanilla)", new Color(0.85f, 0.55f, 0.15f, 0.85f));
            GUILayout.Space(6);
            DrawPill("No LootList (NEW)", new Color(0.7f, 0.2f, 0.2f, 0.9f));
            GUILayout.Space(6);
            DrawPill("LootList exists", new Color(0.2f, 0.6f, 0.2f, 0.85f));
        }

        EditorGUILayout.Space(6);
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawGroupedList();
            GUILayout.Space(8);
            DrawDetailsPanel();
        }

        // clicking outside closes all suggestion popups
        if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
        {
            foreach (var s in suggestStates.Values) s.open = false;
        }
    }

    // -------------------- UI: Top panels --------------------

    private void DrawPathsAndMeta()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Paths", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Config base path", GUILayout.Width(130));
            configBasePath = EditorGUILayout.TextField(configBasePath);
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                var sel = EditorUtility.OpenFolderPanel("Select 7DTD Data/Config folder", configBasePath, "");
                if (!string.IsNullOrEmpty(sel)) configBasePath = sel;
            }
            if (GUILayout.Button("Auto-detect", GUILayout.Width(100))) TryAutoDetect(showDialog: true);
            EditorGUILayout.EndHorizontal();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("ItemIcons path", GUILayout.Width(130));
                EditorGUILayout.SelectableLabel(itemIconsPath ?? "(not resolved)", GUILayout.Height(16));
                if (GUILayout.Button("Open", GUILayout.Width(70)))
                {
                    if (!string.IsNullOrEmpty(itemIconsPath) && Directory.Exists(itemIconsPath))
                        EditorUtility.RevealInFinder(itemIconsPath);
                }
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Mods base path", GUILayout.Width(130));
            modsBasePath = EditorGUILayout.TextField(modsBasePath);
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                var sel = EditorUtility.OpenFolderPanel("Select Mods base folder", modsBasePath, "");
                if (!string.IsNullOrEmpty(sel)) modsBasePath = sel;
            }
            EditorGUILayout.EndHorizontal();
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Mod Meta", EditorStyles.boldLabel);
            modNameBase = EditorGUILayout.TextField("Base mod name", modNameBase);
            modVersion = EditorGUILayout.TextField("Version", modVersion);
            modAuthor = EditorGUILayout.TextField("Author", modAuthor);
            modWebsite = EditorGUILayout.TextField("Website", modWebsite);
            EditorGUILayout.LabelField("Description");
            modDescription = EditorGUILayout.TextArea(modDescription, GUILayout.MinHeight(38));

            EditorGUILayout.LabelField("readme.md (Markdown)");
            readmeMarkdown = EditorGUILayout.TextArea(readmeMarkdown, GUILayout.MinHeight(70));
        }
    }

    private void DrawFiltersAndDefaults()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Scan Filters & Loot Defaults", EditorStyles.boldLabel);

            // Filters
            EditorGUILayout.LabelField("Skip blocks", EditorStyles.miniBoldLabel);
            skipNameContainsCsv = EditorGUILayout.TextField(
                new GUIContent("Name contains (CSV)", "Skip blocks whose name contains any of these substrings."),
                skipNameContainsCsv);
            skipFilterTagsCsv = EditorGUILayout.TextField(
                new GUIContent("FilterTags contains (CSV)", "Skip blocks whose FilterTags contains any of these tokens (always also skips 'ignored')."),
                skipFilterTagsCsv);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Loot Defaults", EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            lootCountDefault = EditorGUILayout.IntField(new GUIContent("count (rolls)"), lootCountDefault, GUILayout.MaxWidth(240));
            lootSizeDefault = EditorGUILayout.TextField(new GUIContent("size (cols,rows)"), lootSizeDefault, GUILayout.MaxWidth(240));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            lootOpenSound = EditorGUILayout.TextField(new GUIContent("sound_open"), lootOpenSound, GUILayout.MaxWidth(320));
            lootCloseSound = EditorGUILayout.TextField(new GUIContent("sound_close"), lootCloseSound, GUILayout.MaxWidth(320));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            lootQualityTemplate = EditorGUILayout.TextField(new GUIContent("loot_quality_template"), lootQualityTemplate, GUILayout.MaxWidth(320));
            lootDestroyOnClose = EditorGUILayout.Toggle(new GUIContent("destroy_on_close"), lootDestroyOnClose);
            EditorGUILayout.EndHorizontal();
        }
    }

    // -------------------- UI: List + Grouping --------------------

    private void DrawGroupedList()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(position.width * 0.55f)))
        {
            EditorGUILayout.LabelField("Candidates", EditorStyles.boldLabel);
            leftScroll = EditorGUILayout.BeginScrollView(leftScroll);

            if (!scanned)
                EditorGUILayout.HelpBox("Click Scan to load non-lootable blocks.", MessageType.Info);

            // MATERIALIZE up-front to keep Layout/Repaint control counts identical
            var filtered = FilteredCandidates().ToList();
            var grouped = filtered
                .GroupBy(c => Classify(c.blockName))
                .Select(g => new GroupBucket { Key = g.Key, Items = g.ToList() })
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var bucket in grouped)
            {
                bool open = GetFoldout(bucket.Key);
                open = EditorGUILayout.BeginFoldoutHeaderGroup(open, $"{bucket.Key}  ({bucket.Items.Count})");
                groupFoldouts[bucket.Key] = open;

                if (open)
                {
                    // --- NEW: Category-wide select tools ---
                    using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                    {
                        GUILayout.Label("Category selection:", EditorStyles.miniLabel);
                        if (GUILayout.Button("Select all", EditorStyles.toolbarButton, GUILayout.Width(80)))
                            foreach (var c in bucket.Items) c.selected = true;

                        if (GUILayout.Button("Deselect all", EditorStyles.toolbarButton, GUILayout.Width(90)))
                            foreach (var c in bucket.Items) c.selected = false;

                        if (GUILayout.Button("Invert", EditorStyles.toolbarButton, GUILayout.Width(60)))
                            foreach (var c in bucket.Items) c.selected = !c.selected;

                        GUILayout.FlexibleSpace();
                    }

                    foreach (var c in bucket.Items)
                    {
                        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                        {
                            c.selected = EditorGUILayout.Toggle(c.selected, GUILayout.Width(18));

                            // icon – only resolve during REPAINT to avoid layout divergence
                            if (showIcons)
                            {
                                Texture2D tex = null;
                                if (Event.current.type == EventType.Repaint)
                                    tex = GetIconFor(c);
                                GUILayout.Box(tex != null ? tex : Texture2D.grayTexture, GUILayout.Width(40), GUILayout.Height(40));
                            }

                            using (new EditorGUILayout.VerticalScope())
                            {
                                EditorGUILayout.LabelField(c.blockName, EditorStyles.boldLabel);

                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    // Status pills:
                                    DrawPill("NOT LOOTABLE (vanilla)", new Color(0.85f, 0.55f, 0.15f, 0.85f));

                                    var lootName = string.IsNullOrWhiteSpace(c.lootListName) ? c.blockName : c.lootListName.Trim();
                                    bool existsLL = LootListExists(lootName);

                                    GUILayout.Space(6);
                                    if (existsLL)
                                        DrawPill("LootList exists", new Color(0.2f, 0.6f, 0.2f, 0.85f));
                                    else
                                        DrawPill("No LootList (NEW)", new Color(0.7f, 0.2f, 0.2f, 0.9f));
                                }

                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    EditorGUILayout.PrefixLabel("LootList");
                                    c.lootListName = EditorGUILayout.TextField(c.lootListName ?? c.blockName);
                                }
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    EditorGUILayout.PrefixLabel("Size");
                                    c.size = EditorGUILayout.TextField(string.IsNullOrEmpty(c.size) ? lootSizeDefault : c.size, GUILayout.Width(80));
                                    GUILayout.Space(10);
                                    EditorGUILayout.PrefixLabel("Count");
                                    c.count = EditorGUILayout.IntField(c.count == 0 ? lootCountDefault : c.count, GUILayout.Width(60));
                                }
                            }

                            if (GUILayout.Button("Details", GUILayout.Width(70)))
                                selectedIndex = candidates.IndexOf(c);
                        }
                    }
                }

                EditorGUILayout.EndFoldoutHeaderGroup();
            }

            if (scanned && filtered.Count == 0)
                EditorGUILayout.HelpBox("No results with current search/filters.", MessageType.None);

            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select all (shown)")) foreach (var c in filtered) c.selected = true;
                if (GUILayout.Button("Deselect all (shown)")) foreach (var c in filtered) c.selected = false;
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Rescan")) Scan();
            }
        }
    }

    private IEnumerable<BlockInfo> FilteredCandidates()
    {
        IEnumerable<BlockInfo> q = candidates;

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(c => c.blockName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

        if (showOnlyWithIcon)
            q = q.Where(c => GetIconFor(c) != null);

        if (onlyMissingLootList)
            q = q.Where(c => !LootListExists(string.IsNullOrWhiteSpace(c.lootListName) ? c.blockName : c.lootListName.Trim()));

        return q;
    }

    private bool GetFoldout(string key)
    {
        if (!groupFoldouts.TryGetValue(key, out var v))
        {
            v = key.StartsWith("Pallet", StringComparison.OrdinalIgnoreCase);
            groupFoldouts[key] = v;
        }
        return v;
    }

    private static string Classify(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("pallet"))
        {
            if (n.Contains("bag")) return "Pallet (Bags)";
            if (n.Contains("tile")) return "Pallet (Tiles)";
            if (n.Contains("cap")) return "Pallet (Caps)";
            if (n.Contains("base")) return "Pallet (Base)";
            if (n.Contains("tv") || n.Contains("electronics")) return "Pallet (Electronics)";
            return "Pallet (Other)";
        }
        if (n.Contains("brick")) return "Bricks";
        if (n.Contains("drywall")) return "DryWall";
        if (n.Contains("sand")) return "Sand";
        if (n.Contains("concrete")) return "Concrete";
        if (n.Contains("fertilizer")) return "Fertilizer";
        if (n.Contains("flour")) return "Flour";
        if (n.Contains("cowfeed")) return "Cow Feed";
        if (n.Contains("cardboard") || n.Contains("brownboxes")) return "Cardboard / Boxes";
        if (n.Contains("pipe")) return "Pipes";
        if (n.Contains("wood") || n.Contains("plywood") || n.Contains("planks")) return "Wood";
        if (n.Contains("electronics") || n.Contains("tv")) return "Electronics";
        return "Other";
    }

    // -------------------- UI: Details & Loot rows --------------------

    private void DrawDetailsPanel()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
        {
            EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);
            rightScroll = EditorGUILayout.BeginScrollView(rightScroll);

            if (selectedIndex < 0 || selectedIndex >= candidates.Count)
            {
                EditorGUILayout.HelpBox("Select a block and click Details to edit lootcontainer & items.", MessageType.None);
            }
            else
            {
                var c = candidates[selectedIndex];
                Texture2D tex = null;
                if (showIcons && Event.current.type == EventType.Repaint)
                    tex = GetIconFor(c);
                GUILayout.Box(tex != null ? tex : Texture2D.grayTexture, GUILayout.Width(64), GUILayout.Height(64));

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Block", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(c.blockName, GUILayout.Height(16));
                EditorGUILayout.LabelField("Extends", c.extends ?? "(none)");
                EditorGUILayout.LabelField("CustomIcon", c.customIcon ?? "(none)");

                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Lootcontainer", EditorStyles.boldLabel);

                c.lootListName = EditorGUILayout.TextField("name", c.lootListName ?? c.blockName);
                var lootName = string.IsNullOrWhiteSpace(c.lootListName) ? c.blockName : c.lootListName.Trim();
                bool existsLL = LootListExists(lootName);

                // explicit status about "no lootable yet"
                if (!existsLL)
                {
                    EditorGUILayout.HelpBox(
                        $"No lootcontainer named '{lootName}' exists in vanilla. " +
                        "When generating, a NEW <lootcontainer> with this name will be added.",
                        MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        $"Lootcontainer '{lootName}' already exists in vanilla. " +
                        "Your output will include a container with the same name — consider using a unique name to avoid conflicts.",
                        MessageType.Info);
                }

                c.count = EditorGUILayout.IntField("count", c.count == 0 ? lootCountDefault : c.count);
                c.size = EditorGUILayout.TextField("size (cols,rows)", string.IsNullOrEmpty(c.size) ? lootSizeDefault : c.size);
                c.soundOpen = EditorGUILayout.TextField("sound_open", string.IsNullOrEmpty(c.soundOpen) ? lootOpenSound : c.soundOpen);
                c.soundClose = EditorGUILayout.TextField("sound_close", string.IsNullOrEmpty(c.soundClose) ? lootCloseSound : c.soundClose);
                c.qualityTemplate = EditorGUILayout.TextField("loot_quality_template", string.IsNullOrEmpty(c.qualityTemplate) ? lootQualityTemplate : c.qualityTemplate);
                c.destroyOnClose = EditorGUILayout.Toggle("destroy_on_close", c.destroyOnClose ?? lootDestroyOnClose);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Use unique name (prefix feel_)"))
                    {
                        string baseName = lootName;
                        if (!baseName.StartsWith("feel_", StringComparison.OrdinalIgnoreCase))
                            baseName = "feel_" + baseName;
                        int idx = 1;
                        string tryName = baseName;
                        while (LootListExists(tryName)) { tryName = $"{baseName}_{idx++}"; }
                        c.lootListName = tryName;
                    }
                    GUILayout.FlexibleSpace();
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Loot Items", EditorStyles.boldLabel);
                if (c.items == null) c.items = GuessItems(c.blockName);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Block?", GUILayout.Width(52));
                    GUILayout.Label("Name", GUILayout.MinWidth(160));
                    GUILayout.Label("Count", GUILayout.Width(90));
                    GUILayout.Label("Prob", GUILayout.Width(60));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("", GUILayout.Width(22));
                }

                for (int i = 0; i < c.items.Count; i++)
                {
                    var it = c.items[i];
                    bool exists = it.isBlock ? validBlocks.Contains(it.name) : validItems.Contains(it.name);

                    var oldBg = GUI.backgroundColor;
                    if (!exists) GUI.backgroundColor = new Color(1f, 0.78f, 0.78f);

                    using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                    {
                        it.isBlock = EditorGUILayout.ToggleLeft("", it.isBlock, GUILayout.Width(52));

                        string key = $"{selectedIndex}:{i}:{(it.isBlock ? "B" : "I")}";
                        if (!suggestStates.TryGetValue(key, out var s)) suggestStates[key] = s = new AutoSuggestState();

                        var before = it.name ?? "";
                        GUI.SetNextControlName(key);
                        it.name = EditorGUILayout.TextField(it.name ?? "", GUILayout.MinWidth(160));

                        if (!string.Equals(before, it.name, StringComparison.Ordinal))
                            s.open = !string.IsNullOrEmpty(it.name) && it.name.Length >= 2;

                        if (s.open)
                        {
                            var src = it.isBlock ? validBlocks : validItems;
                            s.suggestions = Suggest(src, it.name, 30);
                            if (s.suggestions.Count == 0) s.open = false;
                        }

                        if (GUILayout.Button("▼", GUILayout.Width(22)))
                        {
                            var src = it.isBlock ? validBlocks : validItems;
                            s.suggestions = Suggest(src, it.name, 100);
                            s.open = !s.open && s.suggestions.Count > 0;
                        }

                        if (s.open && Event.current.type == EventType.Repaint)
                            s.lastRect = GUILayoutUtility.GetLastRect();

                        if (s.open)
                        {
                            var popupRect = new Rect(s.lastRect.x, s.lastRect.yMax + 2, Mathf.Max(220, s.lastRect.width + 24), 160);
                            GUI.Box(popupRect, GUIContent.none, EditorStyles.helpBox);

                            var inner = new Rect(popupRect.x + 4, popupRect.y + 4, popupRect.width - 8, popupRect.height - 8);
                            s.scroll = GUI.BeginScrollView(inner, s.scroll, new Rect(0, 0, inner.width - 16, s.suggestions.Count * 20));
                            for (int j = 0; j < s.suggestions.Count; j++)
                            {
                                var r = new Rect(0, j * 20, inner.width - 16, 20);
                                if (GUI.Button(r, s.suggestions[j], EditorStyles.label))
                                {
                                    it.name = s.suggestions[j];
                                    s.open = false;
                                    GUI.FocusControl(null);
                                }
                            }
                            GUI.EndScrollView();
                        }

                        it.count = EditorGUILayout.TextField(it.count ?? "1", GUILayout.Width(90));
                        it.prob = EditorGUILayout.TextField(it.prob ?? "1", GUILayout.Width(60));

                        if (GUILayout.Button("X", GUILayout.Width(22)))
                        {
                            c.items.RemoveAt(i);
                            i--;
                        }
                    }

                    GUI.backgroundColor = oldBg;

                    if (!exists)
                        EditorGUILayout.HelpBox("Name does not exist in scanned game data.", MessageType.Warning);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Add Item"))
                        c.items.Add(new LootItem { name = "resourceWood", count = "1", prob = "1", isBlock = false });
                    if (GUILayout.Button("Reset by Guess"))
                        c.items = GuessItems(c.blockName);
                    GUILayout.FlexibleSpace();
                }
            }

            EditorGUILayout.EndScrollView();
        }
    }

    // Small colored badge/pill
    private static void DrawPill(string text, Color bg)
    {
        var style = new GUIStyle(EditorStyles.miniBoldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(6, 6, 2, 2)
        };
        var r = GUILayoutUtility.GetRect(new GUIContent(text), style, GUILayout.ExpandWidth(false));
        if (Event.current.type == EventType.Repaint)
        {
            var rr = new Rect(r.x, r.y + 2, r.width, r.height - 4);
            EditorGUI.DrawRect(rr, bg);
        }
        var old = GUI.color;
        GUI.color = Color.white;
        GUI.Label(r, text, style);
        GUI.color = old;
    }

    private static List<string> Suggest(HashSet<string> source, string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<string>();
        query = query.Trim();
        var starts = source.Where(s => s.StartsWith(query, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();
        if (starts.Count < limit)
        {
            var remain = limit - starts.Count;
            var contains = source.Where(s =>
                    s.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !starts.Contains(s))
                .Take(remain);
            starts.AddRange(contains);
        }
        return starts;
    }

    // -------------------- Actions --------------------

    private void Scan()
    {
        candidates.Clear();
        blockByName.Clear();
        iconCache.Clear();
        validItems.Clear();
        validBlocks.Clear();
        lootContainers.Clear();
        suggestStates.Clear();
        groupFoldouts.Clear();
        selectedIndex = -1;
        scanned = false;

        string blocksPath = Path.Combine(configBasePath, "blocks.xml");
        string itemsPath = Path.Combine(configBasePath, "items.xml");
        string lootPath = Path.Combine(configBasePath, "loot.xml");

        if (!File.Exists(blocksPath) || !File.Exists(itemsPath))
        {
            EditorUtility.DisplayDialog("Missing files", $"Expected:\n{blocksPath}\n{itemsPath}", "OK");
            return;
        }

        try
        {
            // Items
            var xItems = XDocument.Load(itemsPath);
            foreach (var it in xItems.Root.Descendants("item").Where(e => e.Attribute("name") != null))
                validItems.Add((string)it.Attribute("name"));

            // Blocks
            var xBlocks = XDocument.Load(blocksPath);
            foreach (var b in xBlocks.Root.Descendants("block").Where(e => e.Attribute("name") != null))
                validBlocks.Add((string)b.Attribute("name"));
            foreach (var b in xBlocks.Root.Descendants("block").Where(e => e.Attribute("name") != null))
                blockByName[(string)b.Attribute("name")] = b;

            // Lootcontainers (vanilla)
            if (File.Exists(lootPath))
            {
                var xLoot = XDocument.Load(lootPath);
                foreach (var lc in xLoot.Descendants("lootcontainer").Where(e => e.Attribute("name") != null))
                    lootContainers.Add((string)lc.Attribute("name"));
            }

            var nameSkips = SplitCsv(skipNameContainsCsv);
            var tagSkips = SplitCsv(skipFilterTagsCsv);
            if (!tagSkips.Contains("ignored", StringComparer.OrdinalIgnoreCase))
                tagSkips.Add("ignored");

            foreach (var kv in blockByName)
            {
                var name = kv.Key;
                var e = kv.Value;

                // Already lootable?
                var cls = e.Elements("property").FirstOrDefault(p => (string)p.Attribute("name") == "Class");
                bool isLoot = string.Equals((string)cls?.Attribute("value"), "Loot", StringComparison.OrdinalIgnoreCase);
                if (isLoot) continue;

                // Skip by name contains
                if (nameSkips.Any(s => name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                // Skip by FilterTags (any match OR contains 'ignored')
                var tags = GetPropertyValue(e, "FilterTags");
                if (!string.IsNullOrEmpty(tags))
                {
                    var tokens = tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim());
                    if (tokens.Any(t => t.Equals("ignored", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (tagSkips.Count > 0 && tokens.Any(t => tagSkips.Contains(t, StringComparer.OrdinalIgnoreCase)))
                        continue;
                }

                var info = new BlockInfo
                {
                    blockName = name,
                    extends = (string)e.Attribute("Extends"),
                    customIcon = GetPropertyValue(e, "CustomIcon"),
                    lootListName = name, // default proposal
                    size = lootSizeDefault,
                    count = lootCountDefault,
                    soundOpen = lootOpenSound,
                    soundClose = lootCloseSound,
                    qualityTemplate = lootQualityTemplate,
                    destroyOnClose = lootDestroyOnClose,
                    items = GuessItems(name)
                };
                candidates.Add(info);
            }
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("XML error", ex.Message, "OK");
            return;
        }

        scanned = true;
    }

    private void GenerateMod()
    {
        var selected = candidates.Where(c => c.selected).ToList();
        if (selected.Count == 0)
        {
            EditorUtility.DisplayDialog("Nothing selected", "Select at least one block.", "OK");
            return;
        }

        // Pre-flight: how many containers will be NEW vs existing
        int willCreate = selected.Count(c => !LootListExists((c.lootListName ?? c.blockName).Trim()));
        int willReuse = selected.Count - willCreate;

        if (!EditorUtility.DisplayDialog(
                "Generate lootables",
                $"Selected: {selected.Count}\nWill create NEW lootcontainers: {willCreate}\nWill reuse existing names: {willReuse}\n\nProceed?",
                "Generate", "Cancel"))
            return;

        var modName = $"{modNameBase}";
        var modPath = Path.Combine(modsBasePath, modName);
        var cfgPath = Path.Combine(modPath, "Config");
        Directory.CreateDirectory(cfgPath);

        var blocksOut = Path.Combine(cfgPath, "blocks.xml");
        using (var w = new StreamWriter(blocksOut, false, new UTF8Encoding(false)))
        {
            w.WriteLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
            w.WriteLine("<configs>");
            foreach (var c in selected)
            {
                var safe = EscapeApos(c.blockName);
                var lootName = string.IsNullOrWhiteSpace(c.lootListName) ? c.blockName : c.lootListName.Trim();

                w.WriteLine($@"  <append xpath=""/blocks/block[@name='{safe}' ]"">");
                w.WriteLine(@"    <property name=""Class"" value=""Loot"" />");
                w.WriteLine($@"    <property name=""LootList"" value=""{EscapeXmlAttr(lootName)}"" />");
                w.WriteLine("  </append>");
            }
            w.WriteLine("</configs>");
        }

        var lootOut = Path.Combine(cfgPath, "loot.xml");
        using (var w = new StreamWriter(lootOut, false, new UTF8Encoding(false)))
        {
            w.WriteLine("<configs>");
            w.WriteLine(@"	<append xpath=""/lootcontainers"">");
            foreach (var c in selected)
            {
                var lootName = string.IsNullOrWhiteSpace(c.lootListName) ? c.blockName : c.lootListName.Trim();
                var size = string.IsNullOrWhiteSpace(c.size) ? lootSizeDefault : c.size;
                var cnt = c.count <= 0 ? lootCountDefault : c.count;
                var open = string.IsNullOrWhiteSpace(c.soundOpen) ? lootOpenSound : c.soundOpen;
                var close = string.IsNullOrWhiteSpace(c.soundClose) ? lootCloseSound : c.soundClose;
                var qual = string.IsNullOrWhiteSpace(c.qualityTemplate) ? lootQualityTemplate : c.qualityTemplate;
                var destroy = (c.destroyOnClose ?? lootDestroyOnClose) ? "true" : "false";

                w.WriteLine($@"		<lootcontainer name=""{EscapeXmlAttr(lootName)}"" count=""{cnt}"" size=""{EscapeXmlAttr(size)}"" sound_open=""{EscapeXmlAttr(open)}"" sound_close=""{EscapeXmlAttr(close)}"" loot_quality_template=""{EscapeXmlAttr(qual)}"" destroy_on_close=""{destroy}"">");

                var items = (c.items == null || c.items.Count == 0) ? GuessItems(c.blockName) : c.items;
                foreach (var it in items)
                {
                    // Only write valid names (UI already shows invalid rows in red)
                    if (it.isBlock && !validBlocks.Contains(it.name)) continue;
                    if (!it.isBlock && !validItems.Contains(it.name)) continue;

                    var tag = it.isBlock ? "block" : "item";
                    w.WriteLine($@"			<{tag} name=""{EscapeXmlAttr(it.name)}"" count=""{EscapeXmlAttr(it.count)}"" prob=""{EscapeXmlAttr(it.prob)}""/>");
                }

                w.WriteLine("		</lootcontainer>");
                w.WriteLine();
            }
            w.WriteLine("	</append>");
            w.WriteLine("</configs>");
        }

        WriteModInfo(modPath, modName, modDescription, modAuthor, modVersion, modWebsite);
        File.WriteAllText(Path.Combine(modPath, "readme.md"), readmeMarkdown ?? "", new UTF8Encoding(false));

        EditorUtility.DisplayDialog("Done",
            $"Generated:\n{blocksOut}\n{lootOut}\n\nMod: {modName}\n\nNEW lootcontainers: {willCreate}\nReused names: {willReuse}",
            "OK");
        Debug.Log($"[LootableMaker] Generated mod at: {modPath}");
    }

    // -------------------- Helpers --------------------

    private void TryAutoDetectOnce()
    {
        if (autoDetectTried) return;
        autoDetectTried = true;
        TryAutoDetect(showDialog: false);
    }

    private void TryAutoDetect(bool showDialog)
    {
        var foundCfg = Find7DTDConfigPath(configBasePath);
        if (!string.IsNullOrEmpty(foundCfg)) configBasePath = foundCfg;

        var foundMods = FindModsPath(foundCfg);
        if (!string.IsNullOrEmpty(foundMods)) modsBasePath = foundMods;

        if (showDialog)
        {
            if (!string.IsNullOrEmpty(foundCfg))
            {
                EditorUtility.DisplayDialog("7 Days to Die found",
                    $"Config:\n{foundCfg}\n\nMods:\n{modsBasePath}\n\nItemIcons:\n{itemIconsPath}", "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Auto-detect failed",
                    "Fill the paths manually or use Browse.", "OK");
            }
        }
    }

    private string Find7DTDConfigPath(string currentGuess)
    {
        if (!string.IsNullOrEmpty(currentGuess) &&
            Directory.Exists(currentGuess) &&
            File.Exists(Path.Combine(currentGuess, "blocks.xml")))
            return currentGuess;

        var steam = GetSteamPathFromRegistry();
        var candidates = new List<string>();

        void AddIfExists(string p) { if (Directory.Exists(p)) candidates.Add(p); }

        if (!string.IsNullOrEmpty(steam))
        {
            var common = Path.Combine(steam, "steamapps", "common");
            AddIfExists(common);

            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (var line in File.ReadAllLines(vdf))
                {
                    var m = Regex.Match(line, "\"path\"\\s*\"([^\"]+)\"");
                    if (m.Success)
                    {
                        var lib = m.Groups[1].Value.Replace("\\\\", "\\");
                        AddIfExists(Path.Combine(lib, "steamapps", "common"));
                    }
                }
            }
        }

        AddIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common"));
        AddIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common"));

        foreach (var root in candidates.Distinct())
        {
            var game = Path.Combine(root, "7 Days To Die");
            var cfg = Path.Combine(game, "Data", "Config");
            if (Directory.Exists(cfg) && File.Exists(Path.Combine(cfg, "blocks.xml")))
                return cfg;
        }
        return null;
    }

    private string FindModsPath(string foundConfig)
    {
        var appMods = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie", "Mods");
        if (Directory.Exists(appMods)) return appMods;

        if (!string.IsNullOrEmpty(foundConfig))
        {
            var gameDir = Directory.GetParent(Directory.GetParent(foundConfig).FullName)?.FullName;
            var modsDir = string.IsNullOrEmpty(gameDir) ? null : Path.Combine(gameDir, "Mods");
            if (!string.IsNullOrEmpty(modsDir) && Directory.Exists(modsDir))
                return modsDir;
        }
        return null;
    }

    private static string GetSteamPathFromRegistry()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                var path = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(path)) return path.Replace("/", "\\");
            }
        }
        catch { }
        return null;
    }

    private static string TryResolveItemIcons(string cfgPath)
    {
        try
        {
            if (string.IsNullOrEmpty(cfgPath)) return null;
            var dataDir = Directory.GetParent(cfgPath)?.FullName; // ...\Data
            var icons = Path.Combine(dataDir ?? "", "ItemIcons");
            return Directory.Exists(icons) ? icons : null;
        }
        catch { return null; }
    }

    private Texture2D GetIconFor(BlockInfo info)
    {
        if (!showIcons) return null;
        if (iconCache.TryGetValue(info.blockName, out var t)) return t;

        // throttle loading strictly on repaint call sites
        if (iconsLoadedThisFrame >= IconLoadLimitPerFrame) return null;

        string[] tries = new[]
        {
            info.blockName,
            info.customIcon,
            ResolveAncestorCustomIcon(info.blockName)
        }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();

        foreach (var name in tries)
        {
            var path = TryFindIconPath(name);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var tex = LoadTexture(path);
                iconCache[info.blockName] = tex;
                iconsLoadedThisFrame++;
                return tex;
            }
        }
        iconCache[info.blockName] = null;
        return null;
    }

    private string ResolveAncestorCustomIcon(string blockName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string cur = blockName;
        while (!string.IsNullOrEmpty(cur) && !seen.Contains(cur))
        {
            seen.Add(cur);
            if (!blockByName.TryGetValue(cur, out var el)) break;

            var icon = GetPropertyValue(el, "CustomIcon");
            if (!string.IsNullOrWhiteSpace(icon)) return icon;

            cur = (string)el.Attribute("Extends");
        }
        return null;
    }

    private string TryFindIconPath(string iconName)
    {
        if (string.IsNullOrEmpty(itemIconsPath)) return null;
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
        {
            var p = Path.Combine(itemIconsPath, iconName + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static Texture2D LoadTexture(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(bytes);
            tex.Apply(false, true);
            return tex;
        }
        catch { return null; }
    }

    private static string GetPropertyValue(XElement blockEl, string propName)
    {
        var p = blockEl.Elements("property")
            .FirstOrDefault(x => (string)x.Attribute("name") == propName);
        return (string)p?.Attribute("value");
    }

    private bool LootListExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return lootContainers.Contains(name);
    }

    private static string EscapeApos(string s) => s?.Replace("'", "&apos;") ?? "";
    private static string EscapeXmlAttr(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static void WriteModInfo(string modPath, string modName, string desc, string author, string version, string website)
    {
        var xml =
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<xml><ModInfo>
  <Name value=""{EscapeXmlAttr(modName)}""/>
  <DisplayName value=""{EscapeXmlAttr(modName)}""/>
  <Description value=""{EscapeXmlAttr(desc)}""/>
  <Author value=""{EscapeXmlAttr(author)}""/>
  <Version value=""{EscapeXmlAttr(version)}""/>
  <Website value=""{EscapeXmlAttr(website)}""/>
</ModInfo></xml>";
        Directory.CreateDirectory(modPath);
        File.WriteAllText(Path.Combine(modPath, "ModInfo.xml"), xml, new UTF8Encoding(false));
    }

    private static List<string> SplitCsv(string csv)
    {
        var L = new List<string>();
        if (string.IsNullOrWhiteSpace(csv)) return L;
        foreach (var part in csv.Split(','))
        {
            var s = part.Trim();
            if (!string.IsNullOrEmpty(s)) L.Add(s);
        }
        return L;
    }

    // ---------- Guessing loot contents ----------
    private static List<LootItem> GuessItems(string blockName)
    {
        string n = blockName.ToLowerInvariant();
        var L = new List<LootItem>();
        bool any(params string[] keys) => keys.Any(k => n.Contains(k.ToLowerInvariant()));

        if (any("osbwood", "plywood", "woodplanks", "palletempty", "palletbrownboxes"))
        {
            L.Add(new LootItem("resourceWood", "10,30"));
        }
        else if (any("brick"))
        {
            L.Add(new LootItem("resourceRockSmall", "25,60"));
        }
        else if (any("drywall"))
        {
            L.Add(new LootItem("resourceRockSmall", "6,12"));
            L.Add(new LootItem("resourcePaper", "2,6"));
            L.Add(new LootItem("resourceCrushedSand", "8,16"));
        }
        else if (any("metalpipe"))
        {
            L.Add(new LootItem("resourceMetalPipe", "3,8"));
        }
        else if (any("concrete"))
        {
            L.Add(new LootItem("resourceCement", "10,40"));
        }
        else if (any("sand"))
        {
            L.Add(new LootItem("resourceCrushedSand", "10,40"));
        }
        else if (any("fertilizer"))
        {
            L.Add(new LootItem("resourcePotassiumNitratePowder", "10,40"));
        }
        else if (any("flour"))
        {
            L.Add(new LootItem("foodCornMeal", "2,5"));
        }
        else if (any("cowfeed"))
        {
            L.Add(new LootItem("plantedCorn1", "1,5"));
        }
        else if (any("cardboard"))
        {
            L.Add(new LootItem("resourcePaper", "15,30"));
        }
        else if (any("brownboxes"))
        {
            L.Add(new LootItem("resourcePaper", "1,3"));
            L.Add(new LootItem("resourceScrapPolymers", "1,3"));
        }
        else if (any("cans"))
        {
            L.Add(new LootItem("resourceScrapIron", "15,30"));
        }
        else if (any("tarp"))
        {
            L.Add(new LootItem("resourceRockSmall", "10,30", "0.2"));
            L.Add(new LootItem("resourceCrushedSand", "10,30", "0.2"));
            L.Add(new LootItem("resourceClayLump", "10,30", "0.2"));
            L.Add(new LootItem("resourceCobblestones", "10,30", "0.2"));
        }
        else if (any("electronics", "tv"))
        {
            return new List<LootItem> { new LootItem("tvSmallWall1x1", "1", "1", isBlock: true) };
        }
        else
        {
            L.Add(new LootItem("resourceWood", "1,5", "0.5"));
            L.Add(new LootItem("resourceRockSmall", "1,5", "0.5"));
        }

        return L;
    }

    // ---------- Data types ----------

    private class GroupBucket
    {
        public string Key;
        public List<BlockInfo> Items;
    }

    private class BlockInfo
    {
        public string blockName;
        public string extends;
        public string customIcon;
        public bool selected;

        public string lootListName;
        public int count;
        public string size;
        public string soundOpen;
        public string soundClose;
        public string qualityTemplate;
        public bool? destroyOnClose;

        public List<LootItem> items = new();
    }

    private class LootItem
    {
        public bool isBlock;
        public string name;
        public string count;
        public string prob;

        public LootItem() { }
        public LootItem(string name, string count, string prob = "1", bool isBlock = false)
        {
            this.name = name;
            this.count = count;
            this.prob = prob;
            this.isBlock = isBlock;
        }
    }

    private class AutoSuggestState
    {
        public bool open;
        public List<string> suggestions = new();
        public Rect lastRect;
        public Vector2 scroll;
    }
}
