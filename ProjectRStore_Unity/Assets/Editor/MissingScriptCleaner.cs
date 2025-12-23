// Assets/Editor/MissingScriptCleaner.cs
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class MissingScriptCleaner
{
    [MenuItem("Tools/Cleanup/Remove Missing Scripts/All Prefabs In Project")]
    public static void CleanAllPrefabsInProject()
    {
        var guids = AssetDatabase.FindAssets("t:Prefab");
        var paths = new List<string>(guids.Length);
        foreach (var g in guids)
            paths.Add(AssetDatabase.GUIDToAssetPath(g));

        CleanPrefabs(paths);
    }

    [MenuItem("Tools/Cleanup/Remove Missing Scripts/Selected Prefabs (and folders)")]
    public static void CleanSelectedPrefabsAndFolders()
    {
        var paths = new List<string>();
        foreach (var obj in Selection.objects)
        {
            if (!obj) continue;

            var path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) continue;

            if (AssetDatabase.IsValidFolder(path))
            {
                var folderGuids = AssetDatabase.FindAssets("t:Prefab", new[] { path });
                foreach (var g in folderGuids)
                    paths.Add(AssetDatabase.GUIDToAssetPath(g));
            }
            else if (path.EndsWith(".prefab"))
            {
                paths.Add(path);
            }
        }

        if (paths.Count == 0)
        {
            EditorUtility.DisplayDialog("Missing Script Cleaner",
                "No prefabs selected (select prefab assets or folders in the Project window).", "OK");
            return;
        }

        CleanPrefabs(paths);
    }

    private static void CleanPrefabs(List<string> prefabPaths)
    {
        // Optional: dedupe + stable order
        var set = new HashSet<string>(prefabPaths);
        var paths = new List<string>(set);
        paths.Sort();

        int totalPrefabsTouched = 0;
        int totalRemoved = 0;

        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                EditorUtility.DisplayProgressBar(
                    "Removing Missing Scripts",
                    $"{i + 1}/{paths.Count}  {Path.GetFileName(path)}",
                    (float)i / Mathf.Max(1, paths.Count)
                );

                int removed = CleanPrefabAtPath(path);
                if (removed > 0)
                {
                    totalPrefabsTouched++;
                    totalRemoved += removed;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        EditorUtility.DisplayDialog(
            "Missing Script Cleaner",
            $"Done.\n\nPrefabs scanned: {paths.Count}\nPrefabs changed: {totalPrefabsTouched}\nMissing scripts removed: {totalRemoved}",
            "OK"
        );

        Debug.Log($"[MissingScriptCleaner] Scanned {paths.Count} prefabs. Changed {totalPrefabsTouched}. Removed {totalRemoved} missing scripts.");
    }

    private static int CleanPrefabAtPath(string prefabPath)
    {
        // Load prefab contents in isolation (safe way to edit prefab assets)
        var root = PrefabUtility.LoadPrefabContents(prefabPath);
        if (!root) return 0;

        int removedCount = 0;

        try
        {
            // Clean root + all children
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
            {
                if (!t) continue;
                removedCount += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            }

            if (removedCount > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        return removedCount;
    }
}
