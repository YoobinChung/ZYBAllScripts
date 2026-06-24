using System;
using UnityEditor;
using UnityEngine;

namespace ToonyColorsPro
{
    namespace CustomShaderImporter
    {
        [InitializeOnLoad]
        public static class TCP2_ShaderPipelineSync
        {
            static bool syncScheduled;
            static bool syncRunning;

            static TCP2_ShaderPipelineSync()
            {
                ScheduleSync();
                EditorApplication.projectChanged += ScheduleSync;
            }

            public static void ScheduleSync()
            {
                if (syncScheduled)
                {
                    return;
                }

                syncScheduled = true;
                EditorApplication.delayCall += SyncImportedShadersWithCurrentPipeline;
            }

            static void SyncImportedShadersWithCurrentPipeline()
            {
                syncScheduled = false;
                if (syncRunning)
                {
                    return;
                }

                syncRunning = true;
                try
                {
                    bool isUsingURP = TCP2_ShaderImporter.IsUsingURP();
                    string expectedPipeline = isUsingURP ? "Universal Render Pipeline" : "Built-In Render Pipeline";

                    foreach (var assetPath in AssetDatabase.GetAllAssetPaths())
                    {
                        if (!assetPath.EndsWith(TCP2_ShaderImporter.FILE_EXTENSION, StringComparison.InvariantCultureIgnoreCase))
                        {
                            continue;
                        }

                        var importer = AssetImporter.GetAtPath(assetPath) as TCP2_ShaderImporter;
                        if (importer == null || importer.detectedRenderPipeline == expectedPipeline)
                        {
                            continue;
                        }

                        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    }
                }
                finally
                {
                    syncRunning = false;
                }
            }
        }

        public class TCP2_ShaderPostProcessor : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
            {
                CleanTCP2Shaders(importedAssets);
                TCP2_ShaderPipelineSync.ScheduleSync();
            }

            static void CleanTCP2Shaders(string[] paths)
            {
                foreach (var assetPath in paths)
                {
                    if (!assetPath.EndsWith(TCP2_ShaderImporter.FILE_EXTENSION, StringComparison.InvariantCultureIgnoreCase))
                    {
                        continue;
                    }

                    var shader = AssetDatabase.LoadMainAssetAtPath(assetPath) as Shader;
                    if (shader != null)
                    {
                        ShaderUtil.ClearShaderMessages(shader);
                        if (!ShaderUtil.ShaderHasError(shader))
                        {
                            ShaderUtil.RegisterShader(shader);
                        }
                    }
                }
            }
        }
    }
}
