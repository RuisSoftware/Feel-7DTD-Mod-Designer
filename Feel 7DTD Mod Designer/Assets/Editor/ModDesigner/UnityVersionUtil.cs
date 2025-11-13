using System.Diagnostics;
using System.IO;
using System.Text;

public static class UnityVersionUtil
{
    public static bool TryGetUnityFromUnityPlayer(string gameFolder, out string version)
    {
        version = null;
        try
        {
            var dllPath = Path.Combine(gameFolder, "UnityPlayer.dll");
            if (!File.Exists(dllPath)) return false;
            var fvi = FileVersionInfo.GetVersionInfo(dllPath);
            var v = fvi.FileVersion ?? fvi.ProductVersion;
            if (string.IsNullOrWhiteSpace(v)) return false;
            version = v.Trim();
            return true;
        }
        catch { return false; }
    }

    public static bool TryGetUnityFromExe(string exePath, out string versionMaybeNoSuffix)
    {
        versionMaybeNoSuffix = null;
        try
        {
            if (!File.Exists(exePath)) return false;
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            var v = fvi.FileVersion ?? fvi.ProductVersion;
            if (string.IsNullOrWhiteSpace(v)) return false;
            versionMaybeNoSuffix = v.Trim();
            return true;
        }
        catch { return false; }
    }

    public static bool TryReadAssetBundleUnityVersions(string bundlePath, out string unityVersion, out string unityPlayerVersion)
    {
        unityVersion = null; unityPlayerVersion = null;
        try
        {
            using var fs = File.OpenRead(bundlePath);
            using var sr = new StreamReader(fs, Encoding.ASCII, false, 256, true);
            string sig = sr.ReadLine();
            if (sig != "UnityFS" && sig != "UnityRaw" && sig != "UnityWeb") return false;
            string fmt = sr.ReadLine();
            string engine = sr.ReadLine();
            string player = sr.ReadLine();
            if (string.IsNullOrWhiteSpace(engine)) return false;
            unityVersion = engine.Trim();
            unityPlayerVersion = string.IsNullOrWhiteSpace(player) ? engine.Trim() : player.Trim();
            return true;
        }
        catch { return false; }
    }


}
