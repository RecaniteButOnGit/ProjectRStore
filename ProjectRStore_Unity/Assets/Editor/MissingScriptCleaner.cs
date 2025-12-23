// Assets/Editor/MissingScriptCleaner.cs
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class MissingScriptCleaner
{
    [MenuItem("Tools/Cleanup/Remove Missing Scripts + Delete NavPoint(s)/All Prefabs In Project")]
    public static void CleanAllPrefabsInProject()
    {
        var guids = AssetDatabase.FindAssets("t:Prefab");
        var paths = new List<string>(guids.Length);
        foreach (var g in guids)
            paths.Add(AssetDatabase.GUIDToAssetPath(g));

        CleanPrefabs(paths);
    }

    [MenuItem("Tools/Cleanup/Remove Missing Scripts + Delete NavPoint(s)/Selected Prefabs (and folders)")]
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
            EditorUtility.DisplayDialog("Cleaner",
                "No prefabs selected (select prefab assets or folders in the Project window).", "OK");
            return;
        }

        CleanPrefabs(paths);
    }

    private static void CleanPrefabs(List<string> prefabPaths)
    {
        var set = new HashSet<string>(prefabPaths);
        var paths = new List<string>(set);
        paths.Sort();

        int prefabsChanged = 0;
        int totalMissingScriptsRemoved = 0;
        int totalNavPointDeleted = 0;
        int totalNavPointsGroupDeleted = 0;

        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                EditorUtility.DisplayProgressBar(
                    "Cleaning Prefabs",
                    $"{i + 1}/{paths.Count}  {Path.GetFileName(path)}",
                    (float)i / Mathf.Max(1, paths.Count)
                );

                int removedMissing;
                int deletedNavPoint;
                int deletedNavPointsGroup;
                bool changed = CleanPrefabAtPath(path, out removedMissing, out deletedNavPoint, out deletedNavPointsGroup);

                if (changed)
                {
                    prefabsChanged++;
                    totalMissingScriptsRemoved += removedMissing;
                    totalNavPointDeleted += deletedNavPoint;
                    totalNavPointsGroupDeleted += deletedNavPointsGroup;
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
            "Cleaner",
            $"Done.\n\nPrefabs scanned: {paths.Count}\nPrefabs changed: {prefabsChanged}\n" +
            $"Missing scripts removed: {totalMissingScriptsRemoved}\n" +
            $"NavPoint objects deleted: {totalNavPointDeleted}\n" +
            $"NavPoints groups deleted: {totalNavPointsGroupDeleted}",
            "OK"
        );

        Debug.Log($"[MissingScriptCleaner] Scanned {paths.Count} prefabs. Changed {prefabsChanged}. " +
                  $"Removed {totalMissingScriptsRemoved} missing scripts. " +
                  $"Deleted {totalNavPointDeleted} NavPoint objects. Deleted {totalNavPointsGroupDeleted} NavPoints groups.");
    }

    private static bool CleanPrefabAtPath(
        string prefabPath,
        out int removedMissingScripts,
        out int deletedNavPoint,
        out int deletedNavPointsGroup)
    {
        removedMissingScripts = 0;
        deletedNavPoint = 0;
        deletedNavPointsGroup = 0;

        var root = PrefabUtility.LoadPrefabContents(prefabPath);
        if (!root) return false;

        bool changed = false;

        try
        {
            // Snapshot all transforms before we start deleting stuff.
            var transforms = root.GetComponentsInChildren<Transform>(true);

            // 1) Remove missing scripts
            foreach (var t in transforms)
            {
                if (!t) continue;
                int r = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                if (r > 0)
                {
                    removedMissingScripts += r;
                    changed = true;
                }
            }

            // 2) Delete NavPoints groups (delete parent => children go too)
            //    Deepest-first to avoid iterator invalidation.
            for (int i = transforms.Length - 1; i >= 0; i--)
            {
                var t = transforms[i];
                if (!t) continue;
                if (t.gameObject == root) continue;

                if (t.name == "NavPoints")
                {
                    // Explicitly delete children first (mostly ceremonial, but matches your request)
                    for (int c = t.childCount - 1; c >= 0; c--)
                        Object.DestroyImmediate(t.GetChild(c).gameObject);

                    Object.DestroyImmediate(t.gameObject);
                    deletedNavPointsGroup++;
                    changed = true;
                }
            }

            // Refresh snapshot after deletions
            transforms = root.GetComponentsInChildren<Transform>(true);

            // 3) Delete single NavPoint objects
            for (int i = transforms.Length - 1; i >= 0; i--)
            {
                var t = transforms[i];
                if (!t) continue;
                if (t.gameObject == root) continue;

                if (t.name == "NavPoint")
                {
                    Object.DestroyImmediate(t.gameObject);
                    deletedNavPoint++;
                    changed = true;
                }
            }

            if (changed)
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        return changed;
    }
}
