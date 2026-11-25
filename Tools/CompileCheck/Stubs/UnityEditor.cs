// Stand-ins for the Unity editor API — compile check only.
using System;
using UnityEngine;

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class MenuItemAttribute : Attribute
    {
        public int priority;
        public MenuItemAttribute(string itemName) { }
        public MenuItemAttribute(string itemName, bool isValidateFunction) { }
        public MenuItemAttribute(string itemName, bool isValidateFunction, int priority) { }
    }

    public static class AssetDatabase
    {
        public static void CreateAsset(UnityEngine.Object asset, string path) { }
        public static string CreateFolder(string parentFolder, string newFolderName) => "";
        public static string[] FindAssets(string filter) => new string[0];
        public static string[] FindAssets(string filter, string[] searchInFolders) => new string[0];
        public static string GUIDToAssetPath(string guid) => "";
        public static string GenerateUniqueAssetPath(string path) => path;
        public static string GetAssetPath(UnityEngine.Object asset) => "";
        public static T GetBuiltinExtraResource<T>(string path) where T : UnityEngine.Object => null;
        public static bool IsValidFolder(string path) => false;
        public static string[] GetSubFolders(string path) => new string[0];
        public static T LoadAssetAtPath<T>(string path) where T : UnityEngine.Object => null;
        public static UnityEngine.Object LoadAssetAtPath(string path, Type type) => null;
        public static void SaveAssets() { }
        public static void Refresh() { }
        public static void StartAssetEditing() { }
        public static void StopAssetEditing() { }
        public static void ImportAsset(string path) { }
    }

    public static class EditorUtility
    {
        public static void ClearProgressBar() { }
        public static bool DisplayDialog(string title, string message, string ok) => true;
        public static bool DisplayDialog(string title, string message, string ok, string cancel) => true;
        public static bool DisplayProgressBar(string title, string info, float progress) => false;
        public static string OpenFilePanel(string title, string directory, string extension) => "";
        public static void SetDirty(UnityEngine.Object target) { }
    }

    public class AssetImporter : UnityEngine.Object
    {
        public static AssetImporter GetAtPath(string path) => null;
        public string assetPath => "";
        public void SaveAndReimport() { }
    }

    public static class EditorPrefs
    {
        public static string GetString(string key) => "";
        public static string GetString(string key, string defaultValue) => defaultValue;
        public static void SetString(string key, string value) { }
    }

    public static class PrefabUtility
    {
        public static GameObject LoadPrefabContents(string assetPath) => null;
        public static GameObject SaveAsPrefabAsset(GameObject instanceRoot, string assetPath) => null;
        public static GameObject SaveAsPrefabAsset(GameObject instanceRoot, string assetPath, out bool success) { success = true; return null; }
        public static void UnloadPrefabContents(GameObject contentsRoot) { }
    }

    public static class Selection
    {
        public static GameObject activeGameObject { get; set; }
        public static UnityEngine.Object activeObject { get; set; }
    }

    public static class Undo
    {
        public static void RegisterCreatedObjectUndo(UnityEngine.Object objectToUndo, string name) { }
    }
}

namespace UnityEditor.SceneManagement
{
    public static class EditorSceneManager
    {
        public static bool MarkSceneDirty(UnityEngine.SceneManagement.Scene scene) => true;
    }
}

namespace UnityEditor
{
    public class SerializedObject
    {
        public SerializedObject(UnityEngine.Object obj) { }
        public SerializedProperty FindProperty(string path) => null;
        public bool ApplyModifiedProperties() => true;
        public bool ApplyModifiedPropertiesWithoutUndo() => true;
        public void Update() { }
    }

    public class SerializedProperty
    {
        public int arraySize { get; set; }
        public UnityEngine.Object objectReferenceValue { get; set; }
        public int intValue { get; set; }
        public float floatValue { get; set; }
        public bool boolValue { get; set; }
        public string stringValue { get; set; }
        public int enumValueIndex { get; set; }
        public Vector2 vector2Value { get; set; }
        public Color colorValue { get; set; }
        public SerializedProperty GetArrayElementAtIndex(int index) => null;
        public void InsertArrayElementAtIndex(int index) { }
        public void DeleteArrayElementAtIndex(int index) { }
        public void ClearArray() { }
        public SerializedProperty FindPropertyRelative(string path) => null;
    }

    public static class EditorGUIUtility
    {
        public static void PingObject(UnityEngine.Object obj) { }
    }

    public static class GameObjectUtility
    {
        public static int RemoveMonoBehavioursWithMissingScript(GameObject go) => 0;
    }

    public enum TextureImporterType { Default, NormalMap, GUI, Sprite, Cursor, Cookie, Lightmap, SingleChannel }
    public enum SpriteImportMode { None, Single, Multiple, Polygon }

    public class TextureImporter : AssetImporter
    {
        public TextureImporterType textureType { get; set; }
        public SpriteImportMode spriteImportMode { get; set; }
        public float spritePixelsPerUnit { get; set; }
    }
}

namespace UnityEditor
{
    public static class EditorApplication
    {
        public static bool isPlaying { get; set; }
        public static bool isPaused { get; set; }
    }
}
