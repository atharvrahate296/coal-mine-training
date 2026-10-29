// =====================================================================
//  ImageTargetHandler.cs - INDUSTRIAL AR SCANNER (ADVANCED EDITION)
//
//  FIXES
//   - CS0030 (line 175): in AR Foundation 6 `eventArgs.removed` is a list of
//     KeyValuePair<TrackableId, ARTrackedImage>, not ARTrackedImage. Removed
//     targets are now resolved through their TrackableId (the image object
//     may already be destroyed, so we never read from it).
//   - Legacy Input.touchCount replaced by the new Input System (the old
//     API throws when the project only uses the Input System package).
//   - HUD buttons were anchored to the centre but positioned like top
//     corners, so they landed in the wrong place. All HUD is anchored now.
//   - The reticle was a solid yellow square covering the camera view. It is
//     now four corner brackets + a scan line.
//   - After tapping the gas-detector hologram the new quiz panel kept
//     spinning (it inherited the "hologram" flag). State is now per object.
//   - Re-scanning an already answered item asked the quiz again.
//   - Unknown reference-image names are reported on screen, so a mis-named
//     entry in the Reference Image Library is obvious.
//
//  NEW
//   - Smooth billboarding + pop-in animation, lock-on chirps and haptics
//   - Animated 3D gas detector: live CH4 / CO / O2 LCD, blinking LED
//   - Gear checklist with per-item status (not seen / seen / OK / wrong)
//   - Progress bar, score chip, toasts, completion screen with rating
//   - Shuffled answers, explanations, quiz countdown, Android back key
//
//  Public fields and static members are unchanged, so your scene and any
//  other scripts keep working. Needs: ARTrackedImageManager, an info-panel
//  prefab (Canvas > Panel > InfoText, AnswerButtonA, AnswerButtonB) and a
//  "MainMenu" scene in Build Profiles.
// =====================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;

public class ImageTargetHandler : MonoBehaviour
{
    // ------------------------------------------------------------------
    //  INSPECTOR (unchanged names)
    // ------------------------------------------------------------------
    [Header("AR Core References")]
    public ARTrackedImageManager trackedImageManager;
    public GameObject infoPanelPrefab;
    public TextMeshProUGUI hudScoreText;

    [Header("Configuration")]
    public float yOffset = 0.12f;
    public string mainMenuSceneName = "MainMenu";

    [Header("Quiz")]
    [SerializeField] private float quizDelay = 2.5f;
    [SerializeField] private bool shuffleAnswers = true;
    [SerializeField] private bool resetSessionOnStart = true;
    [Tooltip("Small debug readout (top-left). Turn off once everything works.")]
    [SerializeField] private bool showDiagnostics = true;

    // ------------------------------------------------------------------
    //  SESSION (static: unchanged)
    // ------------------------------------------------------------------
    public static int score = 0;
    public static int totalAnswered = 0;
    public static HashSet<string> itemsSeen = new HashSet<string>();
    private static readonly Dictionary<string, bool> results = new Dictionary<string, bool>();

    // ------------------------------------------------------------------
    //  DATA
    // ------------------------------------------------------------------
    private class ItemData
    {
        public string key, title, info, question, optionA, optionB, explanation;
        public bool correctIsA, is3DHologram;
    }

    private class Spawned
    {
        public GameObject go;
        public Transform anchor;
        public bool isHolo;
        public bool wasActive = true;
        public float born;
        public Vector3 baseScale = Vector3.one;
        public TextMeshPro lcd;
        public Material ledMat;
        public float nextLcdUpdate;
    }

    private readonly Dictionary<string, ItemData> itemDatabase = new Dictionary<string, ItemData>();
    private readonly List<string> itemOrder = new List<string>();
    private readonly Dictionary<string, Spawned> spawned = new Dictionary<string, Spawned>();
    private readonly Dictionary<TrackableId, string> idToKey = new Dictionary<TrackableId, string>();
    private readonly HashSet<string> detectedKeys = new HashSet<string>();
    private readonly HashSet<string> warnedUnknown = new HashSet<string>();
    private string lastAcquiredKey;
    private bool gasCalibrated;
    private bool subscribed;
    private float nextSync, nextDiag;
    private int eventCount;
    private TextMeshProUGUI diagText;

    // HUD
    private GameObject hudCanvasObj;
    private TextMeshProUGUI statusBannerText, toastText, menuLabel, gearLabel, progressLabel, drawerProgressLabel, scoreLabel;
    private RectTransform progressFill, drawerProgressFill, scanLineRT, reticleRT;
    private readonly List<Image> reticleParts = new List<Image>();
    private Color reticleColor = new Color(1f, 0.8f, 0.2f, 0.55f);
    private GameObject checklistPanel, completionPanel;
    private TextMeshProUGUI completionScoreText, completionMessageText;
    private readonly Dictionary<string, TextMeshProUGUI> checklistRows = new Dictionary<string, TextMeshProUGUI>();
    private string bannerKeyShown = "#";
    private float toastTimer, lastBackPress = -10f;
    private bool completionShown, sessionSaved;
    private readonly List<RaycastResult> uiHits = new List<RaycastResult>();

    // Audio
    private AudioSource audioSource;
    private AudioClip lockOnClip, tapClip, correctClip, wrongClip;

    // ------------------------------------------------------------------
    //  LIFECYCLE
    // ------------------------------------------------------------------
    void Awake()
    {
        BuildDatabase();
        SetupProceduralAudio();
        if (resetSessionOnStart)
        {
            score = 0;
            totalAnswered = 0;
            itemsSeen.Clear();
            results.Clear();
        }
    }

    void OnEnable()
    {
        Subscribe();
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    // The manager may be unassigned in the Inspector. Start() finds it and calls Subscribe() again,
    // so detection works either way (previously an unassigned field meant no events ever arrived).
    void Subscribe()
    {
        if (subscribed || trackedImageManager == null) return;
        trackedImageManager.trackablesChanged.AddListener(OnTrackablesChanged);
        subscribed = true;
    }

    void Unsubscribe()
    {
        if (!subscribed || trackedImageManager == null) return;
        trackedImageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
        subscribed = false;
    }

    void Start()
    {
        if (trackedImageManager == null) trackedImageManager = FindAnyObjectByType<ARTrackedImageManager>();
        if (trackedImageManager == null) Debug.LogError("ImageTargetHandler: no ARTrackedImageManager found in the scene.");
        else Subscribe();
        if (infoPanelPrefab == null) Debug.LogWarning("ImageTargetHandler: Info Panel Prefab not assigned - using the built-in panel.");

        // The scene's old ScoreText ("New Text") is replaced by the HUD score chip.
        if (hudScoreText == null)
        {
            GameObject st = GameObject.Find("ScoreText");
            if (st != null) hudScoreText = st.GetComponent<TextMeshProUGUI>();
        }
        if (hudScoreText != null) hudScoreText.gameObject.SetActive(false);
        if (FindAnyObjectByType<EventSystem>() == null)
        {
            GameObject es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }

        BuildScannerHUD();
        RefreshHudStats();
    }

    void Update()
    {
        Camera cam = Camera.main;
        AnimateSpawned(cam);
        AnimateHud(Time.deltaTime);

        if (Time.time >= nextSync)                                 // safety net: poll the manager too, not only events
        {
            nextSync = Time.time + 0.25f;
            SyncFromTrackables();
        }
        UpdateDiagnostics();
        HandleTap(cam);
        HandleBackKey();
    }

    // ------------------------------------------------------------------
    //  CONTENT
    // ------------------------------------------------------------------
    void Add(string key, string title, string info, string question, string a, string b, bool correctIsA, string explanation, bool holo = false)
    {
        itemDatabase[key] = new ItemData
        {
            key = key, title = title, info = info, question = question, optionA = a, optionB = b,
            correctIsA = correctIsA, explanation = explanation, is3DHologram = holo
        };
        itemOrder.Add(key);
    }

    void BuildDatabase()
    {
        // Keys must match the names in your XR Reference Image Library exactly.
        Add("HelmetImage", "SAFETY HELMET", "Protects against rockfalls and head impact.",
            "When must you wear this underground?", "At all times", "Only during blasting", true,
            "Falling rock gives no warning. The helmet stays on from entry to exit.");
        Add("MaskImage", "GAS MASK / RESPIRATOR", "Supplies breathable air during toxic atmospheric events.",
            "What must you inspect before shift entry?", "Airtight seal and filter", "Strap color", true,
            "A leaking seal or a clogged filter gives no protection. Check it every shift.");
        Add("DangerSignImage", "DANGER SIGN", "Indicates immediately hazardous or unventilated zones.",
            "A red DANGER sign blocks a split. Action?", "Enter quickly", "Stop and notify supervisor", false,
            "Danger signs mark zones with an immediate risk. Never enter without authorisation.");
        Add("RoofSignImage", "ROOF COLLAPSE WARNING", "Warns of geological instability and loose strata.",
            "You observe loose roof strata. Action?", "Report immediately & retreat", "Continue under caution", true,
            "Loose strata can fail without warning. Retreat to supported ground and report it.");
        Add("EscapeRouteImage", "EMERGENCY ESCAPE ROUTE", "Marks the intake airway escape passage to surface.",
            "When should the escape path be memorized?", "Before every shift", "Only during an emergency", true,
            "Smoke or gas can remove visibility. Knowing the route in advance saves seconds.");
        Add("GasDetectorImage", "MULTI-GAS DETECTOR", "Measures CH4, CO, and O2 levels. Tap the detector to calibrate.",
            "Methane alarm exceeds 1.25%. What is mandatory?", "Evacuate and cut power", "Reset alarm and continue", true,
            "Methane nears its explosive range. Cut power, evacuate and report. Never reset and carry on.", true);
    }

    // ------------------------------------------------------------------
    //  AR TRACKING EVENTS
    // ------------------------------------------------------------------
    void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
    {
        // 1 + 2. Newly detected and updated images share one code path
        foreach (ARTrackedImage img in args.added) { eventCount++; ProcessTrackedImage(img); }
        foreach (ARTrackedImage img in args.updated) { eventCount++; ProcessTrackedImage(img); }

        // 3. Removed. Value may already be destroyed, so resolve through the TrackableId.
        foreach (KeyValuePair<TrackableId, ARTrackedImage> removed in args.removed)
        {
            string key;
            if (idToKey.TryGetValue(removed.Key, out key))
            {
                if (key != null) DestroySpawned(key);
                idToKey.Remove(removed.Key);
            }
        }
    }

    // Resolves the reference-image name to one of our keys. Exact match first, then a forgiving
    // match so names like "Methanegasdetectorlmage", "EscaperouteImage" or "FragileImage" still work.
    string ResolveKey(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (itemDatabase.ContainsKey(raw)) return raw;
        string n = raw.ToLowerInvariant();
        if (n.Contains("detector") || n.Contains("methane")) return "GasDetectorImage";
        if (n.Contains("helmet")) return "HelmetImage";
        if (n.Contains("mask") || n.Contains("respirator")) return "MaskImage";
        if (n.Contains("danger")) return "DangerSignImage";
        if (n.Contains("roof") || n.Contains("fragile")) return "RoofSignImage";
        if (n.Contains("escape") || n.Contains("exit")) return "EscapeRouteImage";
        return null;
    }

    void ProcessTrackedImage(ARTrackedImage img)
    {
        string key;
        if (!idToKey.TryGetValue(img.trackableId, out key))
        {
            string raw = img.referenceImage.name;
            key = ResolveKey(raw);
            idToKey[img.trackableId] = key;                        // null = unknown, cached so we warn once
            if (key == null && warnedUnknown.Add(raw))
            {
                Debug.LogWarning("Unknown reference image '" + raw + "'. Rename it in the XR Reference Image Library.");
                ShowToast("Unknown image '" + raw + "' - rename it in the library", 4f);
            }
        }
        if (key == null) return;

        bool tracking = img.trackingState == TrackingState.Tracking;
        Spawned s;
        if (!spawned.TryGetValue(key, out s) || s.go == null)
        {
            if (s != null) spawned.Remove(key);                    // its GameObject was destroyed
            if (tracking) SpawnFor(img, key);
            return;
        }

        s.go.SetActive(tracking);                                  // hide while the image is not actively tracked
        if (tracking && !s.wasActive) OnTargetAcquired(key, true);
        s.wasActive = tracking;
    }

    void SyncFromTrackables()
    {
        if (trackedImageManager == null || !trackedImageManager.enabled) return;
        foreach (ARTrackedImage img in trackedImageManager.trackables) ProcessTrackedImage(img);
    }

    void UpdateDiagnostics()
    {
        if (diagText == null || Time.time < nextDiag) return;
        nextDiag = Time.time + 0.5f;
        if (!showDiagnostics) { diagText.text = ""; return; }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("AR session: ").Append(ARSession.state);
        sb.Append("\nManager: ").Append(trackedImageManager == null ? "MISSING" : (trackedImageManager.enabled ? "on" : "DISABLED"));
        if (trackedImageManager != null)
        {
            int lib = trackedImageManager.referenceLibrary != null ? trackedImageManager.referenceLibrary.count : -1;
            sb.Append("  Library: ").Append(lib < 0 ? "NONE" : lib.ToString());
        }
        sb.Append("  Events: ").Append(eventCount).Append(subscribed ? "" : "  (NOT subscribed)");
        sb.Append("\nPanel prefab: ").Append(infoPanelPrefab != null ? "assigned" : "built-in");
        if (trackedImageManager != null)
        {
            foreach (ARTrackedImage img in trackedImageManager.trackables)
            {
                string k;
                idToKey.TryGetValue(img.trackableId, out k);
                sb.Append("\n> ").Append(img.referenceImage.name).Append(" [").Append(img.trackingState).Append("] = ").Append(k ?? "UNKNOWN");
            }
        }
        diagText.text = sb.ToString();
    }

    // Built-in world-space info panel, used when no prefab is assigned. Child names match what the
    // quiz code looks for (InfoText, AnswerButtonA, AnswerButtonB).
    GameObject BuildFallbackPanel()
    {
        GameObject root = new GameObject("InfoPanel_BuiltIn");
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        root.AddComponent<GraphicRaycaster>();
        RectTransform rt = root.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(720f, 480f);
        rt.localScale = Vector3.one * 0.0004f;                     // 720 units -> 0.29 m wide

        Vector2 C = new Vector2(0.5f, 0.5f);
        NewImage("Panel", root.transform, C, Vector2.zero, new Vector2(720f, 480f), new Color(0.05f, 0.08f, 0.12f, 0.95f), true);
        NewImage("Accent", root.transform, C, new Vector2(0f, 235f), new Vector2(720f, 10f), new Color(0.98f, 0.8f, 0.08f, 1f));
        TextMeshProUGUI info = NewText(root.transform, "", 34, Color.white, TextAlignmentOptions.Center, C, new Vector2(0f, 100f), new Vector2(660f, 240f));
        info.gameObject.name = "InfoText";
        Color navy = new Color(0.12f, 0.28f, 0.45f, 1f);
        Button a = NewButton(root.transform, "AnswerButtonA", C, new Vector2(0f, -80f), new Vector2(640f, 80f), "A", navy, () => { });
        Button b = NewButton(root.transform, "AnswerButtonB", C, new Vector2(0f, -175f), new Vector2(640f, 80f), "B", navy, () => { });
        a.GetComponentInChildren<TextMeshProUGUI>().fontSize = 26f;
        b.GetComponentInChildren<TextMeshProUGUI>().fontSize = 26f;
        a.gameObject.SetActive(false);
        b.gameObject.SetActive(false);
        return root;
    }

    void SpawnFor(ARTrackedImage img, string key)
    {
        ItemData data = itemDatabase[key];
        Spawned s = new Spawned { anchor = img.transform, isHolo = data.is3DHologram, born = Time.time };

        if (data.is3DHologram)
        {
            s.go = BuildCompositeGasDetector("Hologram_" + key, s);
        }
        else
        {
            s.go = infoPanelPrefab != null ? Instantiate(infoPanelPrefab) : BuildFallbackPanel();
            PreparePanel(s.go, data, key, false);
        }

        s.go.transform.position = img.transform.position + new Vector3(0f, yOffset, 0f);
        s.go.transform.SetParent(img.transform, true);
        s.baseScale = s.go.transform.localScale;
        s.go.transform.localScale = Vector3.zero;                // pop-in animation grows it
        s.go.SetActive(img.trackingState == TrackingState.Tracking);
        spawned[key] = s;

        detectedKeys.Add(key);
        OnTargetAcquired(key, false);
        RefreshHudStats();
    }

    void DestroySpawned(string key)
    {
        Spawned s;
        if (!spawned.TryGetValue(key, out s)) return;
        if (s.go != null) Destroy(s.go);
        spawned.Remove(key);
    }

    void OnTargetAcquired(string key, bool reacquired)
    {
        lastAcquiredKey = key;
        PlayAudio(lockOnClip, reacquired ? 0.4f : 0.7f);
        if (!reacquired) TriggerVibration();
    }

    // ------------------------------------------------------------------
    //  PER-FRAME ANIMATION
    // ------------------------------------------------------------------
    static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    void AnimateSpawned(Camera cam)
    {
        if (cam == null) return;
        float follow = 1f - Mathf.Exp(-10f * Time.deltaTime);

        foreach (KeyValuePair<string, Spawned> kvp in spawned)
        {
            Spawned s = kvp.Value;
            if (s.go == null || !s.go.activeInHierarchy) continue;
            Transform tr = s.go.transform;

            // Billboard: the object's -Z side faces the camera (same as the old LookAt + Rotate(180)).
            Vector3 away = tr.position - cam.transform.position;
            if (away.sqrMagnitude > 0.0001f)
            {
                Quaternion face = Quaternion.LookRotation(away, Vector3.up);
                if (s.isHolo) face *= Quaternion.Euler(0f, Mathf.Sin(Time.time * 0.9f) * 40f, 0f);   // gentle sway keeps the LCD readable
                tr.rotation = Quaternion.Slerp(tr.rotation, face, follow);
            }

            tr.localScale = s.baseScale * EaseOutBack(Mathf.Clamp01((Time.time - s.born) / 0.35f));

            if (s.isHolo) AnimateGasDetector(s);
        }
    }

    void AnimateGasDetector(Spawned s)
    {
        float t = Time.time;
        if (s.ledMat != null)
        {
            bool on = Mathf.Sin(t * 5f) > -0.2f;
            Color led = gasCalibrated ? new Color(0.2f, 1f, 0.4f) : new Color(1f, 0.55f, 0.1f);
            s.ledMat.SetColor("_EmissionColor", led * (on ? 2f : 0.25f));
            s.ledMat.color = led;
            if (s.ledMat.HasProperty("_BaseColor")) s.ledMat.SetColor("_BaseColor", led);
        }
        if (s.lcd != null && t >= s.nextLcdUpdate)
        {
            s.nextLcdUpdate = t + 0.15f;                             // 6-7 Hz is plenty and avoids garbage every frame
            float noise = Mathf.PerlinNoise(t * 1.3f, 0.5f) - 0.5f;
            float ch4 = 0.32f + 0.10f * Mathf.Sin(t * 0.7f) + noise * (gasCalibrated ? 0.01f : 0.08f);
            float co = 6f + 3f * Mathf.Sin(t * 0.5f) + noise * 4f;
            float o2 = 20.9f + noise * 0.2f;
            s.lcd.text = "CH4 " + ch4.ToString("0.00") + "%\nCO  " + co.ToString("0") + "ppm\nO2  " + o2.ToString("0.0") + "%";
        }
    }

    // ------------------------------------------------------------------
    //  3D COMPOSITE GAS DETECTOR
    // ------------------------------------------------------------------
    GameObject BuildCompositeGasDetector(string rootName, Spawned s)
    {
        GameObject root = new GameObject(rootName);

        GameObject body = Part(root, "Body", PrimitiveType.Cube, Vector3.zero, new Vector3(0.08f, 0.12f, 0.04f), new Color(0.95f, 0.75f, 0.1f), 0.2f);
        Part(root, "SensorGrill", PrimitiveType.Cube, new Vector3(0f, 0.07f, 0f), new Vector3(0.06f, 0.025f, 0.035f), new Color(0.15f, 0.15f, 0.15f), 0.1f);
        Part(root, "LCDScreen", PrimitiveType.Cube, new Vector3(0f, 0.015f, -0.021f), new Vector3(0.055f, 0.045f, 0.005f), new Color(0.03f, 0.16f, 0.2f), 0.8f);
        GameObject led = Part(root, "StatusLED", PrimitiveType.Sphere, new Vector3(0.025f, 0.047f, -0.022f), new Vector3(0.012f, 0.012f, 0.012f), Color.green, 1.5f);
        s.ledMat = led.GetComponent<Renderer>().sharedMaterial;
        Part(root, "Button", PrimitiveType.Cylinder, new Vector3(0f, -0.045f, -0.021f), new Vector3(0.02f, 0.004f, 0.02f), new Color(0.15f, 0.15f, 0.15f), 0f)
            .transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // Live LCD readout
        GameObject lcdGo = new GameObject("LCDText");
        lcdGo.transform.SetParent(root.transform, false);
        lcdGo.transform.localPosition = new Vector3(0f, 0.015f, -0.0245f);
        s.lcd = lcdGo.AddComponent<TextMeshPro>();
        s.lcd.rectTransform.sizeDelta = new Vector2(0.052f, 0.042f);
        s.lcd.enableAutoSizing = true;
        s.lcd.fontSizeMin = 0.01f;
        s.lcd.fontSizeMax = 0.3f;
        s.lcd.alignment = TextAlignmentOptions.MidlineLeft;
        s.lcd.color = new Color(0.45f, 1f, 0.9f);
        s.lcd.text = "CH4 0.00%\nCO  0ppm\nO2  20.9%";

        // Hint floating above
        GameObject hintGo = new GameObject("Hint");
        hintGo.transform.SetParent(root.transform, false);
        hintGo.transform.localPosition = new Vector3(0f, 0.105f, 0f);
        TextMeshPro hint = hintGo.AddComponent<TextMeshPro>();
        hint.rectTransform.sizeDelta = new Vector2(0.16f, 0.03f);
        hint.enableAutoSizing = true;
        hint.fontSizeMin = 0.01f;
        hint.fontSizeMax = 0.3f;
        hint.alignment = TextAlignmentOptions.Center;
        hint.color = new Color(1f, 0.85f, 0.2f);
        hint.fontStyle = FontStyles.Bold;
        hint.text = "TAP TO CALIBRATE";

        // One box collider on the root gives reliable taps
        foreach (Collider c in root.GetComponentsInChildren<Collider>()) Destroy(c);
        BoxCollider box = root.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0.02f, 0f);
        box.size = new Vector3(0.12f, 0.2f, 0.08f);
        return root;
    }

    GameObject Part(GameObject root, string name, PrimitiveType type, Vector3 pos, Vector3 scale, Color color, float emission)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = MakeMaterial(color, emission);
        return go;
    }

    Material MakeMaterial(Color color, float emission)
    {
        // If pink in the APK: add the URP Lit shader to Project Settings > Graphics > Always Included Shaders.
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (shader == null) shader = Shader.Find("Standard");
        Material mat = new Material(shader);
        mat.color = color;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (emission > 0f)
        {
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", color * emission);
        }
        return mat;
    }

    // ------------------------------------------------------------------
    //  INFO PANEL + QUIZ
    // ------------------------------------------------------------------
    static string Header(ItemData d)
    {
        return "<size=110%><b><color=#FACC15>" + d.title + "</color></b></size>\n";
    }

    void PreparePanel(GameObject panel, ItemData data, string key, bool instantQuestion)
    {
        EnsureWorldCanvasCamera(panel);
        HideAnswerButtons(panel);

        if (itemsSeen.Contains(key))                              // already answered: don't quiz again
        {
            SetInfo(panel, Header(data) + data.info + "\n\n<color=#4ADE80>[OK] Already verified</color>");
            return;
        }
        if (instantQuestion) { ShowQuestion(panel, data, key); return; }

        if (data.is3DHologram) SetInfo(panel, Header(data) + data.info);
        else StartCoroutine(DelayedQuizRoutine(panel, data, key));
    }

    IEnumerator DelayedQuizRoutine(GameObject panel, ItemData data, string key)
    {
        float remaining = quizDelay;
        while (remaining > 0f)
        {
            if (panel == null) yield break;
            SetInfo(panel, Header(data) + data.info + "\n\n<color=#FFD966>Quiz in " + Mathf.CeilToInt(remaining) + "s</color>");
            float step = Mathf.Min(0.5f, remaining);
            yield return new WaitForSeconds(step);
            remaining -= step;
        }
        if (panel != null && !itemsSeen.Contains(key)) ShowQuestion(panel, data, key);
    }

    void ShowQuestion(GameObject panel, ItemData data, string key)
    {
        SetInfo(panel, "<color=#9CA3AF>QUESTION</color>\n<b>" + data.question + "</b>");

        Button btnA = GetButtonComponent(panel, "AnswerButtonA");
        Button btnB = GetButtonComponent(panel, "AnswerButtonB");
        if (btnA == null || btnB == null) return;

        bool flip = shuffleAnswers && Random.value > 0.5f;        // answer is not always "A"
        string textA = flip ? data.optionB : data.optionA;
        string textB = flip ? data.optionA : data.optionB;
        bool aCorrect = flip ? !data.correctIsA : data.correctIsA;

        btnA.gameObject.SetActive(true);
        btnB.gameObject.SetActive(true);
        SetButtonText(btnA, "A) " + textA);
        SetButtonText(btnB, "B) " + textB);
        btnA.onClick.RemoveAllListeners();
        btnB.onClick.RemoveAllListeners();
        btnA.onClick.AddListener(() => OnAnswerClicked(panel, data, key, aCorrect));
        btnB.onClick.AddListener(() => OnAnswerClicked(panel, data, key, !aCorrect));
    }

    void OnAnswerClicked(GameObject panel, ItemData data, string key, bool isCorrect)
    {
        HideAnswerButtons(panel);

        if (itemsSeen.Add(key))                                    // each item scores once
        {
            totalAnswered++;
            if (isCorrect) score++;
            results[key] = isCorrect;
        }

        string text = Header(data);
        if (isCorrect) text += "<color=#4ADE80><b>CORRECT</b></color>\n";
        else text += "<color=#F87171><b>INCORRECT</b></color>\nAnswer: <b>" + (data.correctIsA ? data.optionA : data.optionB) + "</b>\n";
        SetInfo(panel, text + data.explanation);

        PlayAudio(isCorrect ? correctClip : wrongClip, 0.8f);
        if (!isCorrect) TriggerVibration();
        ShowToast(isCorrect ? "Verified: " + data.title : "Review SOP: " + data.title, 2.5f);
        RefreshHudStats();

        if (itemsSeen.Count >= itemDatabase.Count && !completionShown) StartCoroutine(ShowCompletionAfter(2f));
    }

    void SetInfo(GameObject panel, string text)
    {
        TextMeshProUGUI t = GetTextComponent(panel, "InfoText");
        if (t == null) return;
        if (!t.enableAutoSizing)                                   // long explanations must not overflow the panel
        {
            float max = t.fontSize;
            t.enableAutoSizing = true;
            t.fontSizeMax = max;
            t.fontSizeMin = Mathf.Max(1f, max * 0.4f);
        }
        t.text = text;
    }

    void HideAnswerButtons(GameObject panel)
    {
        Button a = GetButtonComponent(panel, "AnswerButtonA");
        Button b = GetButtonComponent(panel, "AnswerButtonB");
        if (a != null) a.gameObject.SetActive(false);
        if (b != null) b.gameObject.SetActive(false);
    }

    void SetButtonText(Button b, string text)
    {
        TextMeshProUGUI t = b.GetComponentInChildren<TextMeshProUGUI>(true);
        if (t != null) t.text = text;
    }

    void EnsureWorldCanvasCamera(GameObject panel)
    {
        foreach (Canvas c in panel.GetComponentsInChildren<Canvas>(true))
            if (c.renderMode == RenderMode.WorldSpace && c.worldCamera == null) c.worldCamera = Camera.main;
    }

    TextMeshProUGUI GetTextComponent(GameObject parent, string childName)
    {
        foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            if (child.name == childName) return child.GetComponent<TextMeshProUGUI>();
        return null;
    }

    Button GetButtonComponent(GameObject parent, string childName)
    {
        foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            if (child.name == childName) return child.GetComponent<Button>();
        return null;
    }

    // ------------------------------------------------------------------
    //  INPUT
    // ------------------------------------------------------------------
    void HandleTap(Camera cam)
    {
        if (cam == null) return;
        Pointer p = Pointer.current;                               // new Input System: touch or mouse
        if (p == null || !p.press.wasPressedThisFrame) return;
        Vector2 pos = p.position.ReadValue();
        if (IsOverUI(pos)) return;

        Ray ray = cam.ScreenPointToRay(pos);
        RaycastHit[] hits = Physics.SphereCastAll(ray, 0.03f, 20f);   // AR planes may be hit first, so check them all
        Transform best = null;
        float bestDist = float.MaxValue;
        foreach (RaycastHit h in hits)
        {
            Transform t = h.collider.transform;
            while (t != null && !t.name.StartsWith("Hologram_")) t = t.parent;
            if (t != null && h.distance < bestDist) { best = t; bestDist = h.distance; }
        }
        if (best == null) return;

        string key = best.name.Substring("Hologram_".Length);
        Spawned s;
        ItemData d;
        if (spawned.TryGetValue(key, out s) && itemDatabase.TryGetValue(key, out d) && s.isHolo && s.go == best.gameObject)
            OnHologramTapped(d, key, s);
    }

    bool IsOverUI(Vector2 pos)
    {
        if (EventSystem.current == null) return false;
        PointerEventData data = new PointerEventData(EventSystem.current) { position = pos };
        uiHits.Clear();
        EventSystem.current.RaycastAll(data, uiHits);
        return uiHits.Count > 0;
    }

    void OnHologramTapped(ItemData data, string key, Spawned old)
    {
        PlayAudio(tapClip, 0.9f);
        TriggerVibration();
        gasCalibrated = true;
        Vector3 pos = old.go.transform.position;
        Transform anchor = old.anchor;
        Destroy(old.go);

        Spawned s = new Spawned { anchor = anchor, isHolo = false, born = Time.time };   // isHolo=false: the panel no longer spins
        s.go = infoPanelPrefab != null ? Instantiate(infoPanelPrefab) : BuildFallbackPanel();
        s.go.transform.position = pos;
        s.go.transform.SetParent(anchor, true);
        s.baseScale = s.go.transform.localScale;
        s.go.transform.localScale = Vector3.zero;
        PreparePanel(s.go, data, key, true);
        spawned[key] = s;
        ShowToast("Detector calibrated", 2f);
    }

    void HandleBackKey()
    {
        Keyboard kb = Keyboard.current;                            // Android back button arrives as Escape
        if (kb == null || !kb.escapeKey.wasPressedThisFrame) return;

        if (checklistPanel != null && checklistPanel.activeSelf) { checklistPanel.SetActive(false); return; }
        if (completionPanel != null && completionPanel.activeSelf) { completionPanel.SetActive(false); return; }
        if (Time.time - lastBackPress < 2f) { GoToMenu(); return; }
        lastBackPress = Time.time;
        ShowToast("Press back again to return to the menu", 2f);
    }

    void GoToMenu()
    {
        if (!sessionSaved)
        {
            sessionSaved = true;
            if (BackendSync.Instance != null) BackendSync.Instance.SaveAndSyncScore(score, itemDatabase.Count);
        }
        if (Application.CanStreamedLevelBeLoaded(mainMenuSceneName)) SceneManager.LoadScene(mainMenuSceneName);
        else Debug.LogError("Scene '" + mainMenuSceneName + "' is not in Build Profiles.");
    }

    // ------------------------------------------------------------------
    //  SCANNER HUD
    // ------------------------------------------------------------------
    void BuildScannerHUD()
    {
        hudCanvasObj = new GameObject("ARScannerHUDCanvas");
        Canvas canvas = hudCanvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 15;
        CanvasScaler scaler = hudCanvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;
        hudCanvasObj.AddComponent<GraphicRaycaster>();
        Transform root = hudCanvasObj.transform;

        // Keep HUD clear of the camera cut-out / gesture bar (canvas units).
        float scale = Mathf.Sqrt((Screen.width / 1080f) * (Screen.height / 1920f));
        Rect safe = Screen.safeArea;
        float topInset = Mathf.Max(0f, (Screen.height - (safe.y + safe.height)) / scale);
        float botInset = Mathf.Max(0f, safe.y / scale);

        Vector2 TL = new Vector2(0f, 1f), TR = new Vector2(1f, 1f), TM = new Vector2(0.5f, 1f),
                BM = new Vector2(0.5f, 0f), C = new Vector2(0.5f, 0.5f);
        Color chip = new Color(0.08f, 0.11f, 0.16f, 0.9f);

        // Top row: menu | gear list
        Button menu = NewButton(root, "BtnMenu", TL, new Vector2(150f, -90f - topInset), new Vector2(240f, 90f), "< MENU", chip, GoToMenu);
        menuLabel = menu.GetComponentInChildren<TextMeshProUGUI>();
        Button gear = NewButton(root, "BtnChecklist", TR, new Vector2(-210f, -90f - topInset), new Vector2(340f, 90f), "GEAR LIST 0/6", chip, ToggleChecklistDrawer);
        gearLabel = gear.GetComponentInChildren<TextMeshProUGUI>();

        // Progress bar + score chip
        progressFill = NewProgressBar(root, TM, new Vector2(0f, -200f - topInset), new Vector2(640f, 40f), "VERIFIED 0/6", out progressLabel);
        scoreLabel = NewText(root, "SCORE 0/6", 30, new Color(0.4f, 0.9f, 0.4f), TextAlignmentOptions.Center, TM, new Vector2(0f, -255f - topInset), new Vector2(500f, 50f));
        diagText = NewText(root, "", 20, new Color(0.7f, 0.9f, 1f, 0.9f), TextAlignmentOptions.TopLeft, TL, new Vector2(380f, -400f - topInset), new Vector2(720f, 200f));

        BuildReticle(root, C, new Vector2(0f, 60f));

        // Status banner + toast
        Image banner = NewImage("StatusBanner", root, BM, new Vector2(0f, 210f + botInset), new Vector2(940f, 80f), new Color(0.05f, 0.08f, 0.12f, 0.85f));
        statusBannerText = NewText(banner.transform, "", 30, Color.white, TextAlignmentOptions.Center, C, Vector2.zero, new Vector2(920f, 70f));
        toastText = NewText(root, "", 34, new Color(1f, 1f, 1f, 0f), TextAlignmentOptions.Center, BM, new Vector2(0f, 330f + botInset), new Vector2(900f, 90f));
        toastText.fontStyle = FontStyles.Bold;

        BuildChecklistDrawer(root, C);
        BuildCompletionPanel(root, C);
    }

    void BuildReticle(Transform root, Vector2 anchor, Vector2 pos)
    {
        reticleRT = NewRect("Reticle", root, anchor, pos, new Vector2(420f, 420f));
        const float half = 210f, len = 90f, th = 8f;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                // Corner brackets: one horizontal + one vertical bar per corner
                reticleParts.Add(NewImage("BracketH", reticleRT, new Vector2(0.5f, 0.5f), new Vector2(sx * (half - len / 2f), sy * half), new Vector2(len, th), reticleColor));
                reticleParts.Add(NewImage("BracketV", reticleRT, new Vector2(0.5f, 0.5f), new Vector2(sx * half, sy * (half - len / 2f)), new Vector2(th, len), reticleColor));
            }
        reticleParts.Add(NewImage("Dot", reticleRT, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(10f, 10f), reticleColor));
        Image scan = NewImage("ScanLine", reticleRT, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(380f, 4f), reticleColor);
        scanLineRT = scan.rectTransform;
        reticleParts.Add(scan);
    }

    void BuildChecklistDrawer(Transform root, Vector2 C)
    {
        Image bg = NewImage("ChecklistDrawer", root, C, new Vector2(0f, 20f), new Vector2(880f, 900f), new Color(0.06f, 0.08f, 0.12f, 0.96f), true);
        checklistPanel = bg.gameObject;
        Transform t = checklistPanel.transform;

        NewText(t, "UNDERGROUND EQUIPMENT CHECKLIST", 34, new Color(1f, 0.85f, 0.2f), TextAlignmentOptions.Center, C, new Vector2(0f, 390f), new Vector2(820f, 70f));
        drawerProgressFill = NewProgressBar(t, C, new Vector2(0f, 320f), new Vector2(760f, 40f), "VERIFIED 0/6", out drawerProgressLabel);

        float y = 230f;
        foreach (string key in itemOrder)
        {
            TextMeshProUGUI row = NewText(t, "", 32, new Color(0.85f, 0.85f, 0.85f), TextAlignmentOptions.Left, C, new Vector2(0f, y), new Vector2(760f, 60f));
            checklistRows[key] = row;
            y -= 78f;
        }
        NewText(t, "Scan each item, then answer its question to verify it.", 24, new Color(0.6f, 0.65f, 0.7f), TextAlignmentOptions.Center, C, new Vector2(0f, -330f), new Vector2(800f, 50f));
        NewButton(t, "BtnCloseList", C, new Vector2(0f, -395f), new Vector2(300f, 70f), "CLOSE", new Color(0.35f, 0.12f, 0.12f, 0.95f), () => checklistPanel.SetActive(false));
        checklistPanel.SetActive(false);
    }

    void BuildCompletionPanel(Transform root, Vector2 C)
    {
        Image bg = NewImage("CompletionPanel", root, C, new Vector2(0f, 20f), new Vector2(900f, 760f), new Color(0.05f, 0.09f, 0.08f, 0.97f), true);
        completionPanel = bg.gameObject;
        Transform t = completionPanel.transform;

        NewText(t, "ALL EQUIPMENT VERIFIED", 46, new Color(0.4f, 0.95f, 0.55f), TextAlignmentOptions.Center, C, new Vector2(0f, 290f), new Vector2(840f, 80f));
        completionScoreText = NewText(t, "SCORE", 80, Color.white, TextAlignmentOptions.Center, C, new Vector2(0f, 150f), new Vector2(840f, 120f));
        completionMessageText = NewText(t, "", 34, new Color(0.85f, 0.85f, 0.85f), TextAlignmentOptions.Center, C, new Vector2(0f, -30f), new Vector2(820f, 230f));
        completionMessageText.enableAutoSizing = true;
        completionMessageText.fontSizeMin = 22f;
        completionMessageText.fontSizeMax = 34f;
        NewButton(t, "BtnKeepScanning", C, new Vector2(0f, -190f), new Vector2(620f, 90f), "CONTINUE SCANNING", new Color(0.12f, 0.35f, 0.65f, 1f), () => completionPanel.SetActive(false));
        NewButton(t, "BtnSaveMenu", C, new Vector2(0f, -300f), new Vector2(620f, 90f), "SAVE & RETURN TO MENU", new Color(0.2f, 0.5f, 0.28f, 1f), GoToMenu);
        completionPanel.SetActive(false);
    }

    IEnumerator ShowCompletionAfter(float delay)
    {
        completionShown = true;
        yield return new WaitForSeconds(delay);

        int total = itemDatabase.Count;
        float pct = total > 0 ? (float)score / total : 0f;
        string wrong = "";
        foreach (string key in itemOrder)
        {
            bool ok;
            if (results.TryGetValue(key, out ok) && !ok) wrong += (wrong.Length > 0 ? ", " : "") + itemDatabase[key].title;
        }

        completionScoreText.text = score + " / " + total;
        string msg = pct >= 0.8f ? "<color=#4ADE80><b>EXCELLENT</b></color>\nYou are ready for the shift briefing." : "<color=#FACC15><b>NEEDS REVIEW</b></color>\nRe-scan the items you missed and read the explanations.";
        if (wrong.Length > 0) msg += "\n\n<color=#F87171>Review: " + wrong + "</color>";
        completionMessageText.text = msg;
        completionPanel.SetActive(true);
        PlayAudio(correctClip, 0.9f);
        TriggerVibration();
    }

    void ToggleChecklistDrawer()
    {
        if (checklistPanel != null) checklistPanel.SetActive(!checklistPanel.activeSelf);
    }

    // ------------------------------------------------------------------
    //  HUD STATE
    // ------------------------------------------------------------------
    void RefreshHudStats()
    {
        int total = itemOrder.Count;
        int done = itemsSeen.Count;
        float ratio = total > 0 ? (float)done / total : 0f;

        if (gearLabel != null) gearLabel.text = "GEAR LIST " + done + "/" + total;
        if (progressLabel != null) progressLabel.text = "VERIFIED " + done + "/" + total;
        if (drawerProgressLabel != null) drawerProgressLabel.text = "VERIFIED " + done + "/" + total;
        SetFill(progressFill, ratio);
        SetFill(drawerProgressFill, ratio);
        if (hudScoreText != null) hudScoreText.text = "Score: " + score + "/" + total;
        if (scoreLabel != null) scoreLabel.text = "SCORE " + score + "/" + total;

        foreach (string key in itemOrder)
        {
            TextMeshProUGUI row;
            if (!checklistRows.TryGetValue(key, out row)) continue;
            string title = itemDatabase[key].title;
            bool ok;
            if (results.TryGetValue(key, out ok))
                row.text = ok ? "<color=#4ADE80>[OK]</color>   " + title : "<color=#F87171>[X]</color>    " + title;
            else if (detectedKeys.Contains(key))
                row.text = "<color=#FACC15>[SEEN]</color> <color=#D1D5DB>" + title + "</color>";
            else
                row.text = "<color=#6B7280>[    ]  " + title + "</color>";
        }
    }

    void SetFill(RectTransform fill, float ratio)
    {
        if (fill != null) fill.anchorMax = new Vector2(Mathf.Clamp01(ratio), 1f);
    }

    void ShowToast(string message, float seconds = 2.5f)
    {
        if (toastText == null) return;
        toastText.text = message;
        toastTimer = seconds;
    }

    void AnimateHud(float dt)
    {
        // Which target is currently locked?
        string focus = null;
        Spawned fs;
        if (lastAcquiredKey != null && spawned.TryGetValue(lastAcquiredKey, out fs) && fs.go != null && fs.go.activeSelf) focus = lastAcquiredKey;
        else
            foreach (KeyValuePair<string, Spawned> kv in spawned)
                if (kv.Value.go != null && kv.Value.go.activeSelf) { focus = kv.Key; break; }

        string shown = focus ?? "";
        if (shown != bannerKeyShown && statusBannerText != null)
        {
            bannerKeyShown = shown;
            statusBannerText.text = focus == null
                ? "SCANNING... POINT CAMERA AT SAFETY EQUIPMENT"
                : "TARGET LOCKED: <color=#38BDF8>" + itemDatabase[focus].title + "</color>";
        }

        // Reticle: yellow while scanning, green on lock
        Color target = focus == null ? new Color(1f, 0.8f, 0.2f, 0.55f) : new Color(0.2f, 1f, 0.4f, 0.95f);
        reticleColor = Color.Lerp(reticleColor, target, 1f - Mathf.Exp(-8f * dt));
        foreach (Image part in reticleParts) if (part != null) part.color = reticleColor;

        if (reticleRT != null)
        {
            float pulse = 1f + (focus == null ? 0.04f : 0.07f) * Mathf.Sin(Time.time * (focus == null ? 3f : 6f));
            reticleRT.localScale = new Vector3(pulse, pulse, 1f);
        }
        if (scanLineRT != null)
        {
            scanLineRT.gameObject.SetActive(focus == null);       // radar sweep only while searching
            scanLineRT.anchoredPosition = new Vector2(0f, Mathf.Sin(Time.time * 1.6f) * 185f);
        }

        if (toastTimer > 0f)
        {
            toastTimer -= dt;
            toastText.color = new Color(1f, 1f, 1f, Mathf.Clamp01(toastTimer));
        }
    }

    // ------------------------------------------------------------------
    //  UI FACTORY (everything anchored, nothing relies on defaults)
    // ------------------------------------------------------------------
    RectTransform NewRect(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    Image NewImage(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, Color color, bool raycast = false)
    {
        Image img = NewRect(name, parent, anchor, pos, size).gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    TextMeshProUGUI NewText(Transform parent, string text, float fontSize, Color color, TextAlignmentOptions align, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        TextMeshProUGUI tmp = NewRect("Label", parent, anchor, pos, size).gameObject.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.alignment = align;
        tmp.color = color;
        tmp.raycastTarget = false;
        return tmp;
    }

    Button NewButton(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, string label, Color bg, UnityEngine.Events.UnityAction onClick)
    {
        Image img = NewImage(name, parent, anchor, pos, size, bg, true);
        Button btn = img.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);
        TextMeshProUGUI t = NewText(img.transform, label, 30, Color.white, TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero, size - new Vector2(16f, 16f));
        t.fontStyle = FontStyles.Bold;
        return btn;
    }

    RectTransform NewProgressBar(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, string text, out TextMeshProUGUI label)
    {
        Image bg = NewImage("ProgressBar", parent, anchor, pos, size, new Color(0.08f, 0.1f, 0.14f, 0.9f));
        Image fill = NewImage("Fill", bg.transform, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, new Color(0.2f, 0.75f, 0.4f, 0.95f));
        RectTransform rt = fill.rectTransform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = new Vector2(0f, 1f);
        rt.offsetMin = new Vector2(3f, 3f); rt.offsetMax = new Vector2(-3f, -3f);
        label = NewText(bg.transform, text, 22, Color.white, TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero, size);
        label.fontStyle = FontStyles.Bold;
        return rt;
    }

    // ------------------------------------------------------------------
    //  SYNTHESIZED AUDIO + HAPTICS
    // ------------------------------------------------------------------
    void SetupProceduralAudio()
    {
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        lockOnClip = GenerateTone(1200f, 0.14f, 0.4f);
        tapClip = GenerateTone(800f, 0.08f, 0.35f);
        correctClip = GenerateTone(1046f, 0.2f, 0.35f);
        wrongClip = GenerateTone(170f, 0.35f, 0.45f);
    }

    AudioClip GenerateTone(float freq, float duration, float volume)
    {
        const int rate = 44100;
        int n = Mathf.CeilToInt(rate * duration);
        float[] samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = Mathf.Sin(2f * Mathf.PI * freq * i / rate) * volume * (1f - (float)i / n);
        AudioClip clip = AudioClip.Create("Tone_" + freq, n, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    void PlayAudio(AudioClip clip, float vol)
    {
        if (audioSource != null && clip != null) audioSource.PlayOneShot(clip, vol);
    }

    void TriggerVibration()
    {
#if UNITY_ANDROID || UNITY_IOS
        Handheld.Vibrate();
#endif
    }
}