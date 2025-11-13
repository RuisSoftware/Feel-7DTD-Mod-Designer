using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

class CopyCandidate
{
    public string Label;          // bv. "Config/" of "ModInfo.xml"
    public string SourcePath;     // absolute bron (bestand of directory). Leeg als 'virtual'
    public string DestRelPath;    // relatieve bestemming t.o.v. destGvRoot (bv. "Config", "ModInfo.xml")
    public bool IsDir;
    public bool Selected = true;
    public bool IsVirtualGenerate = false; // als we ModInfo moeten genereren
}

class CopyPreviewWindow : EditorWindow
{
    string _title;
    string _subtitle;
    string _destPathToShow;
    List<CopyCandidate> _items;
    Vector2 _scroll;
    Action<bool> _onClose;

    public static bool Show(string title, string subtitle, string destPath, List<CopyCandidate> items)
    {
        bool accepted = false;
        var win = CreateInstance<CopyPreviewWindow>();
        win._title = title;
        win._subtitle = subtitle;
        win._destPathToShow = destPath;
        win._items = items;
        win._onClose = ok => accepted = ok;
        win.position = new Rect(Screen.width / 2f, Screen.height / 2f, 520, 540);
        win.ShowModalUtility();
        return accepted;
    }

    void OnGUI()
    {
        GUILayout.Label(_title, EditorStyles.boldLabel);
        if (!string.IsNullOrEmpty(_subtitle))
            GUILayout.Label(_subtitle, EditorStyles.wordWrappedMiniLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Alles aan")) foreach (var i in _items) i.Selected = true;
            if (GUILayout.Button("Alles uit")) foreach (var i in _items) i.Selected = false;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Toon nieuwe mod map")) EditorUtility.RevealInFinder(_destPathToShow);
        }

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        foreach (var it in _items)
        {
            using (new EditorGUILayout.HorizontalScope("box"))
            {
                it.Selected = EditorGUILayout.Toggle(it.Selected, GUILayout.Width(22));
                GUILayout.Label(it.IsDir ? "📁" : (it.IsVirtualGenerate ? "✨" : "📄"), GUILayout.Width(22));
                GUILayout.Label(it.Label, GUILayout.Width(200));
                GUILayout.Label($"→ {it.DestRelPath}", EditorStyles.miniLabel);
            }
        }
        GUILayout.EndScrollView();

        GUILayout.FlexibleSpace();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Annuleren")) { _onClose?.Invoke(false); Close(); }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Kopiëren")) { _onClose?.Invoke(true); Close(); }
        }
    }
}
