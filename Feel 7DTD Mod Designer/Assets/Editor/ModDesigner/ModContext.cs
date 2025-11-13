using System.Collections.Generic;

public class ModContext
{
    public string GameConfigPath = "";  // e.g. ".../7DaysToDie/Data/Config" (read-only reference)
    public string ModFolder = "";       // e.g. ".../<ModName>"
    public string ModConfigPath = "";   // e.g. ".../<ModName>/XML/Config" or similar
    public string ModName = "";
    public string SelectedGameVersion = "";   // bv. "2.4"
    public bool IsVersionLocked = false;      // lock state voor huidige gameversie
    public string UnityTarget = "";           // opgeslagen unity versie voor huidige gameversie
    public string ModVersion = "1.0";         // modversie voor huidige gameversie
    public string ManifestPath = "";          // handig voor debug
    public bool HasValidMod => !string.IsNullOrEmpty(ModConfigPath);
    public Dictionary<string, LocalizationEntry> LocalizationEntries = new();

}