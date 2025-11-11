using System.Collections.Generic;

public class ModContext
{
    public string GameConfigPath = "";  // e.g. ".../7DaysToDie/Data/Config" (read-only reference)
    public string ModFolder = "";       // e.g. ".../<ModName>"
    public string ModConfigPath = "";   // e.g. ".../<ModName>/XML/Config" or similar
    public string ModName = "";
    public bool HasValidMod => !string.IsNullOrEmpty(ModConfigPath);
    public Dictionary<string, LocalizationEntry> LocalizationEntries = new();

}