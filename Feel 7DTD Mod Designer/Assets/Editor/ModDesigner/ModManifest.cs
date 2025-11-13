using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
public class ModManifest
{
    [Serializable]
    public class GameVersionInfo
    {
        public string modVersion = "1.0";
        public string unity = "";          // bv. "2022.3.62f1"
        public bool locked = false;
    }

    public string currentGameVersion = "";                    // bv. "2.4"
    public Dictionary<string, GameVersionInfo> versions = new(); // key = "2.4"

    // --- helpers ---
    public static string GetManifestPath(string modFolder) => Path.Combine(modFolder, "XML", "manifest.json");

    public static ModManifest Load(string modFolder)
    {
        try
        {
            var path = GetManifestPath(modFolder);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
#if UNITY_2021_3_OR_NEWER
                return JsonUtility.FromJson<ModManifest>(json) ?? new ModManifest();
#else
                return JsonUtility.FromJson<ModManifest>(json) ?? new ModManifest();
#endif
            }
        }
        catch { /* ignore */ }
        return new ModManifest();
    }

    public void Save(string modFolder)
    {
        try
        {
            var path = GetManifestPath(modFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
            var json = JsonUtility.ToJson(this, true);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ModManifest] Save failed: {ex.Message}");
        }
    }

    public string[] ListGameVersions()
    {
        var keys = new List<string>(versions.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        return keys.ToArray();
    }

    public GameVersionInfo GetOrCreate(string gv)
    {
        if (!versions.TryGetValue(gv, out var v)) { v = new GameVersionInfo(); versions[gv] = v; }
        return v;
    }
}
