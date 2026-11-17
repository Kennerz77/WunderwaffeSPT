using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Wunderwaffe.Editor
{
    /// <summary>
    /// Unity 2022.3.43f1 editor helpers for the Wunderwaffe.
    /// Menu: Wunderwaffe > 1. Import Wunderwaffe, then Wunderwaffe > 2. Build bundles.
    /// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod Wunderwaffe.Editor.WwBundleTools.RunAllBatch
    /// </summary>
    public static class WwBundleTools
    {
        private const string BuildDir = "AssetBundles";
        // Only this install. Do NOT point at D:\OTHER LAUNCHERS\BSTATE GAMES.
        private const string ServerModDir = @"D:\OTHER LAUNCHERS\BUTTERSTATE\SPT_Runtime\user\mods\Wunderwaffe";
        // Bundle keys are paths under the server mod's bundles folder; the item templates point at the same keys
        private const string KeyFolder = "Wunderwaffe";
        private const string PluginDir = @"D:\OTHER LAUNCHERS\BUTTERSTATE\BepInEx\plugins\Wunderwaffe";

        private static readonly (string bundle, string asset)[] Bundles =
        {
            ($"{WwPaths.ContainerName}.bundle", WwImporter.ContainerPrefabPath),
            ($"{WwPaths.MagName}.bundle", WwImporter.MagPrefabPath),
        };

        [MenuItem("Wunderwaffe/2. Build bundles")]
        public static void BuildMenu()
        {
            if (Build())
                EditorUtility.RevealInFinder(Path.Combine(ServerModDir, "bundles"));
        }

        public static bool Build()
        {
            foreach (var (_, asset) in Bundles)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(asset) == null)
                {
                    Debug.LogError($"[Wunderwaffe] No prefab at {asset}. Run the import first.");
                    return false;
                }
            }

            Directory.CreateDirectory(BuildDir);
            // EFT finds the prefab by name: the asset whose name is the bundle's file name without the extension
            AssetBundleBuild[] builds = Bundles.Select(b => new AssetBundleBuild { assetBundleName = b.bundle, assetNames = new[] { b.asset } })
                .Append(new AssetBundleBuild { assetBundleName = WwFx.BundleName, assetNames = WwFx.Prefabs.Select(WwFx.PrefabPath).ToArray() })
                .ToArray();
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(BuildDir, builds, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            if (manifest == null)
            {
                Debug.LogError("[Wunderwaffe] Bundle build failed; see console.");
                return false;
            }

            string dest = Path.Combine(ServerModDir, "bundles", KeyFolder);
            Directory.CreateDirectory(dest);
            foreach (var (bundle, _) in Bundles)
            {
                File.Copy(Path.Combine(BuildDir, bundle), Path.Combine(dest, bundle), true);
                Debug.Log($"[Wunderwaffe] {bundle}: {new FileInfo(Path.Combine(dest, bundle)).Length / 1024} KB");
            }

            // SPT loads a mod's bundles from bundles.json next to its DLL. Each bundle is self-contained (its shaders
            // and textures are inside), so nothing depends on the game's own bundles.
            string entries = string.Join(",\n", Bundles.Select(b => $"    {{ \"key\": \"{KeyFolder}/{b.bundle}\", \"dependencyKeys\": [] }}"));
            File.WriteAllText(Path.Combine(ServerModDir, "bundles.json"), "{\n  \"manifest\": [\n" + entries + "\n  ]\n}\n");
            Debug.Log($"[Wunderwaffe] Built and copied bundles -> {dest}");

            // The bolt and impact effects: loaded by the client from its own plugin folder
            Directory.CreateDirectory(PluginDir);
            File.Copy(Path.Combine(BuildDir, WwFx.BundleName), Path.Combine(PluginDir, WwFx.BundleName), true);
            Debug.Log($"[Wunderwaffe] {WwFx.BundleName}: {new FileInfo(Path.Combine(PluginDir, WwFx.BundleName)).Length / 1024} KB -> {PluginDir}");
            return true;
        }

        public static void RunAllBatch()
        {
            try
            {
                WwImporter.ImportAll();
                WwPreview.RenderBatch();
                WwIcons.RenderAll();
                if (!Build())
                    EditorApplication.Exit(1);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Import and previews only (no bundles), for checking the retarget.</summary>
        public static void PreviewBatch()
        {
            try
            {
                WwImporter.ImportAll();
                WwPreview.RenderBatch();
                WwIcons.RenderAll();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
