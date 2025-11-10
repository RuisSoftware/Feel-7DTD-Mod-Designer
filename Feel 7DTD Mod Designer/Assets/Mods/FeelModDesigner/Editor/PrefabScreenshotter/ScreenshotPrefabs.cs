// Assets/Editor/ScreenshotPrefabs.cs
using UnityEngine;
using UnityEditor;
using System.IO;

public class ScreenshotPrefabs
{
    /// <summary>
    /// Maakt een écht transparante (RGBA) 1:1 icon-screenshot van een prefab en slaat op als PNG.
    /// Gebruikt black/white matte compositing voor perfecte alpha (geen grijze randen).
    /// </summary>
    public static bool TryMakePrefabIcon(GameObject prefab, string savePath, int size = 512, float yawDeg = 45f, float pitchDeg = 25f)
    {
        if (prefab == null) return false;

        // 1) Render twee keer met verschillende achtergronden
        var texBlack = RenderPrefabWithBG(prefab, size, Color.black, yawDeg, pitchDeg);
        var texWhite = RenderPrefabWithBG(prefab, size, Color.white, yawDeg, pitchDeg);
        if (texBlack == null || texWhite == null)
        {
            Cleanup(texBlack);
            Cleanup(texWhite);
            return false;
        }

        // 2) Bereken alpha + niet-gepremultipliede kleur (unmultiply)
        var final = ComposeTransparent(texBlack, texWhite);

        // 3) Schrijf PNG
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(savePath, final.EncodeToPNG());
        AssetDatabase.Refresh();

        // 4) (opt.) Zet importer goed als dit in Assets/ staat
        string assetPath = ModDesignerWindow.SystemPathToAssetPath(Path.GetFullPath(savePath));
        if (!string.IsNullOrEmpty(assetPath))
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
        }

        Cleanup(texBlack);
        Cleanup(texWhite);
        Cleanup(final);
        return true;
    }

    // ----------------- Kern: render met vaste camera en lights -----------------
    static Texture2D RenderPrefabWithBG(GameObject prefab, int size, Color bg, float yawDeg, float pitchDeg)
    {
        var root = new GameObject("~IconRoot") { hideFlags = HideFlags.HideAndDontSave };
        GameObject instance = null;
        Camera cam = null;
        Light key = null, fill = null;
        RenderTexture rt = null;
        Texture2D outTex = null;

        try
        {
            instance = GameObject.Instantiate(prefab, root.transform);
            instance.hideFlags = HideFlags.HideAndDontSave;

            var rends = instance.GetComponentsInChildren<Renderer>();
            if (rends == null || rends.Length == 0)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(instance.transform, false);
                cube.hideFlags = HideFlags.HideAndDontSave;
                rends = instance.GetComponentsInChildren<Renderer>();
            }

            Bounds b = new Bounds(rends[0].bounds.center, Vector3.zero);
            for (int i = 0; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

            // Camera setup
            var camGO = new GameObject("~IconCam") { hideFlags = HideFlags.HideAndDontSave };
            camGO.transform.SetParent(root.transform, false);
            cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = bg;
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 1000f;
            cam.allowHDR = false;
            cam.allowMSAA = true;

            // >>> FRONT/RIGHT VIEW <<< 
            // yaw (Y-as)  = 45°  -> +X (rechts) +Z (voorkant)
            // pitch (X-as)= ~25° -> licht van boven
            Vector3 viewDir = Quaternion.Euler(-pitchDeg, yawDeg, 0f) * Vector3.forward;

            float radius = b.extents.magnitude;
            float halfFov = cam.fieldOfView * Mathf.Deg2Rad * 0.5f;
            float dist = Mathf.Max(0.1f, (radius / Mathf.Tan(halfFov)) * 1.2f);

            cam.transform.position = b.center + viewDir.normalized * dist;
            cam.transform.LookAt(b.center);

            // Lights: key ongeveer met camera mee, fill als tegenlicht
            key = new GameObject("~KeyLight").AddComponent<Light>();
            key.hideFlags = HideFlags.HideAndDontSave;
            key.type = LightType.Directional;
            key.intensity = 1.2f;
            key.transform.rotation = Quaternion.LookRotation(b.center - cam.transform.position) * Quaternion.Euler(10f, -15f, 0f);

            fill = new GameObject("~FillLight").AddComponent<Light>();
            fill.hideFlags = HideFlags.HideAndDontSave;
            fill.type = LightType.Directional;
            fill.intensity = 0.6f;
            fill.transform.rotation = Quaternion.LookRotation(cam.transform.position - b.center) * Quaternion.Euler(-10f, 180f, 0f);

            // Render target
            rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            var prevActive = RenderTexture.active;
            cam.targetTexture = rt;
            RenderTexture.active = rt;

            cam.Render();

            outTex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            outTex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            outTex.Apply();

            RenderTexture.active = prevActive;
            cam.targetTexture = null;

            return outTex;
        }
        finally
        {
            if (rt != null) rt.Release();
            if (cam != null) GameObject.DestroyImmediate(cam.gameObject);
            if (key != null) GameObject.DestroyImmediate(key.gameObject);
            if (fill != null) GameObject.DestroyImmediate(fill.gameObject);
            if (instance != null) GameObject.DestroyImmediate(instance);
            if (root != null) GameObject.DestroyImmediate(root);
        }
    }


    // Zet twee opaak renders (zwart/wit) om naar RGBA met perfecte alpha
    static Texture2D ComposeTransparent(Texture2D black, Texture2D white)
    {
        int w = black.width, h = black.height;
        var final = new Texture2D(w, h, TextureFormat.RGBA32, false, false);

        var pb = black.GetPixels32();
        var pw = white.GetPixels32();
        var pf = new Color32[pb.Length];

        for (int i = 0; i < pb.Length; i++)
        {
            // sRGB bytes -> 0..1
            float rb = pb[i].r / 255f, gb = pb[i].g / 255f, bb = pb[i].b / 255f;
            float rw = pw[i].r / 255f, gw = pw[i].g / 255f, bw = pw[i].b / 255f;

            // Alpha per kanaal; neem maximum (robuster)
            float ar = 1f - (rw - rb);
            float ag = 1f - (gw - gb);
            float ab = 1f - (bw - bb);
            float a = Mathf.Clamp01(Mathf.Max(ar, Mathf.Max(ag, ab)));

            float r = 0f, g = 0f, b = 0f;
            if (a > 1e-5f)
            {
                r = Mathf.Clamp01(rb / a);
                g = Mathf.Clamp01(gb / a);
                b = Mathf.Clamp01(bb / a);
            }

            pf[i] = new Color(r, g, b, a);
        }

        final.SetPixels32(pf);
        final.Apply(false, false);
        return final;
    }

    static void Cleanup(Object obj)
    {
        if (obj == null) return;
        if (obj is Texture2D) Object.DestroyImmediate(obj);
    }
}