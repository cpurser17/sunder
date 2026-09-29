using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Sunder > Create Minion Template Prefab: builds the one shared prefab every
/// minion spawns from, laid out like the existing token prefabs (0.1 scale
/// root with GridAgent + CreatureController, and a flat child SpriteRenderer
/// facing the camera). CreatureController fills in the token, movement and
/// radius from the MinionDefinition at spawn.
///
/// Built in code rather than committed as a file so it links to this
/// project's own script GUIDs. Safe to re-run: an existing template is left
/// alone, and only hooked up to the open scene's MinionSummoner if that
/// doesn't have one yet. Customise the prefab freely afterwards.
/// </summary>
public static class MinionTemplateBuilder
{
    private const string TemplatePath = "Assets/Prefabs/MinionTemplate.prefab";

    [MenuItem("Sunder/Create Minion Template Prefab", priority = 20)]
    public static void Create()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);
        bool created = prefab == null;

        if (created)
        {
            var root = new GameObject("MinionTemplate");
            root.transform.localScale = Vector3.one * 0.1f;
            root.AddComponent<CreatureController>(); // RequireComponent adds GridAgent

            var token = new GameObject("Token");
            token.transform.SetParent(root.transform, false);
            token.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var sprite = token.AddComponent<SpriteRenderer>();
            sprite.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sprite.receiveShadows    = false;

            prefab = PrefabUtility.SaveAsPrefabAsset(root, TemplatePath);
            Object.DestroyImmediate(root);
        }

        string hookedUp = HookUpSummoner(prefab);
        EditorGUIUtility.PingObject(prefab);
        EditorUtility.DisplayDialog("Minion Template",
            (created ? $"Created {TemplatePath}." : $"{TemplatePath} already exists — left unchanged.") + "\n\n" + hookedUp, "OK");
    }

    private static string HookUpSummoner(GameObject prefab)
    {
        var summoner = Object.FindAnyObjectByType<MinionSummoner>(FindObjectsInactive.Include);
        if (summoner == null)
            return "No MinionSummoner in the open scene — assign the template to its Minion Template field by hand.";

        var so   = new SerializedObject(summoner);
        var prop = so.FindProperty("minionTemplate");
        if (prop.objectReferenceValue != null)
            return $"The scene's MinionSummoner already uses {prop.objectReferenceValue.name} — left unchanged.";

        prop.objectReferenceValue = prefab;
        so.ApplyModifiedProperties();
        EditorSceneManager.MarkSceneDirty(summoner.gameObject.scene);
        return "Assigned it to the scene's MinionSummoner — save the scene to keep it.";
    }
}
