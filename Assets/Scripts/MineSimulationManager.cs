// =====================================================================
//  MineSimulationManager.cs  -  ADVANCED EDITION
//  Procedural VR/mobile coal-mine emergency simulation.
//  Everything (tunnel, items, HUD, quiz UI, effects, audio) is built at
//  runtime, so the scene only needs: Main Camera + this script on an
//  empty GameObject. Needs a "MainMenu" scene in Build Profiles.
//
//  What's new vs. the original:
//   - Stub methods implemented (falling rocks, conveyor fire, smoke)
//   - State machine (Exploring / GasLeak / Blackout / Finished)
//   - Hazards trigger automatically when you walk into them
//   - Cannot skip the gas zone; maze unlocks only after the mask
//   - Fixed: junction wall that sealed the main tunnel, walkable-area
//     clamp, ambient light never applying (needs AmbientMode.Flat),
//     legacy Input.gyro crash with the new Input System, double-scoring,
//     divide-by-zero in results, hard-coded "/6" score
//   - Score is out of the REAL maximum (skipping items now costs you)
//   - All 6 textures are used; explanations after every answer
//   - Shuffled answers, objective line, timer, toast messages,
//     oxygen bar, retry button, head-bob, camera shake, procedural audio
//   - ASCII-only UI text (emoji/arrows render as boxes in default TMP)
// =====================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;

public class MineSimulationManager : MonoBehaviour
{
    // ------------------------------------------------------------------
    //  INSPECTOR
    // ------------------------------------------------------------------
    [Header("Optional: Assign your ARImages textures here in Inspector")]
    public Texture2D helmetTexture;
    public Texture2D maskTexture;
    public Texture2D dangerSignTexture;
    public Texture2D roofSignTexture;
    public Texture2D escapeRouteTexture;
    public Texture2D gasDetectorTexture;

    [Header("Gameplay")]
    [SerializeField] private float quizReadDelay = 2.5f;
    [SerializeField] private float walkSpeed = 3f;
    [SerializeField] private float gasWalkSpeed = 1f;
    [SerializeField] private float maxGasTime = 12f;
    [SerializeField, Range(0.1f, 1f)] private float passPercent = 0.7f;
    [SerializeField] private bool shuffleAnswers = true;
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    [Header("Controls")]
    [SerializeField] private float lookSensitivity = 0.22f;
    [SerializeField] private bool useGyro = true;
    [SerializeField] private float gyroSensitivity = 1f;
    [SerializeField] private bool gyroInvertYaw = false;
    [SerializeField] private bool gyroInvertPitch = false;

    // ------------------------------------------------------------------
    //  TYPES
    // ------------------------------------------------------------------
    private enum SimState { Exploring, GasLeak, Blackout, Finished }

    public enum ItemKind { Standard, RoofFall, Fire, GasLeak, GasMask, EscapeSign, MazeExit, MazeDeadEnd }

    public class MineItemData
    {
        public string key;
        public string title;
        public string info;
        public string question;
        public string optionA;
        public string optionB;
        public string explanation;
        public bool correctIsA;
        public Vector3 position;
        public Color fallbackColor;
        public Texture2D texture;
        public ItemKind kind = ItemKind.Standard;

        // runtime
        public Material material;
        public Color emissionBase;

        public bool Scorable
        {
            get { return kind == ItemKind.Standard || kind == ItemKind.RoofFall || kind == ItemKind.Fire || kind == ItemKind.GasMask; }
        }
    }

    // ------------------------------------------------------------------
    //  CONSTANTS (world layout)
    // ------------------------------------------------------------------
    private const float RoofTriggerZ = 9.5f;
    private const float FireTriggerZ = 18.5f;
    private const float GasTriggerZ = 30.5f;
    private const float JunctionZ = 33.4f;     // where the T-junction starts
    private const float MazeMaxZ = 38.5f;
    private const float DragThresholdPx = 12f;
    private const float TapMaxPx = 18f;
    private const float TapMaxTime = 0.35f;

    // ------------------------------------------------------------------
    //  RUNTIME STATE
    // ------------------------------------------------------------------
    private SimState state = SimState.Exploring;

    // camera / input
    private Camera mainCam;
    private Vector3 baseCameraPosition;
    private float yaw, pitch;
    private UnityEngine.InputSystem.Gyroscope gyroDevice;
    private bool isPressing, pressOnRight, hasDragged;
    private Vector2 pointerDownPos, lastPointerPos;
    private float pressStartTime;
    private float moveInput, bobPhase, bobBlend, shakeAmount;

    // HUD / UI
    private TextMeshProUGUI scoreText, timerText, objectiveText, promptText, toastText, panicText, o2LabelText;
    private RectTransform o2FillRT;
    private GameObject o2Root;
    private Image blackoutImage;
    private GameObject quizPanel;
    private TextMeshProUGUI quizTitleText, quizBodyText;
    private Button buttonA, buttonB, continueButton, closeButton;
    private GameObject resultsPanel;
    private TextMeshProUGUI resultsTitleText, resultsScoreText, resultsMessageText;
    private float toastTimer, promptTimer = 16f;

    // session
    private int score, totalAnswered, maxScore;
    private readonly HashSet<string> answeredItems = new HashSet<string>();
    private float elapsed;
    private bool sessionSaved;

    // hazards
    private bool gasTriggered, maskEquipped, roofFallTriggered, fireTriggered, mazeUnlocked;
    private float gasTimer, beepTimer;
    private Light helmetFlashlight, hazardLight, fireLight;
    private readonly List<Light> tunnelLights = new List<Light>();
    private readonly List<Rigidbody> fallingRocks = new List<Rigidbody>();
    private ParticleSystem fireParticles, smokeParticles;

    // content
    private readonly Dictionary<string, MineItemData> itemDatabase = new Dictionary<string, MineItemData>();
    private Coroutine activeQuizCoroutine;

    // audio / cached assets
    private AudioSource sfx;
    private AudioClip beepClip, correctClip, wrongClip, rumbleClip;
    private Material particleMaterial;
    private Texture2D softCircle;

    // ------------------------------------------------------------------
    //  LIFECYCLE
    // ------------------------------------------------------------------
    void Start()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        SetupCameraAndAtmosphere();
        EnsureEventSystem();
        SetupAudio();
        BuildMineTunnel();
        BuildHUDAndQuizUI();
        PopulateSafetyObjects();
        RefreshHUD();
        SetObjective("Walk forward and inspect the safety equipment.");
    }

    void OnDestroy()
    {
        if (gyroDevice != null && gyroDevice.added) InputSystem.DisableDevice(gyroDevice);
    }

    // ------------------------------------------------------------------
    //  CAMERA + ATMOSPHERE
    // ------------------------------------------------------------------
    void SetupCameraAndAtmosphere()
    {
        mainCam = Camera.main;
        if (mainCam == null)
        {
            GameObject camObj = new GameObject("Main Camera");
            camObj.tag = "MainCamera";
            mainCam = camObj.AddComponent<Camera>();
        }
        if (FindFirstObjectByType<AudioListener>() == null) mainCam.gameObject.AddComponent<AudioListener>();

        baseCameraPosition = new Vector3(0f, 1.7f, -8f);
        mainCam.transform.position = baseCameraPosition;
        mainCam.transform.rotation = Quaternion.identity;
        mainCam.nearClipPlane = 0.05f;
        mainCam.fieldOfView = 70f;
        mainCam.clearFlags = CameraClearFlags.SolidColor;
        mainCam.backgroundColor = new Color(0.05f, 0.04f, 0.03f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.08f, 0.06f, 0.04f);
        RenderSettings.fogDensity = 0.045f;
        // Without Flat mode the ambientLight colour is ignored (skybox wins).
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.18f, 0.16f, 0.14f);

        // Helmet lamp: off until the power failure.
        helmetFlashlight = mainCam.gameObject.AddComponent<Light>();
        helmetFlashlight.type = LightType.Spot;
        helmetFlashlight.range = 30f;
        helmetFlashlight.spotAngle = 45f;
        helmetFlashlight.intensity = 0f;
        helmetFlashlight.color = new Color(0.9f, 0.9f, 1f);

        foreach (Light l in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (l.type == LightType.Directional)
            {
                l.intensity = 0.25f;
                l.color = new Color(0.9f, 0.8f, 0.65f);
            }
        }

        // New Input System gyroscope (legacy Input.gyro throws when the
        // project only uses the new Input System).
        gyroDevice = UnityEngine.InputSystem.Gyroscope.current;
        if (useGyro && gyroDevice != null) InputSystem.EnableDevice(gyroDevice);
    }

    void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() == null)
        {
            GameObject es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }
    }

    // ------------------------------------------------------------------
    //  MATERIALS / PRIMITIVES
    // ------------------------------------------------------------------
    Shader FindFirstShader(params string[] names)
    {
        foreach (string n in names)
        {
            Shader s = Shader.Find(n);
            if (s != null) return s;
        }
        return null;
    }

    Material CreateLitMaterial(Color color, Texture2D tex = null, float emission = 0f)
    {
        // TIP: if materials turn pink in the APK, add these shaders to
        // Project Settings > Graphics > Always Included Shaders.
        Shader shader = FindFirstShader("Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard", "Sprites/Default");
        Material mat = new Material(shader);
        mat.color = color;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (tex != null)
        {
            mat.mainTexture = tex;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        }
        if (emission > 0f)
        {
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            // Textured signs glow in their own colours; flat items glow in their tint.
            if (tex != null && mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", tex);
            Color e = (tex != null ? Color.white : color) * emission;
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", e);
        }
        return mat;
    }

    GameObject CreateBox(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, bool keepCollider = false)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetParent(parent);
        box.transform.position = pos;
        box.transform.localScale = scale;
        box.GetComponent<Renderer>().sharedMaterial = mat;
        if (!keepCollider) Destroy(box.GetComponent<Collider>());
        return box;
    }

    Light CreatePointLight(string name, Transform parent, Vector3 pos, Color color, float range, float intensity)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent);
        go.transform.position = pos;
        Light l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.range = range;
        l.intensity = intensity;
        l.color = color;
        return l;
    }

    // ------------------------------------------------------------------
    //  TUNNEL
    // ------------------------------------------------------------------
    void BuildMineTunnel()
    {
        GameObject root = new GameObject("MineTunnelEnvironment");
        Material wall = CreateLitMaterial(new Color(0.16f, 0.15f, 0.14f));
        Material floor = CreateLitMaterial(new Color(0.12f, 0.11f, 0.10f));
        Material wood = CreateLitMaterial(new Color(0.38f, 0.22f, 0.09f));
        Material steel = CreateLitMaterial(new Color(0.55f, 0.57f, 0.60f));
        Transform t = root.transform;

        // MAIN TUNNEL (z -12 .. 34). Floor keeps its collider so rocks land on it.
        CreateBox("Floor_Main", t, new Vector3(0f, -0.1f, 11f), new Vector3(6f, 0.2f, 46f), floor, true);
        CreateBox("Ceiling_Main", t, new Vector3(0f, 3.6f, 11f), new Vector3(6f, 0.2f, 46f), wall);
        CreateBox("LeftWall_Main", t, new Vector3(-3f, 1.75f, 11f), new Vector3(0.2f, 3.7f, 46f), wall);
        CreateBox("RightWall_Main", t, new Vector3(3f, 1.75f, 11f), new Vector3(0.2f, 3.7f, 46f), wall);
        CreateBox("BackWall_Main", t, new Vector3(0f, 1.75f, -11f), new Vector3(6f, 3.7f, 0.2f), wall);
        CreateBox("LeftRail", t, new Vector3(-0.6f, 0.04f, 11f), new Vector3(0.08f, 0.08f, 46f), steel);
        CreateBox("RightRail", t, new Vector3(0.6f, 0.04f, 11f), new Vector3(0.08f, 0.08f, 46f), steel);

        // T-JUNCTION at z = 33..39. The two "back" walls now stop at the
        // main tunnel opening (|x| < 3). The original ones spanned x=-16..16
        // and walled the main tunnel shut.
        // Left branch = WRONG way (dead end)
        CreateBox("Floor_Left", t, new Vector3(-8f, -0.1f, 36f), new Vector3(16f, 0.2f, 6f), floor);
        CreateBox("Ceiling_Left", t, new Vector3(-8f, 3.6f, 36f), new Vector3(16f, 0.2f, 6f), wall);
        CreateBox("Wall_Left_Front", t, new Vector3(-8f, 1.75f, 39f), new Vector3(16f, 3.7f, 0.2f), wall);
        CreateBox("Wall_Left_Back", t, new Vector3(-9.5f, 1.75f, 33f), new Vector3(13f, 3.7f, 0.2f), wall);
        CreateBox("Wall_Left_End", t, new Vector3(-16f, 1.75f, 36f), new Vector3(0.2f, 3.7f, 6f), wall);
        // Right branch = RIGHT way (exit)
        CreateBox("Floor_Right", t, new Vector3(8f, -0.1f, 36f), new Vector3(16f, 0.2f, 6f), floor);
        CreateBox("Ceiling_Right", t, new Vector3(8f, 3.6f, 36f), new Vector3(16f, 0.2f, 6f), wall);
        CreateBox("Wall_Right_Front", t, new Vector3(8f, 1.75f, 39f), new Vector3(16f, 3.7f, 0.2f), wall);
        CreateBox("Wall_Right_Back", t, new Vector3(9.5f, 1.75f, 33f), new Vector3(13f, 3.7f, 0.2f), wall);
        CreateBox("Wall_Right_End", t, new Vector3(16f, 1.75f, 36f), new Vector3(0.2f, 3.7f, 6f), wall);

        // Timber supports + ceiling lamps
        for (float z = -8f; z <= 30f; z += 4f)
        {
            CreateBox("LeftBeam_" + z, t, new Vector3(-2.75f, 1.75f, z), new Vector3(0.3f, 3.5f, 0.3f), wood);
            CreateBox("RightBeam_" + z, t, new Vector3(2.75f, 1.75f, z), new Vector3(0.3f, 3.5f, 0.3f), wood);
            CreateBox("TopBeam_" + z, t, new Vector3(0f, 3.35f, z), new Vector3(5.8f, 0.3f, 0.3f), wood);
            tunnelLights.Add(CreatePointLight("TunnelLight_" + z, t, new Vector3(0f, 3.1f, z), new Color(1f, 0.78f, 0.42f), 10f, 2.2f));
        }

        // Faint green emergency glow at the real exit (stays on during the blackout)
        CreatePointLight("ExitGlow", t, new Vector3(14f, 2.6f, 36f), new Color(0.2f, 1f, 0.4f), 7f, 2.5f);
    }

    // ------------------------------------------------------------------
    //  PROCEDURAL AUDIO (no audio assets needed)
    // ------------------------------------------------------------------
    void SetupAudio()
    {
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        beepClip = MakeTone(880f, 0.12f, 0.35f);
        correctClip = MakeTone(1046f, 0.22f, 0.35f);
        wrongClip = MakeTone(170f, 0.35f, 0.45f);
        rumbleClip = MakeRumble(1.6f, 0.9f);
    }

    AudioClip MakeTone(float freq, float seconds, float volume)
    {
        const int rate = 44100;
        int n = Mathf.CeilToInt(rate * seconds);
        float[] d = new float[n];
        for (int i = 0; i < n; i++)
        {
            float env = 1f - (float)i / n;
            d[i] = Mathf.Sin(2f * Mathf.PI * freq * i / rate) * volume * env;
        }
        AudioClip c = AudioClip.Create("tone_" + freq, n, 1, rate, false);
        c.SetData(d, 0);
        return c;
    }

    AudioClip MakeRumble(float seconds, float volume)
    {
        const int rate = 22050;
        int n = Mathf.CeilToInt(rate * seconds);
        float[] d = new float[n];
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            prev = prev * 0.97f + (Random.value * 2f - 1f) * 0.03f;   // low-passed noise
            float env = Mathf.Sin(Mathf.PI * (float)i / n);
            d[i] = Mathf.Clamp(prev * 14f * volume * env, -1f, 1f);
        }
        AudioClip c = AudioClip.Create("rumble", n, 1, rate, false);
        c.SetData(d, 0);
        return c;
    }

    void Play(AudioClip clip, float volume = 1f)
    {
        if (sfx != null && clip != null) sfx.PlayOneShot(clip, volume);
    }

    static void Vibrate()
    {
#if UNITY_ANDROID || UNITY_IOS
        Handheld.Vibrate();
#endif
    }

    // ------------------------------------------------------------------
    //  CONTENT
    // ------------------------------------------------------------------
    void PopulateSafetyObjects()
    {
        // --- Safety equipment & signs along the main tunnel ---
        RegisterAndSpawnItem(new MineItemData
        {
            key = "HelmetImage", title = "SAFETY HELMET",
            info = "A hard hat protects against falling rock and head impact. Check the strap and lamp bracket before every shift.",
            question = "When must you wear your helmet?",
            optionA = "At all times underground", optionB = "Only during blasting", correctIsA = true,
            explanation = "Roof and rib falls happen without warning. The helmet stays on from entry to exit.",
            position = new Vector3(-1.8f, 1.2f, -4f), fallbackColor = new Color(0.95f, 0.8f, 0.1f), texture = helmetTexture
        });

        RegisterAndSpawnItem(new MineItemData
        {
            key = "DangerSignImage", title = "DANGER SIGN",
            info = "Danger signs mark zones with an immediate risk to life. Never cross one without authorisation.",
            question = "A DANGER sign blocks the way ahead. What do you do?",
            optionA = "Stop and report to your supervisor", optionB = "Pass through quickly", correctIsA = true,
            explanation = "Restricted areas need a permit and a risk check. Speed does not make it safe.",
            position = new Vector3(1.8f, 1.6f, 2f), fallbackColor = new Color(0.9f, 0.1f, 0.1f), texture = dangerSignTexture
        });

        RegisterAndSpawnItem(new MineItemData
        {
            key = "RoofSignImage", title = "ROOF WARNING",
            info = "This sign warns of an unstable roof and a risk of rock or roof collapse in this section.",
            question = "You see a roof-instability warning. Correct response?",
            optionA = "Wait for a support inspection", optionB = "Walk through quickly", correctIsA = true,
            explanation = "Report it and do not proceed until the roof has been inspected and supported.",
            position = new Vector3(-1.8f, 1.6f, 7f), fallbackColor = new Color(0.95f, 0.55f, 0.1f), texture = roofSignTexture
        });

        // --- Hazard 1: roof fall (auto-triggers at z >= 9.5) ---
        RegisterAndSpawnItem(new MineItemData
        {
            key = "RoofFallHazard", title = "HAZARD: ROOF FALL", kind = ItemKind.RoofFall,
            info = "EMERGENCY SIMULATION!\nLoose rock is collapsing from the roof ahead.",
            question = "Immediate action?",
            optionA = "Retreat and alert others", optionB = "Run through the debris", correctIsA = true,
            explanation = "Never run beneath an unstable roof. Retreat to supported ground and report it.",
            position = new Vector3(-2.0f, 1.5f, 11f), fallbackColor = new Color(0.85f, 0.1f, 0.1f)
        });

        // --- Hazard 2: conveyor fire (auto-triggers at z >= 18.5) ---
        RegisterAndSpawnItem(new MineItemData
        {
            key = "FireHazard", title = "HAZARD: FIRE", kind = ItemKind.Fire,
            info = "EMERGENCY SIMULATION!\nThe conveyor belt has caught fire. Smoke is filling the tunnel.",
            question = "Smoke is spreading. Your priority?",
            optionA = "Fight the fire alone", optionB = "Raise the alarm and evacuate", correctIsA = false,
            explanation = "Belt fires produce toxic smoke within seconds. Alarm and evacuate unless trained and equipped.",
            position = new Vector3(2.0f, 1.5f, 21f), fallbackColor = new Color(1f, 0.5f, 0f)
        });

        // --- Gas detector + mask (stress sequence) ---
        RegisterAndSpawnItem(new MineItemData
        {
            key = "GasDetectorImage", title = "GAS DETECTOR",
            info = "Displays live methane (CH4) and CO levels. When the alarm sounds, gas has reached a dangerous level.",
            question = "The gas detector alarm is sounding. What now?",
            optionA = "Evacuate immediately", optionB = "Silence it and continue", correctIsA = true,
            explanation = "Methane is explosive and CO is lethal. An alarm means leave now, never ignore it.",
            position = new Vector3(-1.8f, 1.2f, 25f), fallbackColor = new Color(0.3f, 0.7f, 1f), texture = gasDetectorTexture
        });

        RegisterAndSpawnItem(new MineItemData
        {
            key = "EmergencyGasMask", title = "EMERGENCY GAS MASK", kind = ItemKind.GasMask,
            info = "A self-rescuer mask supplies breathable air for escape when the atmosphere turns toxic.",
            question = "Toxic gas is detected. Do you equip this mask?",
            optionA = "Equip it immediately", optionB = "Ignore it", correctIsA = true,
            explanation = "Seconds matter. Equip the self-rescuer first, then move to fresh air.",
            position = new Vector3(1.8f, 1.2f, 28f), fallbackColor = new Color(0.2f, 0.8f, 0.9f), texture = maskTexture
        });

        RegisterAndSpawnItem(new MineItemData
        {
            key = "GasLeakZone", title = "TOXIC GAS ZONE", kind = ItemKind.GasLeak,
            correctIsA = true, position = new Vector3(2.0f, 1.5f, 32f), fallbackColor = new Color(0.4f, 1f, 0.4f)
        });

        // --- Escape maze ---
        RegisterAndSpawnItem(new MineItemData
        {
            key = "EscapeRouteGuide", title = "ESCAPE ROUTE >>", kind = ItemKind.EscapeSign,
            correctIsA = true, position = new Vector3(4f, 1.6f, 36f), fallbackColor = new Color(0.1f, 0.9f, 0.3f), texture = escapeRouteTexture
        });

        RegisterAndSpawnItem(new MineItemData
        {
            key = "MazeExit", title = "MAIN EXIT - SAFE ZONE", kind = ItemKind.MazeExit,
            correctIsA = true, position = new Vector3(15f, 1.4f, 36f), fallbackColor = new Color(0.1f, 0.8f, 0.3f)
        });

        RegisterAndSpawnItem(new MineItemData
        {
            key = "MazeDeadEnd", title = "COLLAPSED TUNNEL", kind = ItemKind.MazeDeadEnd,
            correctIsA = false, position = new Vector3(-15f, 1.4f, 36f), fallbackColor = new Color(0.8f, 0.1f, 0.1f)
        });

        // Maximum score = number of scorable items (skipping one now costs points).
        maxScore = 0;
        foreach (MineItemData d in itemDatabase.Values) if (d.Scorable) maxScore++;
    }

    void RegisterAndSpawnItem(MineItemData data)
    {
        itemDatabase[data.key] = data;

        if (data.position.y < 1.35f)
        {
            Material stand = CreateLitMaterial(new Color(0.32f, 0.20f, 0.10f));
            CreateBox("Crate_" + data.key, transform, new Vector3(data.position.x, 0.45f, data.position.z), new Vector3(0.7f, 0.9f, 0.7f), stand, true);
        }

        bool wide = data.kind == ItemKind.RoofFall || data.kind == ItemKind.Fire || data.kind == ItemKind.GasLeak;
        GameObject item = GameObject.CreatePrimitive(PrimitiveType.Cube);
        item.name = "Interactable_" + data.key;
        item.transform.position = data.position;
        item.transform.localScale = wide ? new Vector3(1.6f, 1.0f, 0.15f) : new Vector3(0.85f, 0.85f, 0.12f);

        // Face the player (side corridors face the junction centre)
        if (Mathf.Abs(data.position.x) > 3f) item.transform.LookAt(new Vector3(0f, data.position.y, 36f));
        else item.transform.LookAt(new Vector3(0f, data.position.y, data.position.z - 2.5f));

        // Glow-in-the-dark look. Textured signs glow in their own colours.
        float glow = data.texture != null ? 0.55f : 1.3f;
        data.material = data.texture != null ? CreateLitMaterial(Color.white, data.texture, glow) : CreateLitMaterial(data.fallbackColor, null, glow);
        data.emissionBase = (data.texture != null ? Color.white : data.fallbackColor) * glow;
        item.GetComponent<Renderer>().sharedMaterial = data.material;

        GameObject labelObj = new GameObject("Label_" + data.key);
        labelObj.transform.SetParent(item.transform);
        labelObj.transform.localPosition = new Vector3(0f, 0.65f, 0f);
        labelObj.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        TextMeshPro tmp = labelObj.AddComponent<TextMeshPro>();
        tmp.text = data.title;                                  // ASCII only: default TMP font has no emoji/arrows
        tmp.fontSize = 0.8f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = new Color(1f, 0.92f, 0.4f);
        tmp.rectTransform.sizeDelta = new Vector2(2f, 1f);

        if (data.kind == ItemKind.RoofFall) BuildRoofFallRocks(new Vector3(0f, 3.1f, 12.5f));
        if (data.kind == ItemKind.Fire) BuildFireEffect(new Vector3(-1.6f, 0f, 23f));
    }

    // ------------------------------------------------------------------
    //  HAZARD VISUALS
    // ------------------------------------------------------------------
    void BuildRoofFallRocks(Vector3 center)
    {
        Material rockMat = CreateLitMaterial(new Color(0.22f, 0.20f, 0.18f));
        Transform parent = new GameObject("RoofRocks").transform;
        for (int i = 0; i < 9; i++)
        {
            // Keep the centre lane clear: rocks hang left or right of it.
            float side = (i % 2 == 0) ? 1f : -1f;
            Vector3 pos = center + new Vector3(side * Random.Range(0.9f, 2.2f), Random.Range(-0.1f, 0.2f), Random.Range(-1.2f, 1.2f));
            float s = Random.Range(0.25f, 0.5f);
            GameObject rock = CreateBox("Rock_" + i, parent, pos, new Vector3(s, s * Random.Range(0.7f, 1f), s), rockMat, true);
            rock.transform.rotation = Random.rotation;
            rock.layer = 2;                                     // Ignore Raycast: never blocks item taps
            Rigidbody rb = rock.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.mass = 20f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            fallingRocks.Add(rb);
        }
        hazardLight = CreatePointLight("RoofHazardLight", parent, center, new Color(1f, 0.15f, 0.1f), 12f, 0f);
    }

    void BuildFireEffect(Vector3 basePos)
    {
        Transform root = new GameObject("ConveyorFire").transform;
        root.position = basePos;

        // The burning conveyor belt
        Material belt = CreateLitMaterial(new Color(0.08f, 0.08f, 0.09f));
        CreateBox("Conveyor", root, basePos + new Vector3(0f, 0.3f, 0f), new Vector3(1.0f, 0.3f, 4f), belt);
        CreateBox("ConveyorLegL", root, basePos + new Vector3(-0.4f, 0.1f, 0f), new Vector3(0.1f, 0.2f, 4f), belt);
        CreateBox("ConveyorLegR", root, basePos + new Vector3(0.4f, 0.1f, 0f), new Vector3(0.1f, 0.2f, 4f), belt);

        fireParticles = MakeParticles("Flames", root, basePos + new Vector3(0f, 0.5f, 0f),
            new Color(1f, 0.65f, 0.12f, 0.95f), new Color(0.8f, 0.1f, 0f, 0f), 0.9f, 1.6f, 0.7f, 40f, 1.0f);
        smokeParticles = MakeParticles("Smoke", root, basePos + new Vector3(0f, 1.3f, 0f),
            new Color(0.28f, 0.28f, 0.28f, 0.55f), new Color(0.04f, 0.04f, 0.04f, 0f), 4.5f, 0.9f, 1.4f, 12f, 2.0f);
        fireLight = CreatePointLight("FireLight", root, basePos + new Vector3(0f, 1f, 0f), new Color(1f, 0.5f, 0.12f), 9f, 0f);
    }

    ParticleSystem MakeParticles(string name, Transform parent, Vector3 worldPos, Color start, Color end,
                                 float lifetime, float speed, float size, float rate, float growth)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent);
        go.transform.position = worldPos;
        go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);     // cone emits upward

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);  // must be stopped before editing duration

        ParticleSystem.MainModule main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.duration = 1f;
        main.startLifetime = lifetime;
        main.startSpeed = speed;
        main.startSize = new ParticleSystem.MinMaxCurve(size * 0.6f, size);
        main.startColor = start;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 150;

        ParticleSystem.EmissionModule em = ps.emission;
        em.rateOverTime = rate;

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 14f;
        shape.radius = 0.45f;

        ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
        col.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(start, 0f), new GradientColorKey(end, 1f) },
            new[] { new GradientAlphaKey(start.a, 0f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        ParticleSystem.SizeOverLifetimeModule sz = ps.sizeOverLifetime;
        sz.enabled = true;
        sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, growth));

        go.GetComponent<ParticleSystemRenderer>().sharedMaterial = GetParticleMaterial();
        return ps;
    }

    Material GetParticleMaterial()
    {
        if (particleMaterial != null) return particleMaterial;
        if (softCircle == null)
        {
            const int n = 64;
            softCircle = new Texture2D(n, n, TextureFormat.RGBA32, false);
            softCircle.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float r = Vector2.Distance(new Vector2(x, y), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                    float a = Mathf.Pow(Mathf.Clamp01(1f - r), 2f);
                    softCircle.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            softCircle.Apply();
        }
        // Sprites/Default is always included in builds and respects vertex colour + alpha.
        Shader s = FindFirstShader("Sprites/Default", "Universal Render Pipeline/Particles/Unlit");
        particleMaterial = new Material(s);
        particleMaterial.mainTexture = softCircle;
        return particleMaterial;
    }

    // ------------------------------------------------------------------
    //  UI BUILDER
    // ------------------------------------------------------------------
    void BuildHUDAndQuizUI()
    {
        GameObject canvasObj = new GameObject("SimulationHUDCanvas");
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;
        canvasObj.AddComponent<GraphicRaycaster>();
        Transform c = canvasObj.transform;
        Vector2 mid = new Vector2(0.5f, 0.5f), topLeft = new Vector2(0f, 1f), topRight = new Vector2(1f, 1f),
                topMid = new Vector2(0.5f, 1f), botMid = new Vector2(0.5f, 0f);

        // Gas blackout overlay (fills the screen, never blocks input)
        GameObject blackoutObj = new GameObject("BlackoutOverlay");
        blackoutObj.transform.SetParent(c, false);
        blackoutImage = blackoutObj.AddComponent<Image>();
        blackoutImage.color = new Color(0f, 0f, 0f, 0f);
        blackoutImage.raycastTarget = false;
        StretchFull(blackoutObj.GetComponent<RectTransform>());

        panicText = CreateUIText("PanicText", c, mid, new Vector2(0f, 300f), new Vector2(1000f, 200f),
            "WARNING: TOXIC GAS!\nFIND THE GAS MASK FAST!", 65, TextAlignmentOptions.Center, new Color(1f, 0.2f, 0.2f, 0f));
        panicText.fontStyle = FontStyles.Bold;

        // Score (top-left)
        CreateHudBox("ScoreBG", c, topLeft, new Vector2(250f, -80f), new Vector2(450f, 100f));
        scoreText = CreateUIText("ScoreText", c.Find("ScoreBG"), mid, Vector2.zero, new Vector2(420f, 80f),
            "SCORE: 0/0", 40, TextAlignmentOptions.Center, new Color(0.4f, 0.9f, 0.4f));

        // Timer (top-right)
        CreateHudBox("TimerBG", c, topRight, new Vector2(-190f, -80f), new Vector2(330f, 100f));
        timerText = CreateUIText("TimerText", c.Find("TimerBG"), mid, Vector2.zero, new Vector2(310f, 80f),
            "TIME 00:00", 36, TextAlignmentOptions.Center, Color.white);

        // Objective line
        objectiveText = CreateUIText("ObjectiveText", c, topMid, new Vector2(0f, -200f), new Vector2(1000f, 100f),
            "", 34, TextAlignmentOptions.Center, new Color(1f, 0.92f, 0.5f));

        // Oxygen bar (only visible during the gas leak)
        o2Root = CreateHudBox("OxygenBar", c, topMid, new Vector2(0f, -300f), new Vector2(700f, 50f)).gameObject;
        GameObject fill = new GameObject("OxygenFill");
        fill.transform.SetParent(o2Root.transform, false);
        Image fillImg = fill.AddComponent<Image>();
        fillImg.color = new Color(0.9f, 0.25f, 0.2f, 0.95f);
        fillImg.raycastTarget = false;
        o2FillRT = fill.GetComponent<RectTransform>();
        o2FillRT.anchorMin = Vector2.zero; o2FillRT.anchorMax = Vector2.one;
        o2FillRT.offsetMin = new Vector2(4f, 4f); o2FillRT.offsetMax = new Vector2(-4f, -4f);
        o2LabelText = CreateUIText("OxygenLabel", o2Root.transform, mid, Vector2.zero, new Vector2(680f, 46f),
            "OXYGEN", 30, TextAlignmentOptions.Center, Color.white);
        o2Root.SetActive(false);

        // Bottom prompt + toast
        promptText = CreateUIText("PromptText", c, botMid, new Vector2(0f, 120f), new Vector2(1000f, 150f),
            "HOLD RIGHT SIDE (top = walk forward, bottom = walk back)\nDRAG LEFT SIDE or TILT PHONE to look  |  TAP ITEMS TO INSPECT",
            30, TextAlignmentOptions.Center, new Color(1f, 1f, 1f, 0.7f));
        toastText = CreateUIText("ToastText", c, botMid, new Vector2(0f, 330f), new Vector2(900f, 120f),
            "", 38, TextAlignmentOptions.Center, new Color(1f, 1f, 1f, 0f));
        toastText.fontStyle = FontStyles.Bold;

        // ---------------- Quiz / info modal ----------------
        quizPanel = new GameObject("QuizModalPanel");
        quizPanel.transform.SetParent(c, false);
        Image panelImg = quizPanel.AddComponent<Image>();
        panelImg.color = new Color(0.05f, 0.06f, 0.08f, 0.98f);
        RectTransform panelRT = quizPanel.GetComponent<RectTransform>();
        panelRT.sizeDelta = new Vector2(950f, 800f);
        panelRT.anchoredPosition = Vector2.zero;

        Transform q = quizPanel.transform;
        quizTitleText = CreateUIText("QuizTitle", q, mid, new Vector2(0f, 330f), new Vector2(900f, 80f), "TITLE", 52, TextAlignmentOptions.Center, new Color(1f, 0.85f, 0.2f));
        quizBodyText = CreateUIText("QuizBody", q, mid, new Vector2(0f, 110f), new Vector2(850f, 360f), "Info...", 40, TextAlignmentOptions.Center, new Color(0.9f, 0.9f, 0.9f));
        quizBodyText.enableAutoSizing = true;
        quizBodyText.fontSizeMin = 26f;
        quizBodyText.fontSizeMax = 40f;
        Color navy = new Color(0.12f, 0.28f, 0.45f, 1f);
        buttonA = CreateUIButton("AnswerButtonA", q, new Vector2(0f, -150f), new Vector2(800f, 110f), "Option A", navy, 38);
        buttonB = CreateUIButton("AnswerButtonB", q, new Vector2(0f, -280f), new Vector2(800f, 110f), "Option B", navy, 38);
        continueButton = CreateUIButton("ContinueBtn", q, new Vector2(0f, -230f), new Vector2(600f, 120f), "CONTINUE", new Color(0.2f, 0.6f, 0.3f, 1f), 42);
        closeButton = CreateUIButton("ClosePanelBtn", q, new Vector2(0f, -365f), new Vector2(300f, 60f), "CLOSE [X]", new Color(0.4f, 0.15f, 0.15f, 1f), 25);
        closeButton.onClick.AddListener(HidePanel);
        continueButton.gameObject.SetActive(false);
        quizPanel.SetActive(false);

        // ---------------- Results panel ----------------
        resultsPanel = new GameObject("ResultsPanel");
        resultsPanel.transform.SetParent(c, false);
        Image resImg = resultsPanel.AddComponent<Image>();
        resImg.color = new Color(0.08f, 0.09f, 0.12f, 1f);
        StretchFull(resultsPanel.GetComponent<RectTransform>());
        Transform r = resultsPanel.transform;

        resultsTitleText = CreateUIText("ResTitle", r, mid, new Vector2(0f, 420f), new Vector2(900f, 100f), "TRAINING COMPLETE", 66, TextAlignmentOptions.Center, new Color(1f, 0.85f, 0.2f));
        resultsScoreText = CreateUIText("ResScore", r, mid, new Vector2(0f, 220f), new Vector2(900f, 150f), "SCORE", 90, TextAlignmentOptions.Center, Color.white);
        resultsScoreText.enableAutoSizing = true;
        resultsScoreText.fontSizeMin = 40f;
        resultsScoreText.fontSizeMax = 90f;
        resultsMessageText = CreateUIText("ResMsg", r, mid, new Vector2(0f, -30f), new Vector2(900f, 360f), "Message", 40, TextAlignmentOptions.Center, new Color(0.8f, 0.8f, 0.8f));
        resultsMessageText.enableAutoSizing = true;
        resultsMessageText.fontSizeMin = 26f;
        resultsMessageText.fontSizeMax = 40f;

        Button retryBtn = CreateUIButton("RetryBtn", r, new Vector2(0f, -300f), new Vector2(700f, 110f), "RETRY SIMULATION", new Color(0.55f, 0.35f, 0.08f, 1f), 38);
        retryBtn.onClick.AddListener(() => SceneManager.LoadScene(SceneManager.GetActiveScene().name));
        Button returnBtn = CreateUIButton("ReturnBtn", r, new Vector2(0f, -430f), new Vector2(700f, 110f), "SAVE & RETURN TO MENU", new Color(0.12f, 0.35f, 0.65f, 1f), 38);
        returnBtn.onClick.AddListener(SaveAndReturn);
        resultsPanel.SetActive(false);
    }

    void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    RectTransform CreateHudBox(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.6f);
        img.raycastTarget = false;
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    TextMeshProUGUI CreateUIText(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size,
                                 string content, float fontSize, TextAlignmentOptions align, Color color)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        RectTransform rt = obj.AddComponent<RectTransform>();
        rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        TextMeshProUGUI tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.text = content;
        tmp.fontSize = fontSize;
        tmp.alignment = align;
        tmp.color = color;
        tmp.raycastTarget = false;
        return tmp;
    }

    Button CreateUIButton(string name, Transform parent, Vector2 pos, Vector2 size, string label, Color bg, float fontSize)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        Image img = go.AddComponent<Image>();
        img.color = bg;
        Button btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        TextMeshProUGUI tmp = CreateUIText(name + "_Label", go.transform, new Vector2(0.5f, 0.5f), Vector2.zero, size - new Vector2(20f, 20f),
            label, fontSize, TextAlignmentOptions.Center, Color.white);
        tmp.fontStyle = FontStyles.Bold;
        return btn;
    }

    void SetButtonLabel(Button b, string text)
    {
        TextMeshProUGUI t = b.GetComponentInChildren<TextMeshProUGUI>(true);
        if (t != null) t.text = text;
    }

    // ------------------------------------------------------------------
    //  HUD HELPERS
    // ------------------------------------------------------------------
    void RefreshHUD()
    {
        if (scoreText != null) scoreText.text = "SCORE: " + score + "/" + maxScore;
    }

    void SetObjective(string text)
    {
        if (objectiveText != null) objectiveText.text = "OBJECTIVE: " + text;
    }

    void ShowToast(string message, float seconds = 2.5f)
    {
        toastText.text = message;
        toastTimer = seconds;
    }

    static string FormatTime(float seconds)
    {
        int s = Mathf.FloorToInt(seconds);
        return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
    }

    void UpdateHUD(float dt)
    {
        timerText.text = "TIME " + FormatTime(elapsed);

        if (toastTimer > 0f)
        {
            toastTimer -= dt;
            toastText.color = new Color(1f, 1f, 1f, Mathf.Clamp01(toastTimer));   // fades over the last second
        }
        if (promptTimer > 0f)
        {
            promptTimer -= dt;
            promptText.color = new Color(1f, 1f, 1f, Mathf.Clamp01(promptTimer) * 0.7f);
        }
    }

    // ------------------------------------------------------------------
    //  UPDATE LOOP
    // ------------------------------------------------------------------
    void Update()
    {
        float dt = Time.deltaTime;
        if (state != SimState.Finished) elapsed += dt;

        UpdateHUD(dt);
        UpdateItemGlow();
        UpdateHazardEffects();

        bool modal = quizPanel.activeSelf || resultsPanel.activeSelf;
        UpdateGasLeak(dt, modal);

        moveInput = 0f;
        if (state != SimState.Finished)
        {
            if (modal) isPressing = false;
            else
            {
                HandleInput();
                CheckAutoTriggers();
            }
        }
        ApplyCamera(dt);
    }

    void UpdateItemGlow()
    {
        foreach (MineItemData d in itemDatabase.Values)
        {
            if (d.material == null) continue;
            bool done = answeredItems.Contains(d.key);
            float k = done ? 0.6f : 0.8f + 0.3f * Mathf.Sin(Time.time * 2.5f + d.position.z * 0.7f);
            d.material.SetColor("_EmissionColor", d.emissionBase * k);
        }
    }

    void UpdateHazardEffects()
    {
        if (fireTriggered && fireLight != null)
            fireLight.intensity = 2.2f + Mathf.PerlinNoise(Time.time * 9f, 0.3f) * 2.5f;

        if (state == SimState.Blackout && helmetFlashlight != null)
        {
            float n = Mathf.PerlinNoise(Time.time * 7f, 1.7f);
            float dropout = n < 0.12f ? 0.3f : 1f;               // lamp occasionally stutters
            helmetFlashlight.intensity = 6f * (0.92f + 0.08f * n) * dropout;
        }
    }

    void UpdateGasLeak(float dt, bool modal)
    {
        if (state != SimState.GasLeak) return;

        if (!modal) gasTimer -= dt;                               // timer waits while the intro panel is open
        float frac = Mathf.Clamp01(gasTimer / maxGasTime);
        float darkness = 1f - frac;

        blackoutImage.color = new Color(0f, 0f, 0f, darkness * 0.95f);
        panicText.color = new Color(1f, 0.2f, 0.2f, modal ? 0f : Mathf.Abs(Mathf.Sin(Time.time * 5f)));
        o2FillRT.anchorMax = new Vector2(Mathf.Max(0.001f, frac), 1f);

        if (!modal)
        {
            beepTimer -= dt;
            if (beepTimer <= 0f)
            {
                Play(beepClip, 0.8f);
                beepTimer = Mathf.Lerp(1f, 0.25f, darkness);       // alarm speeds up as oxygen drops
            }
        }

        if (gasTimer <= 0f) GameOver("OXYGEN DEPLETED. YOU PASSED OUT.");
    }

    // ------------------------------------------------------------------
    //  INPUT + MOVEMENT
    // ------------------------------------------------------------------
    void HandleInput()
    {
        // Gyro look (new Input System; rad/s -> deg)
        if (useGyro && gyroDevice != null && gyroDevice.added)
        {
            Vector3 rate = gyroDevice.angularVelocity.ReadValue();
            if (rate.magnitude > 0.03f)                            // deadzone to cut drift
            {
                Vector3 deg = rate * Mathf.Rad2Deg * Time.deltaTime * gyroSensitivity;
                yaw += (gyroInvertYaw ? 1f : -1f) * deg.y;
                pitch += (gyroInvertPitch ? 1f : -1f) * deg.x;
            }
        }

        Pointer p = Pointer.current;
        if (p == null) return;
        Vector2 pos = p.position.ReadValue();

        if (p.press.wasPressedThisFrame)
        {
            isPressing = true;
            hasDragged = false;
            pressStartTime = Time.time;
            pointerDownPos = pos;
            lastPointerPos = pos;
            pressOnRight = pos.x > Screen.width * 0.5f;           // zone is locked when the finger lands
        }
        else if (isPressing && p.press.isPressed)
        {
            if (pressOnRight)
            {
                // Walk zone: top half forward, bottom half back. Finger jitter no longer turns into "look".
                moveInput = pos.y > Screen.height * 0.5f ? 1f : -1f;
            }
            else
            {
                if (hasDragged || Vector2.Distance(pos, pointerDownPos) > DragThresholdPx)
                {
                    hasDragged = true;
                    Vector2 delta = pos - lastPointerPos;
                    yaw += delta.x * lookSensitivity;
                    pitch -= delta.y * lookSensitivity;
                }
                lastPointerPos = pos;
            }
        }
        else if (isPressing && p.press.wasReleasedThisFrame)
        {
            isPressing = false;
            bool tap = Vector2.Distance(pos, pointerDownPos) < TapMaxPx && (Time.time - pressStartTime) < TapMaxTime;
            if (tap) TryClick3DObject(pos);                        // taps work on either half of the screen
        }

        if (moveInput != 0f)
        {
            float speed = state == SimState.GasLeak ? gasWalkSpeed : walkSpeed;
            Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;   // yaw-based: no flip when looking down
            baseCameraPosition += fwd * moveInput * speed * Time.deltaTime;
            baseCameraPosition = ClampToWalkable(baseCameraPosition);
            bobPhase += Time.deltaTime * speed * 2.6f;
        }
    }

    Vector3 ClampToWalkable(Vector3 p)
    {
        // Until the gas event is resolved the player cannot enter the junction.
        float maxZ = mazeUnlocked ? MazeMaxZ : JunctionZ - 0.5f;
        p.z = Mathf.Clamp(p.z, -9f, maxZ);
        if (p.z > JunctionZ) p.x = Mathf.Clamp(p.x, -15.2f, 15.2f);
        else p.x = Mathf.Clamp(p.x, -2.4f, 2.4f);
        // Inside a side branch you cannot walk back through its wall.
        if (Mathf.Abs(p.x) > 2.4f) p.z = Mathf.Max(p.z, JunctionZ + 0.1f);
        return p;
    }

    void ApplyCamera(float dt)
    {
        pitch = Mathf.Clamp(pitch, -80f, 80f);
        float darkness = state == SimState.GasLeak ? 1f - Mathf.Clamp01(gasTimer / maxGasTime) : 0f;
        float roll = Mathf.Sin(Time.time * 0.8f) * darkness * 4f;              // dizziness
        mainCam.transform.rotation = Quaternion.Euler(pitch, yaw, roll);

        bobBlend = Mathf.MoveTowards(bobBlend, moveInput != 0f ? 1f : 0f, dt * 5f);
        shakeAmount = Mathf.MoveTowards(shakeAmount, 0f, dt * 0.25f);
        Vector3 bob = new Vector3(0f, Mathf.Sin(bobPhase) * 0.035f * bobBlend, 0f);
        Vector3 shake = Random.insideUnitSphere * (shakeAmount + darkness * 0.05f);
        mainCam.transform.position = baseCameraPosition + bob + shake;
    }

    void CheckAutoTriggers()
    {
        if (state != SimState.Exploring) return;
        float z = baseCameraPosition.z;
        if (!roofFallTriggered && z >= RoofTriggerZ) { OpenInspectionAndQuiz(itemDatabase["RoofFallHazard"]); return; }
        if (!fireTriggered && z >= FireTriggerZ) { OpenInspectionAndQuiz(itemDatabase["FireHazard"]); return; }
        if (!gasTriggered && z >= GasTriggerZ) StartGasLeak();
    }

    void TryClick3DObject(Vector2 screenPosition)
    {
        if (state == SimState.Finished || quizPanel.activeSelf) return;
        Ray ray = mainCam.ScreenPointToRay(screenPosition);
        // SphereCast = forgiving for fingers; layer 2 (rocks) is ignored.
        RaycastHit[] hits = Physics.SphereCastAll(ray, 0.12f, 25f, ~(1 << 2));
        MineItemData found = null;
        float best = float.MaxValue;
        foreach (RaycastHit h in hits)
        {
            string n = h.collider.gameObject.name;
            if (!n.StartsWith("Interactable_") || h.distance >= best) continue;
            MineItemData d;
            if (itemDatabase.TryGetValue(n.Substring("Interactable_".Length), out d)) { found = d; best = h.distance; }
        }
        if (found != null) OpenInspectionAndQuiz(found);
    }

    // ------------------------------------------------------------------
    //  GAME LOGIC
    // ------------------------------------------------------------------
    void OpenInspectionAndQuiz(MineItemData data)
    {
        if (state == SimState.Finished) return;

        switch (data.kind)
        {
            case ItemKind.GasLeak:
                if (!gasTriggered) StartGasLeak();
                else if (state == SimState.GasLeak) ShowToast("Find and equip the GAS MASK!");
                return;

            case ItemKind.GasMask:
                if (state == SimState.GasLeak) { EquipMaskDuringGas(data); return; }
                if (maskEquipped) { ShowToast("Mask already equipped."); return; }
                break;

            case ItemKind.EscapeSign:
                ShowToast("Escape route marker: follow the glowing signs to the main exit.");
                return;

            case ItemKind.MazeExit:
                if (mazeUnlocked) Finish(); else ShowToast("Deal with the hazards ahead first.");
                return;

            case ItemKind.MazeDeadEnd:
                if (mazeUnlocked) GameOver("WRONG TURN! YOU ARE TRAPPED BY THE CAVE-IN.");
                return;
        }

        if (answeredItems.Contains(data.key)) { ShowToast("Already completed."); return; }

        if (data.kind == ItemKind.RoofFall || data.kind == ItemKind.Fire)
        {
            if (hazardQuizPending) return;
            if (data.kind == ItemKind.RoofFall) TriggerRoofFall(); else TriggerFire();
            StartCoroutine(OpenHazardQuizAfter(data, 1.2f));      // let the player see/hear it first
            return;
        }

        ShowQuiz(data, false);
    }

    private bool hazardQuizPending;

    IEnumerator OpenHazardQuizAfter(MineItemData data, float delay)
    {
        hazardQuizPending = true;
        yield return new WaitForSeconds(delay);
        hazardQuizPending = false;
        if (state != SimState.Finished) ShowQuiz(data, true);     // mandatory: no close button
    }

    void TriggerRoofFall()
    {
        if (roofFallTriggered) return;
        roofFallTriggered = true;
        foreach (Rigidbody rb in fallingRocks)
        {
            if (rb == null) continue;
            rb.isKinematic = false;
            rb.AddTorque(Random.insideUnitSphere * 5f, ForceMode.Impulse);
        }
        Play(rumbleClip);
        shakeAmount = Mathf.Max(shakeAmount, 0.12f);
        Vibrate();
        if (hazardLight != null) StartCoroutine(FlashLight(hazardLight, 3f, 1.6f));
        ShowToast("ROOF COLLAPSE AHEAD!", 3f);
    }

    void TriggerFire()
    {
        if (fireTriggered) return;
        fireTriggered = true;
        if (fireParticles != null) fireParticles.Play();
        if (smokeParticles != null) smokeParticles.Play();
        Play(rumbleClip, 0.6f);
        StartCoroutine(FadeFog(0.065f, 6f));
        ShowToast("FIRE ON THE CONVEYOR!", 3f);
    }

    IEnumerator FlashLight(Light l, float peak, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            l.intensity = peak * Mathf.Abs(Mathf.Sin(t * 10f)) * (1f - t / seconds);
            yield return null;
        }
        l.intensity = 0f;
    }

    IEnumerator FadeFog(float target, float seconds)
    {
        float start = RenderSettings.fogDensity, t = 0f;
        while (t < seconds)
        {
            if (state == SimState.Blackout) yield break;          // blackout owns the fog from here
            t += Time.deltaTime;
            RenderSettings.fogDensity = Mathf.Lerp(start, target, t / seconds);
            yield return null;
        }
    }

    // ----- Quiz -----
    void ShowQuiz(MineItemData data, bool mandatory)
    {
        StopQuizCoroutine();
        quizPanel.SetActive(true);
        quizTitleText.text = data.title;
        quizBodyText.text = data.info + "\n\n<color=#FFD966><i>Read the safety info... question in " + quizReadDelay.ToString("0.#") + "s</i></color>";
        buttonA.gameObject.SetActive(false);
        buttonB.gameObject.SetActive(false);
        continueButton.gameObject.SetActive(false);
        closeButton.gameObject.SetActive(!mandatory);
        activeQuizCoroutine = StartCoroutine(ShowQuestionAfterDelay(data, quizReadDelay, mandatory));
    }

    IEnumerator ShowQuestionAfterDelay(MineItemData data, float delay, bool mandatory)
    {
        yield return new WaitForSeconds(delay);

        bool flip = shuffleAnswers && Random.value > 0.5f;        // so the answer is not always "A"
        string textA = flip ? data.optionB : data.optionA;
        string textB = flip ? data.optionA : data.optionB;
        bool aIsCorrect = flip ? !data.correctIsA : data.correctIsA;

        quizBodyText.text = "<color=#9CA3AF>SAFETY QUESTION:</color>\n<b>" + data.question + "</b>";
        buttonA.gameObject.SetActive(true);
        buttonB.gameObject.SetActive(true);
        closeButton.gameObject.SetActive(false);
        SetButtonLabel(buttonA, "A) " + textA);
        SetButtonLabel(buttonB, "B) " + textB);
        buttonA.onClick.RemoveAllListeners();
        buttonB.onClick.RemoveAllListeners();
        buttonA.onClick.AddListener(() => OnAnswerChosen(data, aIsCorrect));
        buttonB.onClick.AddListener(() => OnAnswerChosen(data, !aIsCorrect));
        activeQuizCoroutine = null;
    }

    void OnAnswerChosen(MineItemData data, bool isCorrect)
    {
        RecordScore(data.key, isCorrect);
        if (data.kind == ItemKind.GasMask && isCorrect) maskEquipped = true;
        Play(isCorrect ? correctClip : wrongClip);

        string body;
        if (isCorrect)
        {
            body = "<color=#4ADE80><b>CORRECT!</b></color>";
        }
        else
        {
            string right = data.correctIsA ? data.optionA : data.optionB;
            body = "<color=#F87171><b>NOT QUITE.</b></color>\nCorrect answer: <b>" + right + "</b>";
        }
        if (!string.IsNullOrEmpty(data.explanation)) body += "\n\n" + data.explanation;

        ShowInfoPanel(data.title, body, "CONTINUE", HidePanel);
    }

    bool RecordScore(string key, bool correct)
    {
        if (!answeredItems.Add(key)) return false;                // each item scores once
        totalAnswered++;
        if (correct) score++;
        RefreshHUD();
        return true;
    }

    void ShowInfoPanel(string title, string body, string buttonLabel, UnityAction onContinue)
    {
        StopQuizCoroutine();
        quizPanel.SetActive(true);
        quizTitleText.text = title;
        quizBodyText.text = body;
        buttonA.gameObject.SetActive(false);
        buttonB.gameObject.SetActive(false);
        closeButton.gameObject.SetActive(false);
        continueButton.gameObject.SetActive(true);
        SetButtonLabel(continueButton, buttonLabel);
        continueButton.onClick.RemoveAllListeners();
        continueButton.onClick.AddListener(onContinue);
    }

    void HidePanel()
    {
        StopQuizCoroutine();
        quizPanel.SetActive(false);
    }

    void StopQuizCoroutine()
    {
        if (activeQuizCoroutine != null) { StopCoroutine(activeQuizCoroutine); activeQuizCoroutine = null; }
    }

    // ----- Gas leak -> blackout maze -----
    void StartGasLeak()
    {
        if (gasTriggered) return;
        gasTriggered = true;

        if (maskEquipped)                                         // already prepared: protected, go straight to the maze
        {
            ShowToast("Toxic gas detected, but your mask protects you.", 3f);
            BeginBlackout();
            return;
        }

        state = SimState.GasLeak;
        gasTimer = maxGasTime;
        beepTimer = 0f;
        o2Root.SetActive(true);
        Vibrate();
        SetObjective("GAS LEAK! Find and equip the GAS MASK behind you!");
        ShowInfoPanel("LETHAL GAS DETECTED",
            "<color=#F87171>OXYGEN LEVELS DROPPING CRITICALLY.</color>\n\nFIND AND EQUIP THE EMERGENCY GAS MASK BEHIND YOU IMMEDIATELY!",
            "RUN!", HidePanel);
    }

    void EquipMaskDuringGas(MineItemData data)
    {
        RecordScore(data.key, true);
        maskEquipped = true;
        Play(correctClip);
        BeginBlackout();
    }

    void ClearGasVisuals()
    {
        blackoutImage.color = new Color(0f, 0f, 0f, 0f);
        panicText.color = new Color(1f, 0.2f, 0.2f, 0f);
        o2Root.SetActive(false);
    }

    void BeginBlackout()
    {
        state = SimState.Blackout;
        mazeUnlocked = true;
        ClearGasVisuals();

        foreach (Light l in tunnelLights) if (l != null) l.intensity = 0f;
        RenderSettings.ambientLight = Color.black;
        RenderSettings.fogDensity = 0.15f;                         // thick smoke / darkness
        if (helmetFlashlight != null) helmetFlashlight.intensity = 6f;

        Play(rumbleClip);
        shakeAmount = Mathf.Max(shakeAmount, 0.2f);
        Vibrate();

        SetObjective("Follow the GLOWING signs to the MAIN EXIT. Avoid dead ends!");
        ShowInfoPanel("POWER FAILURE",
            "<color=#4ADE80><b>MASK EQUIPPED!</b></color>\n\nAn explosion knocked out the main power and the tunnel is pitch black.\n\nUse your helmet lamp to find the <color=#FFD966>GLOWING SIGNS</color> and navigate the maze to the exit!",
            "ENTER THE MAZE", HidePanel);
    }

    // ----- End of session -----
    void GameOver(string reason)
    {
        if (state == SimState.Finished) return;
        state = SimState.Finished;
        ClearGasVisuals();
        HidePanel();
        Play(wrongClip);
        Vibrate();

        resultsPanel.SetActive(true);
        resultsTitleText.text = "SIMULATION FAILED";
        resultsTitleText.color = new Color(1f, 0.35f, 0.3f);
        resultsScoreText.text = "STATUS: CRITICAL FAILURE";
        resultsScoreText.color = new Color(1f, 0.2f, 0.2f);
        resultsMessageText.text = "<color=#F87171>" + reason + "</color>\n\nYou must act faster under pressure in high-risk zones.\n\n"
            + "Score " + score + "/" + maxScore + "   |   Time " + FormatTime(elapsed);
    }

    void Finish()
    {
        if (state == SimState.Finished) return;
        state = SimState.Finished;
        ClearGasVisuals();
        HidePanel();
        Play(correctClip);

        float pct = maxScore > 0 ? (float)score / maxScore : 0f;   // no divide-by-zero
        bool passed = pct >= passPercent;

        string missed = "";
        foreach (MineItemData d in itemDatabase.Values)
            if (d.Scorable && !answeredItems.Contains(d.key)) missed += (missed.Length > 0 ? ", " : "") + d.title;

        resultsPanel.SetActive(true);
        resultsTitleText.text = "TRAINING COMPLETE";
        resultsTitleText.color = new Color(1f, 0.85f, 0.2f);
        resultsScoreText.text = "FINAL SCORE: " + score + " / " + maxScore;
        resultsScoreText.color = Color.white;

        string msg = passed
            ? "<color=#4ADE80>Excellent work!</color>\nYou survived the hazards and escaped the maze."
            : "<color=#F87171>Training failed.</color>\nPlease retake the safety induction.";
        msg += "\n\nAccuracy " + Mathf.RoundToInt(pct * 100f) + "%   |   Time " + FormatTime(elapsed);
        if (missed.Length > 0) msg += "\n<color=#FFD966>Not inspected: " + missed + "</color>";
        resultsMessageText.text = msg;
    }

    void SaveAndReturn()
    {
        if (!sessionSaved)
        {
            sessionSaved = true;
            if (BackendSync.Instance != null) BackendSync.Instance.SaveAndSyncScore(score, maxScore);
        }
        if (Application.CanStreamedLevelBeLoaded(mainMenuSceneName)) SceneManager.LoadScene(mainMenuSceneName);
        else Debug.LogError("Scene '" + mainMenuSceneName + "' is not in Build Profiles.");
    }
}