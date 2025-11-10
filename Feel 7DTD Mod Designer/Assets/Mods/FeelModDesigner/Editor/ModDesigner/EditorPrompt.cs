// Assets/Editor/EditorPrompt.cs
using UnityEditor;
using UnityEngine;

public static class EditorPrompt
{
    public static bool PromptString(string title, string label, string initial, out string result)
    {
        // Show a simple text input modal dialog
        var popup = ScriptableObject.CreateInstance<TextPromptPopup>();
        popup.titleContent = new GUIContent(title);
        popup.Init(label, initial);
        popup.ShowModalUtility();
        if (popup.Result == TextPromptPopup.PopupResult.Ok)
        {
            result = popup.Value;
            return true;
        }
        result = initial;
        return false;
    }
}
