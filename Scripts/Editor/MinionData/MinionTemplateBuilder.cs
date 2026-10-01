using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Sunder > Create Minion Template Prefab: builds (or repairs) the one
/// prefab every minion spawns from — imps and portal creatures alike — and
/// hooks it up to the open scene's spawners.
///
/// The prefab holds no MinionDefinition. It's a blank minion: a 0.1-scale
/// root with GridAgent, MinionController, WorkerBehaviour and
/// CreatureBehaviour, plus a flat "Token" child SpriteRenderer facing the
/// camera. Whatever spawns it calls MinionController.Initialise with the
/// right definition, which fills in the token, movement, stats and behaviour.
///
/// Built in code rather than committed as a file so it links to this
/// project's own script GUIDs. Safe to re-run: an existing template keeps
/// its customisations and only gets missing pieces added and broken
/// (missing) scripts removed.
/// </summary>
public static class MinionTemplateBuilder
{
    private const string TemplatePath = "Assets/Prefabs/MinionTemplate.prefab";

    [MenuItem("Sunder/Create Minion Template Prefab", priority = 20)]
    public static void Create()
    {
        var changes = new List<string>();
        var prefab  = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);

        if (prefab == null)
        {
            var root = new GameObject("MinionTemplate");
            root.transform.localScale = Vector3.one * 0.1f;
            EnsureComponents(root, changes);
            prefab = PrefabUtility.SaveAsPrefabAsset(root, TemplatePath);
            Object.DestroyImmediate(root);
            changes.Clear();
            changes.Add($"Created {TemplatePath}.");
        }
        else
        {
            var root = PrefabUtility.LoadPrefabContents(TemplatePath);
            try
            {
                int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
                foreach (Transform child in root.transform)
                    removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
                if (removed > 0) changes.Add($"Removed {removed} missing script(s) (e.g. the old CreatureController).");

                EnsureComponents(root, changes);
                if (changes.Count > 0) PrefabUtility.SaveAsPrefabAsset(root, TemplatePath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            changes.Insert(0, changes.Count > 0 ? $"Updated {TemplatePath}:" : $"{TemplatePath} was already complete.");
        }

        HookUpScene(prefab, changes);

        EditorGUIUtility.PingObject(prefab);
        EditorUtility.DisplayDialog("Minion Template", string.Join("\n", changes), "OK");
    }

    /// <summary>Adds whatever the template needs and doesn't have yet.</summary>
    private static void EnsureComponents(GameObject root, List<string> changes)
    {
        Ensure<GridAgent>(root, changes);
        Ensure<MinionController>(root, changes);
        Ensure<WorkerBehaviour>(root, changes);
        Ensure<CreatureBehaviour>(root, changes);

        if (root.GetComponentInChildren<SpriteRenderer>(true) == null)
        {
            var token = new GameObject("Token");
            token.transform.SetParent(root.transform, false);
            token.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var sprite = token.AddComponent<SpriteRenderer>();
            sprite.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sprite.receiveShadows    = false;
            changes.Add("  + Token child with a SpriteRenderer");
        }
    }

    private static void Ensure<T>(GameObject root, List<string> changes) where T : Component
    {
        if (root.TryGetComponent<T>(out _)) return;
        root.AddComponent<T>();
        changes.Add($"  + {typeof(T).Name}");
    }

    /// <summary>
    /// Points the open scene's MinionSummoner at the template, and clears every
    /// ImpSpawner's own Imp Prefab so imps use the same template.
    /// </summary>
    private static void HookUpScene(GameObject prefab, List<string> changes)
    {
        changes.Add("");

        var summoner = Object.FindAnyObjectByType<MinionSummoner>(FindObjectsInactive.Include);
        if (summoner == null)
        {
            changes.Add("No MinionSummoner in the open scene — open the game scene and run this again " +
                        "to hook it up, or set MinionSummoner's Minion Template by hand.");
            return;
        }

        bool dirty = false;
        var so   = new SerializedObject(summoner);
        var prop = so.FindProperty("minionTemplate");
        if (prop.objectReferenceValue != prefab)
        {
            string was = prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "nothing";
            prop.objectReferenceValue = prefab;
            so.ApplyModifiedProperties();
            changes.Add($"MinionSummoner now uses the template (was {was}).");
            dirty = true;
        }
        else changes.Add("MinionSummoner already uses the template.");

        foreach (var spawner in Object.FindObjectsByType<ImpSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var sso  = new SerializedObject(spawner);
            var imp  = sso.FindProperty("impPrefab");
            if (imp.objectReferenceValue == null) continue;

            string was = imp.objectReferenceValue.name;
            imp.objectReferenceValue = null;
            sso.ApplyModifiedProperties();
            changes.Add($"{spawner.name}: cleared Imp Prefab (was {was}) so imps use the template.");
            dirty = true;
        }

        if (dirty)
        {
            EditorSceneManager.MarkSceneDirty(summoner.gameObject.scene);
            changes.Add("Save the scene to keep these.");
        }
    }
}
