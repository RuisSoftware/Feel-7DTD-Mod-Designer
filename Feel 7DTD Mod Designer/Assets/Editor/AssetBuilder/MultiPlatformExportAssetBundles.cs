using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class MultiPlatformExportAssetBundles
{
    [MenuItem("Assets/Build Multi-Platform AssetBundle From Selection")]
    static void ExportResource()
    {
        string path = EditorUtility.SaveFilePanel("Save Resource", "", "New Resource", "unity3d");
        if (string.IsNullOrEmpty(path)) return;

        PlayerSettings.SetGraphicsAPIs(
            BuildTarget.StandaloneWindows,
            new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore, GraphicsDeviceType.Vulkan });

        Object[] selection = Selection.GetFiltered(typeof(Object), SelectionMode.DeepAssets);

#pragma warning disable CS0618
        BuildPipeline.BuildAssetBundle(
            Selection.activeObject, selection, path,
            BuildAssetBundleOptions.CollectDependencies | BuildAssetBundleOptions.CompleteAssets,
            BuildTarget.StandaloneWindows);
        Selection.objects = selection;
#pragma warning restore CS0618
    }
}
