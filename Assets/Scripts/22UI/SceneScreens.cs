using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Puts each menu scene's screen in place when it loads, so no scene wiring
/// is needed: the Start scene gets a StartMenu, the Campaign scene a
/// CampaignScreen. Add either to its scene yourself to tune it instead.
/// </summary>
public static class SceneScreens
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == SceneNames.Start && Object.FindAnyObjectByType<StartMenu>() == null)
            new GameObject("StartMenu").AddComponent<StartMenu>();
        else if (scene.name == SceneNames.Campaign && Object.FindAnyObjectByType<CampaignScreen>() == null)
            new GameObject("CampaignScreen").AddComponent<CampaignScreen>();
    }
}
