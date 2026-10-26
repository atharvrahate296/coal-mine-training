using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;
using UnityEngine.UI;

public class ImageTargetHandler : MonoBehaviour
{
    [Header("AR Components")]
    public ARTrackedImageManager trackedImageManager;
    public GameObject infoPanelPrefab;
    public TextMeshProUGUI hudScoreText;

    [Header("AR Hologram Settings (Idea 4)")]
    // The height above the printed image to spawn the info panel or 3D object
    public float yOffset = 0.1f; 

    // Keep track of which images have active panels or holograms
    private Dictionary<string, GameObject> spawnedObjects = new Dictionary<string, GameObject>();

    // V2 Static State (Kept for compatibility with your existing structure)
    public static int score = 0;
    public static int totalAnswered = 0;
    public static HashSet<string> itemsSeen = new HashSet<string>();

    // Hardcoded Item Data (V2 style)
    private class ItemData
    {
        public string title;
        public string info;
        public string question;
        public string optionA;
        public string optionB;
        public bool correctIsA;
        public bool is3DHologram; // NEW: Flag to spawn a 3D object instead of a flat panel
    }

    private Dictionary<string, ItemData> itemDatabase = new Dictionary<string, ItemData>()
    {
        { "HelmetImage", new ItemData { title = "SAFETY HELMET", info = "Protects against falling objects.", question = "When must you wear this?", optionA = "Before entering", optionB = "During blasting", correctIsA = true, is3DHologram = false } },
        { "MaskImage", new ItemData { title = "GAS MASK", info = "Protects against toxic gases.", question = "What must you check before use?", optionA = "Seal and filter", optionB = "Battery level", correctIsA = true, is3DHologram = false } },
        { "DangerSignImage", new ItemData { title = "DANGER SIGN", info = "Toxic gas hazard.", question = "What should you do first?", optionA = "Enter quickly", optionB = "Check gas levels", correctIsA = false, is3DHologram = false } },
        { "RoofSignImage", new ItemData { title = "ROOF WARNING", info = "Risk of rock collapse.", question = "You see loose rock. What next?", optionA = "Ignore and continue", optionB = "Report immediately", correctIsA = false, is3DHologram = false } },
        { "EscapeRouteImage", new ItemData { title = "ESCAPE ROUTE", info = "Marks nearest emergency exit.", question = "When should you learn this route?", optionA = "Before starting", optionB = "In emergency", correctIsA = true, is3DHologram = false } },
        
        // IDEA 4: The Gas Detector will now spawn as a 3D Hologram!
        { "GasDetectorImage", new ItemData { title = "3D GAS DETECTOR", info = "TAP THE 3D MODEL TO INSPECT", question = "The alarm sounds. What do you do?", optionA = "Evacuate immediately", optionB = "Wait and check", correctIsA = true, is3DHologram = true } }
    };

    void OnEnable()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.AddListener(OnTrackablesChanged);
    }

    void OnDisable()
    {
        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
    }

    void Start()
    {
        UpdateHUD();
    }

    void Update()
    {
        // Make sure all spawned panels/holograms face the camera
        Camera cam = Camera.main;
        if (cam != null)
        {
            foreach (var kvp in spawnedObjects)
            {
                if (kvp.Value != null)
                {
                    // If it's a 3D hologram, make it spin for the "X-Ray" effect!
                    if (itemDatabase.ContainsKey(kvp.Key) && itemDatabase[kvp.Key].is3DHologram)
                    {
                         kvp.Value.transform.Rotate(Vector3.up, 45f * Time.deltaTime);
                    }
                    else
                    {
                        // Standard flat 2D panel: billboard it to face the camera
                        kvp.Value.transform.LookAt(cam.transform);
                        kvp.Value.transform.Rotate(0, 180, 0); // Correct backwards text
                    }
                }
            }
        }
        
        // Handle tapping the 3D Hologram
        HandleHologramTaps();
    }

    void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> eventArgs)
    {
        // 1. Image recognized for the first time
        foreach (ARTrackedImage trackedImage in eventArgs.added)
        {
            string key = trackedImage.referenceImage.name;
            
            if (itemDatabase.ContainsKey(key) && !spawnedObjects.ContainsKey(key))
            {
                ItemData data = itemDatabase[key];
                GameObject spawnedObj;

                if (data.is3DHologram)
                {
                    // Spawn a 3D Hologram (A spinning blue cube representing the detector)
                    spawnedObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    spawnedObj.name = "Hologram_" + key;
                    spawnedObj.transform.localScale = new Vector3(0.08f, 0.15f, 0.05f); // Size of a handheld detector
                    
                    // Give it a transparent blue "hologram" look
                    Material holoMat = new Material(Shader.Find("Standard"));
                    holoMat.color = new Color(0.1f, 0.5f, 1f, 0.6f); 
                    holoMat.SetFloat("_Mode", 3);
                    holoMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    holoMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    holoMat.SetInt("_ZWrite", 0);
                    holoMat.DisableKeyword("_ALPHATEST_ON");
                    holoMat.EnableKeyword("_ALPHABLEND_ON");
                    holoMat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    holoMat.renderQueue = 3000;
                    
                    spawnedObj.GetComponent<Renderer>().material = holoMat;
                }
                else
                {
                    // Standard 2D Panel
                    spawnedObj = Instantiate(infoPanelPrefab);
                    SetupPanelUI(spawnedObj, data, key);
                }

                spawnedObj.transform.position = trackedImage.transform.position + new Vector3(0, yOffset, 0);
                spawnedObj.transform.parent = trackedImage.transform;
                spawnedObjects.Add(key, spawnedObj);
            }
        }

        // 2. Image tracking updated (moving the phone around)
        foreach (ARTrackedImage trackedImage in eventArgs.updated)
        {
            string key = trackedImage.referenceImage.name;
            
            if (spawnedObjects.ContainsKey(key))
            {
                GameObject obj = spawnedObjects[key];
                if (trackedImage.trackingState == TrackingState.Tracking)
                {
                    obj.SetActive(true);
                    obj.transform.position = trackedImage.transform.position + new Vector3(0, yOffset, 0);
                }
                else
                {
                    obj.SetActive(false);
                }
            }
        }

        // 3. Image lost
        foreach (var trackedImageEntry in eventArgs.removed)
        {
            ARTrackedImage trackedImage = trackedImageEntry.Value;
            string key = trackedImage.referenceImage.name;
            
            if (spawnedObjects.ContainsKey(key))
            {
                Destroy(spawnedObjects[key]);
                spawnedObjects.Remove(key);
            }
        }
    }

    void HandleHologramTaps()
    {
        if (Input.touchCount > 0)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
            {
                Ray ray = Camera.main.ScreenPointToRay(touch.position);
                RaycastHit hit;
                if (Physics.Raycast(ray, out hit))
                {
                    if (hit.collider.name.StartsWith("Hologram_"))
                    {
                        string key = hit.collider.name.Replace("Hologram_", "");
                        // When they tap the hologram, turn it into the Quiz Panel!
                        if (itemDatabase.ContainsKey(key) && spawnedObjects.ContainsKey(key))
                        {
                            // Destroy the 3D model
                            Destroy(spawnedObjects[key]);
                            
                            // Spawn the Quiz Panel in its place
                            GameObject newPanel = Instantiate(infoPanelPrefab);
                            newPanel.transform.position = hit.point + new Vector3(0, yOffset, 0);
                            newPanel.transform.parent = hit.collider.transform.parent; // Attach to the tracked image
                            
                            SetupPanelUI(newPanel, itemDatabase[key], key);
                            
                            // Immediately show the question since they tapped it
                            ShowQuestionImmediately(newPanel, itemDatabase[key], key);
                            
                            spawnedObjects[key] = newPanel;
                        }
                    }
                }
            }
        }
    }

    void SetupPanelUI(GameObject panel, ItemData data, string key)
    {
        TextMeshProUGUI infoText = GetTextComponent(panel, "InfoText");
        if (infoText != null)
        {
            infoText.text = "<b>" + data.title + "</b>\n" + data.info;
        }

        Button btnA = GetButtonComponent(panel, "AnswerButtonA");
        Button btnB = GetButtonComponent(panel, "AnswerButtonB");
        if (btnA != null) btnA.gameObject.SetActive(false);
        if (btnB != null) btnB.gameObject.SetActive(false);

        // If it's a standard panel (not spawned from a hologram tap), start the delayed quiz
        if (!data.is3DHologram)
        {
            StartCoroutine(DelayedQuizRoutine(panel, data, key));
        }
    }

    System.Collections.IEnumerator DelayedQuizRoutine(GameObject panel, ItemData data, string key)
    {
        yield return new WaitForSeconds(2.5f);
        if (panel != null)
        {
            ShowQuestionImmediately(panel, data, key);
        }
    }

    void ShowQuestionImmediately(GameObject panel, ItemData data, string key)
    {
        TextMeshProUGUI infoText = GetTextComponent(panel, "InfoText");
        if (infoText != null)
        {
            infoText.text = "<b>QUESTION:</b>\n" + data.question;
        }

        Button btnA = GetButtonComponent(panel, "AnswerButtonA");
        Button btnB = GetButtonComponent(panel, "AnswerButtonB");

        if (btnA != null && btnB != null)
        {
            btnA.gameObject.SetActive(true);
            btnB.gameObject.SetActive(true);

            TextMeshProUGUI txtA = btnA.GetComponentInChildren<TextMeshProUGUI>();
            TextMeshProUGUI txtB = btnB.GetComponentInChildren<TextMeshProUGUI>();
            if (txtA != null) txtA.text = "A) " + data.optionA;
            if (txtB != null) txtB.text = "B) " + data.optionB;

            btnA.onClick.RemoveAllListeners();
            btnB.onClick.RemoveAllListeners();

            btnA.onClick.AddListener(() => OnAnswerClicked(panel, data.correctIsA == true, key));
            btnB.onClick.AddListener(() => OnAnswerClicked(panel, data.correctIsA == false, key));
        }
    }

    void OnAnswerClicked(GameObject panel, bool isCorrect, string key)
    {
        Button btnA = GetButtonComponent(panel, "AnswerButtonA");
        Button btnB = GetButtonComponent(panel, "AnswerButtonB");
        if (btnA != null) btnA.gameObject.SetActive(false);
        if (btnB != null) btnB.gameObject.SetActive(false);

        TextMeshProUGUI infoText = GetTextComponent(panel, "InfoText");
        if (infoText != null)
        {
            if (isCorrect)
            {
                infoText.text = "<color=#4ADE80><b>Correct!</b></color>";
                if (!itemsSeen.Contains(key))
                {
                    score++;
                }
            }
            else
            {
                infoText.text = "<color=#F87171><b>Not quite.</b></color> Review this item again.";
            }
        }

        if (!itemsSeen.Contains(key))
        {
            itemsSeen.Add(key);
            totalAnswered++;
            UpdateHUD();
        }
    }

    void UpdateHUD()
    {
        if (hudScoreText != null)
        {
            hudScoreText.text = "Score: " + score + "/" + itemDatabase.Count;
        }
    }

    // Helper functions to find UI elements inside the instantiated prefab
    TextMeshProUGUI GetTextComponent(GameObject parent, string name)
    {
        Transform[] children = parent.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in children)
        {
            if (child.name == name) return child.GetComponent<TextMeshProUGUI>();
        }
        return null;
    }

    Button GetButtonComponent(GameObject parent, string name)
    {
        Transform[] children = parent.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in children)
        {
            if (child.name == name) return child.GetComponent<Button>();
        }
        return null;
    }
}