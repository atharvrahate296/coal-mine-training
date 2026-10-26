using UnityEngine;
using UnityEngine.SceneManagement;

public class MainMenuController : MonoBehaviour
{
    public void OnStartARMode()
    {
        SceneManager.LoadScene("ARMode");
    }

    public void OnVRModeClicked()
    {
        SceneManager.LoadScene("VRMode");
    }
}