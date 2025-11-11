// Assets/Editor/TextPromptPopup.cs
using UnityEditor;
using UnityEngine;

public class TextPromptPopup : EditorWindow
{
    public enum PopupResult { None, Ok, Cancel }
    public PopupResult Result { get; private set; } = PopupResult.None;

    string promptLabel = "";
    public string Value { get; private set; } = "";

    public void Init(string label, string initial)
    {
        promptLabel = label;
        Value = initial;
        // Position the window roughly centered
        position = new Rect(Screen.width / 2f, Screen.height / 2f, 360, 120);
    }

    void OnGUI()
    {
        GUILayout.Space(8);
        EditorGUILayout.LabelField(promptLabel);
        Value = EditorGUILayout.TextField(Value);
        GUILayout.FlexibleSpace();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Cancel"))
        {
            Result = PopupResult.Cancel;
            Close();
        }
        if (GUILayout.Button("OK"))
        {
            Result = PopupResult.Ok;
            Close();
        }
        EditorGUILayout.EndHorizontal();
    }
}
