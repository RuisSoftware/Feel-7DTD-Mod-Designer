using System;
using System.Collections.Generic;

public class LocalizationEntry
{
    public string Key;
    public string Source;
    public string Context;
    public string Changes;

    // 👇 Belangrijk: case-insensitive keys
    public Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase);

    public string English
    {
        get => Languages.TryGetValue("English", out var v) ? v : "";
        set => Languages["English"] = value ?? "";
    }
    public string Dutch
    {
        get => Languages.TryGetValue("Dutch", out var v) ? v : "";
        set => Languages["Dutch"] = value ?? "";
    }
}
