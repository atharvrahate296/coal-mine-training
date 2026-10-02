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
        Debug.Log("VR Mode is coming soon!");
        // We'll replace this with a proper popup message next
    }
}