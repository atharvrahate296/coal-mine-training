using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;

public class BackendSync : MonoBehaviour
{
    public static BackendSync Instance;

    [Header("Backend Settings")]
    // NOTE: When testing on your phone, change localhost to your PC's Wi-Fi IP Address (e.g., http://192.168.1.5:3000/api/sessions)
    public string backendUrl = "http://localhost:3000/api/sessions";

    [System.Serializable]
    public class SessionData
    {
        public string traineeId;
        public string mode;
        public int score;
        public int totalAnswered;
        public string deviceModel;
    }

    void Awake()
    {
        // Keep this script alive across scenes
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    public void SaveAndSyncScore(int finalScore, int totalQuestions)
    {
        // Create the data payload
        SessionData data = new SessionData
        {
            traineeId = "EMP-" + Random.Range(1000, 9999).ToString(), // Mock Employee ID for now
            mode = "VR",
            score = finalScore,
            totalAnswered = totalQuestions,
            deviceModel = SystemInfo.deviceModel
        };

        string jsonData = JsonUtility.ToJson(data);

        // 1. OFFLINE FIRST: Save locally to the device
        string offlinePath = Application.persistentDataPath + "/offline_scores.json";
        File.AppendAllText(offlinePath, jsonData + "\n");
        Debug.Log("💾 Score saved offline to: " + offlinePath);

        // 2. Try to sync to the Trainer Dashboard Backend
        StartCoroutine(PostToBackend(jsonData));
    }

    IEnumerator PostToBackend(string jsonData)
    {
        UnityWebRequest request = new UnityWebRequest(backendUrl, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            Debug.Log("✅ Successfully synced score to the Trainer Dashboard!");
        }
        else
        {
            Debug.LogWarning("⚠️ Offline mode active: Could not reach backend right now. Error: " + request.error);
        }
    }
}