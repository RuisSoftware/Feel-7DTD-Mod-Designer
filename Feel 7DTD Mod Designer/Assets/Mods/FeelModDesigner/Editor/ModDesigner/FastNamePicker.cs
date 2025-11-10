using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class FastNamePicker : EditorWindow
{
    string[] _all = Array.Empty<string>();
    Func<string, bool> _isItem = _ => false;
    Func<string, bool> _isBlock = _ => false;

    string _filter = "";
    bool _showItems = true;
    bool _showBlocks = true;

    Vector2 _scroll;
    int _page = 0;
    const int PageSize = 200;

    string _result = null;
    Action<string> _onClose;

    public static string Show(string title, string subtitle, string[] allNames, Func<string, bool> isItem, Func<string, bool> isBlock, string prefill = "")
    {
        string result = null;
        var win = CreateInstance<FastNamePicker>();
        win.titleContent = new GUIContent(title);
        win._all = allNames ?? Array.Empty<string>();
        win._isItem = isItem ?? (_ => false);
        win._isBlock = isBlock ?? (_ => false);
        win._filter = prefill ?? "";
        win._onClose = s => result = s;
        win.position = new Rect(Screen.width / 2f, Screen.height / 2f, 520, 560);
        win.ShowModalUtility();
        return result;
    }

    void CloseWith(string s)
    {
        _onClose?.Invoke(s);
        Close();
    }

    void OnGUI()
    {
        GUILayout.Label(titleContent, EditorStyles.boldLabel);

        // Sub
        GUILayout.Label("Type to filter • Use Items/Blocks toggles • Paging if many results", EditorStyles.wordWrappedMiniLabel);
        GUILayout.Space(4);

        // Zoek + toggles
        EditorGUILayout.BeginHorizontal();
        GUI.SetNextControlName("fast_filter");
        string nf = EditorGUILayout.TextField("Search", _filter);
        if (nf != _filter) { _filter = nf; _page = 0; }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        _showItems = GUILayout.Toggle(_showItems, "Items", "Button", GUILayout.Width(70));
        _showBlocks = GUILayout.Toggle(_showBlocks, "Blocks", "Button", GUILayout.Width(70));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Clear", GUILayout.Width(60))) { _filter = ""; _page = 0; GUI.FocusControl("fast_filter"); }
        EditorGUILayout.EndHorizontal();

        // Filteren
        IEnumerable<string> q = _all;
        if (!_showItems || !_showBlocks)
        {
            if (_showItems && !_showBlocks) q = q.Where(_isItem);
            else if (_showBlocks && !_showItems) q = q.Where(_isBlock);
            else q = Array.Empty<string>();
        }
        if (!string.IsNullOrWhiteSpace(_filter))
        {
            var f = _filter.Trim();
            q = q.Where(s => s.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var list = q as string[] ?? q.ToArray();
        int total = list.Length;
        int totalPages = Mathf.Max(1, Mathf.CeilToInt(total / (float)PageSize));
        _page = Mathf.Clamp(_page, 0, totalPages - 1);

        // Paginering
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label($"Results: {total}  (showing page {_page + 1}/{totalPages}, max {PageSize}/page)");
        GUILayout.FlexibleSpace();
        GUI.enabled = _page > 0;
        if (GUILayout.Button("◀ Prev", GUILayout.Width(70))) _page--;
        GUI.enabled = _page < totalPages - 1;
        if (GUILayout.Button("Next ▶", GUILayout.Width(70))) _page++;
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        // Slice tekenen
        int start = _page * PageSize;
        int count = Mathf.Clamp(PageSize, 0, total - start);

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        for (int i = 0; i < count; i++)
        {
            string s = list[start + i];
            bool isIt = _isItem(s);
            bool isBl = _isBlock(s);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(s, GUILayout.ExpandWidth(true)))
                CloseWith(s);

            GUILayout.Label(isIt ? "item" : (isBl ? "block" : ""), EditorStyles.miniLabel, GUILayout.Width(40));
            EditorGUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        GUILayout.FlexibleSpace();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Cancel", GUILayout.Width(80))) CloseWith(null);
        GUILayout.FlexibleSpace();
        // Bij exact match -> snellink
        if (!string.IsNullOrEmpty(_filter) && list.Contains(_filter))
        {
            if (GUILayout.Button($"Use \"{_filter}\"", GUILayout.Width(160))) CloseWith(_filter);
        }
        EditorGUILayout.EndHorizontal();

        // Enter/ESC hotkeys
        var e = Event.current;
        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Escape) { CloseWith(null); e.Use(); }
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                // Als exact match, neem die, anders als er 1 resultaat in zicht is, neem die
                if (!string.IsNullOrEmpty(_filter) && list.Contains(_filter))
                    CloseWith(_filter);
                else if (count == 1)
                    CloseWith(list[start]);
                e.Use();
            }
        }
    }
}
