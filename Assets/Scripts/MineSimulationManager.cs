// =====================================================================================
//  MineSimulationManager.cs
//  CoalMineVR - Industrial VR Disaster & Evacuation Simulation Engine
//  VIT Pune | CSE (AI & ML) | Guide: Prof. Kalyani Ghuge
//
//  Engine  : Unity 6 (6000.x) + URP 17.x | Android 10+ (API 29+), IL2CPP, ARM64
//  Input   : New Input System only (Touchscreen / Pointer / Keyboard / Gyroscope)
//  Scene   : VRMode (Build index 2) -> returns to MainMenu
//
//  ZERO-ASSET ARCHITECTURE
//  Every mesh, material, texture, light, particle system, sound and UI element in this
//  drill is generated in code at runtime. Drop this component on an empty GameObject in
//  an empty scene and press Play.
//
//  DRILL FLOW (DGMS emergency evacuation, 4 scored competency checks)
//   Phase 1  Shift Walk-In & Strata Sounding     -> Check 1: sound the roof, support or advance
//   Phase 2  Strata Failure, Tremor & Cave-In     -> Check 2: retreat under supported roof
//   Phase 3  Toxic Gas, Blackout & Hypoxia Sprint -> Check 3: don the self-rescuer (25 s O2)
//   Phase 4  Cap-Lamp Navigation & T-Junction     -> Check 4: choose the intake escapeway
//   Phase 5  Shaft Cage, Ascent & DGMS Debrief    -> BackendSync + return to MainMenu
//
//  CONTROLS
//   Phone : left half = floating move joystick | right half = drag to look, tap to interact
//           gyroscope adds look on top | INTERACT button when an object is in focus
//           Android BACK twice = return to MainMenu
//   Editor: WASD / arrows move, Q/E or mouse-drag look, click or F to interact,
//           ESC twice = MainMenu, developer phase panel top-left
//
//  AR MARKER WALK-THROUGH MODE
//   The main menu can start this scene with PlayerPrefs "CMVR_UseMarker" = 1, meaning the
//   trainee printed the MineAnchorMarker image and put it on the floor. In that mode an
//   ARTrackedImageManager is created at runtime (AR Foundation 6 + ARCore): the on-screen
//   prompt asks the trainee to point the camera at the marker, and the first locked
//   detection parents MineEnvironment to the marker so the whole mine tunnel is anchored
//   to the printed image at true room scale (arMineScale = 1 by default). Physical walking
//   then translates the trainee through the tunnel via the XROrigin / TrackedPoseDriver
//   camera; the on-screen joystick & gyroscope become a fallback that slides the anchored
//   world under the camera. If AR is unsupported (editor play mode, no ARCore, missing
//   library, no camera permission) the scene cleanly falls back to the classic mode.
//   All 5 DGMS phases, the wear-mask button and every existing control are unchanged.
//
//  v2 UI / UX POLISH
//   - Critical-action pulse: UpdateCriticalPulse() drives a smooth, continuous breathing
//     scale from Mathf.Sin(Time.time * 6f) on the WEAR MASK button (plus a soft halo and a
//     brightening tint), with the amplitude rising as the breathable air runs out. The
//     context action button pulses only while it offers a life-saving interaction.
//   - Audio + haptic feedback: a dedicated PASS chime (clipPass) and equipment latch
//     (clipLatch) are synthesised alongside the existing bank, and Buzz(Haptic) plays
//     distinct amplitude-controlled vibration waveforms (Tick / Confirm / Success /
//     Warning / Failure / Impact) through android.os.VibrationEffect, falling back to
//     Handheld.Vibrate() on any device that refuses the service.
//   - UI scaling integrity: one balanced type scale across the HUD, the decision card and
//     the debrief certificate, with tight auto-size ranges so no label collapses to
//     unreadable or wraps awkwardly at 1080 x 1920 and up.
//   - All on-screen text is plain ASCII so it renders in the default TMP font.
//
//  BUILD NOTE: shaders are looked up with Shader.Find, so make sure
//  "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit" and "Sprites/Default"
//  are in Project Settings > Graphics > Always Included Shaders (or used by any material in
//  the build). TextMesh Pro Essentials must be imported (default font asset).
//  For the AR marker mode, enable ARCore in XR Plug-in Management and add the URP
//  "ARBackgroundRendererFeature" to your Universal Renderer so the camera feed is drawn.
// =====================================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Unity.XR.CoreUtils;                              // XROrigin (AR marker mode)
using UnityEngine.InputSystem.XR;                     // TrackedPoseDriver (AR marker mode)
using UnityEngine.XR.ARFoundation;                    // ARSession / ARTrackedImageManager
using UnityEngine.XR.ARSubsystems;                    // XRReferenceImageLibrary / RuntimeReferenceImageLibrary
using TMPro;

public class MineSimulationManager : MonoBehaviour
{
    // ---------------------------------------------------------------- Inspector
    [Header("Trainee & Session")]
    [SerializeField] private string traineeId = "EMP-1042";
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    [SerializeField, Range(0.1f, 1f)] private float passPercent = 0.75f;

    [Header("Movement & Look")]
    [SerializeField] private float walkSpeed = 2.6f;
    [SerializeField] private float hypoxiaWalkSpeed = 1.8f;
    [SerializeField] private float touchLookSensitivity = 0.12f;
    [SerializeField] private float mouseLookSensitivity = 0.18f;
    [SerializeField] private float joystickRadius = 120f;
    [SerializeField] private bool useGyro = true;
    [SerializeField] private float gyroSensitivity = 1.0f;

    [Header("Emergency Timing")]
    [SerializeField] private float oxygenSeconds = 60f; // gas-leak reaction window (floored at 60 s in code so old scene values can't shorten it)
    [SerializeField] private float decisionSeconds = 15f;
    [SerializeField] private float caveInDecisionSeconds = 7f;
    [SerializeField, Range(0.1f, 1f)] private float respiratorFullCreditFraction = 0.5f;
    [SerializeField] private bool shuffleOptions = true;

    [Header("Developer")]
    [SerializeField] private bool showDebugPanel = false;

    // ---------------------------------------------------------------- AR marker walk-through mode
    // Player-driven: the main menu writes PlayerPrefs "CMVR_UseMarker" = 1 when the trainee
    // chose "START WITH AR MARKER". Everything below is only used in that mode.
    [Header("AR Marker Mode (used when PlayerPrefs CMVR_UseMarker = 1)")]
    [Tooltip("Optional XR Reference Image Library containing an image named MineAnchorMarker. If empty, Resources/MineAnchorMarkerLibrary is tried, then arMarkerTexture.")]
    [SerializeField] private XRReferenceImageLibrary arMarkerLibrary;
    [Tooltip("Fallback: a readable (Read/Write enabled) texture of the printed marker. Used to build a runtime library when no arMarkerLibrary is assigned.")]
    [SerializeField] private Texture2D arMarkerTexture;
    [Tooltip("Must match the name of the image in the library / texture. This is what the trainee prints and puts on the floor.")]
    [SerializeField] private string arMarkerImageName = "MineAnchorMarker";
    [Tooltip("Physical width of the printed marker in metres. Must match the printed size, otherwise the room-scale tunnel is scaled wrong.")]
    [SerializeField, Range(0.05f, 1f)] private float arMarkerWidthMeters = 0.25f;
    [Tooltip("Uniform world scale applied to MineEnvironment when it is anchored to the marker. 1 = true room scale (walk the tunnel 1:1). Values other than 1 can trigger rigidbody scale warnings on the loose rocks.")]
    [SerializeField, Range(0.2f, 2f)] private float arMineScale = 1f;

    // ---------------------------------------------------------------- Layout constants (metres)
    private const float DriftStartZ = -10f;
    private const float DriftEndZ = 34.5f;     // drift meets the cross-cut here
    private const float DriftHalfWidth = 2.5f;
    private const float RoofHeight = 3.4f;
    private const float WalkHalfWidth = 1.85f;
    private const float XcutZ = 37f;           // centre line of the T-junction cross-cut
    private const float XcutHalfDepth = 2.5f;
    private const float XcutMinX = -18f;
    private const float XcutMaxX = 17.5f;
    private const float SoundingGateZ = 4.3f;
    private const float SlabZ = 6.4f;
    private const float CaveTriggerZ = 15.5f;
    private const float CollapseZ = 12.2f;
    private const float StationZ = 27f;
    private const float ShaftX = 15.2f;
    private const float EyeHeight = 1.68f;

    private const float GateRaisedY = 3.75f;
    private const float GateClosedY = 1.12f;

    private const int TotalDecisions = 4;
    private const float PointsPerCheck = 25f;

    // ---------------------------------------------------------------- Types
    private enum Phase { ShiftWalkIn, StrataFailure, ToxicGas, Wayfinding, Evacuation, Debrief }

    private static readonly string[] PhaseNames =
    {
        "PHASE 1  |  SHIFT WALK-IN",
        "PHASE 2  |  STRATA FAILURE",
        "PHASE 3  |  TOXIC GAS - BLACKOUT",
        "PHASE 4  |  ESCAPE WAYFINDING",
        "PHASE 5  |  SHAFT EVACUATION",
        "DEBRIEF"
    };

    private class Interactable
    {
        public string label;
        public string action;
        public Collider collider;
        public Material glowMat;
        public Color glowColor;
        public bool active = true;
        public float range = 4f;
        public System.Action onUse;
    }

    private class Competency
    {
        public string code;
        public string title;
        public bool done;
        public bool correct;
        public float points;
        public string note = "Not reached";
    }

    // ---------------------------------------------------------------- Runtime state
    private Phase phase = Phase.ShiftWalkIn;
    private float elapsed;
    private bool cinematicLock;
    private string outcomeReason = "";
    private bool survived;

    // Camera / player
    private Camera cam;
    private Light capLamp;
    private Vector3 basePos;
    private float yaw, pitch, bobPhase, shake, heightOffset;
    private float minZ = DriftStartZ + 1f, maxZ = SoundingGateZ;
    private Vector2 moveVec;

    // Input
    private UnityEngine.InputSystem.Gyroscope gyro;
    private int joyTouchId = -1, lookTouchId = -1;
    private Vector2 joyOrigin, joyCurrent, lookStart, lookLast;
    private float lookStartTime;
    private bool lookDragged;
    private bool mouseHeld, mouseDragged;
    private Vector2 mouseStart, mouseLast;
    private float mouseStartTime;
    private float lastBackPress = -10f;
    private readonly List<RaycastResult> uiHits = new List<RaycastResult>();

    // Interactables
    private readonly Dictionary<Collider, Interactable> interactables = new Dictionary<Collider, Interactable>();
    private Interactable focused, slabIt, stationIt;

    // Competencies
    private Competency[] comps;

    // Hazard flags
    private bool soundingDone, caveInStarted, gasStarted, maskOn, cageEntered, gasArmed;
    private Button maskButton;
    private Coroutine capFailRoutine;
    private float maskNudgeTimer;
    private bool wayfindCommitted, leftWarned, resultsSynced;
    private float gasTimer, gasStartTime, maskDonFraction;
    private string syncStatus = "";

    // Scene references
    private Transform envRoot, staticRoot;
    private readonly List<Light> roofLights = new List<Light>();
    private readonly List<Material> lampLensMats = new List<Material>();
    private Material lampLensOff;
    private Transform strataSlab;
    private GameObject soundingTape;
    private readonly List<Transform> rubble = new List<Transform>();
    private readonly List<Vector3> rubbleTargets = new List<Vector3>();
    private readonly List<Rigidbody> looseRocks = new List<Rigidbody>();
    private ParticleSystem dustBurst, gritFall, gasHaze, sparks, leftSmoke, slabDust;
    private readonly List<Light> beaconLights = new List<Light>();
    private readonly List<Material> beaconMats = new List<Material>();
    private Transform brattice;
    private Transform cage, cageGate;
    private Light stationLight, cageLight;

    // AR marker mode runtime state (all null/unused when not in marker mode).
    private const string UseMarkerPrefKey = "CMVR_UseMarker";
    private static readonly Vector3 ArStartLocal = new Vector3(0f, EyeHeight, -8f); // basePos expressed in mine-space (matches SetupCamera spawn)
    private bool arMarkerMode;      // true = started with marker, AR rig is being attempted
    private bool arRigBuilt;        // ARSession + TrackedPoseDriver live; never drive cam rotation/position directly
    private bool arAnchored;        // MineEnvironment successfully parented to the tracked marker
    private bool arScanSkipped;     // trainee tapped SKIP -> classic mode fallback
    private GameObject arRigRoot;   // "ARCameraRig" (XROrigin) - destroyed on fallback
    private GameObject arSessionGO; // "ARSession" - destroyed on fallback
    private ARSession arSession;
    private GameObject arScanOverlay; // full-screen scanning UI
    private TextMeshProUGUI arScanStatus;
    private XROrigin arOrigin;
    private ARTrackedImageManager arImageManager;
    private Camera arCamera;
    private Transform arAnchor;     // yaw-only upright parent representing the marker in the room
    private Vector3 arAnchorHomePos;
    private Quaternion arAnchorHomeRot = Quaternion.identity;
    private Vector3 arWorldDrift;   // accumulated slide applied to arAnchor (joystick fallback while anchored)
    private Color arSavedFogColor;
    private float arSavedFogDensity;
    private bool arSavedFog;
    private UnityEngine.Rendering.AmbientMode arSavedAmbientMode;
    private Color arSavedAmbient;
    private bool arEnvSaved;        // fog/ambient snapshot taken before AR overrides them

    // Textures / materials / sprites
    private Texture2D texCoal, texRock, texGravel, texWood, texRust, texConcrete;
    private Material particleMat;
    private Sprite sprRound, sprRoundLine, sprCircle, sprRing, sprVignette, sprStripes, sprSoft;

    // Audio
    private AudioSource sfx, ambSrc, alarmSrc, breathSrc, winchSrc;
    private AudioClip clipVent, clipCreak, clipSnap, clipCaveIn, clipAlarm, clipBreathHeavy, clipRespirator,
                      clipChime, clipWinch, clipDrummy, clipClang, clipSpark, clipHiss, clipBeep, clipGood, clipBad, clipTick,
                      clipPass, clipLatch;

    // ---------------------------------------------------------------- UI palette
    private static readonly Color UIPanelBg = new Color(0.055f, 0.06f, 0.068f, 0.9f);
    private static readonly Color UIPanelSolid = new Color(0.07f, 0.075f, 0.085f, 0.98f);
    private static readonly Color UILine = new Color(1f, 1f, 1f, 0.09f);
    private static readonly Color UIAmber = new Color(0.96f, 0.67f, 0.05f);
    private static readonly Color UIRed = new Color(0.9f, 0.28f, 0.27f);
    private static readonly Color UIGreen = new Color(0.25f, 0.84f, 0.52f);
    private static readonly Color UITextCol = new Color(0.93f, 0.94f, 0.95f);
    private static readonly Color UIMuted = new Color(0.6f, 0.63f, 0.67f);

    // UI references
    private Canvas canvas;
    private CanvasScaler scaler;
    private RectTransform safeRoot;
    private Rect lastSafeArea;
    private Vector2Int lastScreen;
    private TextMeshProUGUI phaseText, timerText, objectiveText, toastText, hintText, focusLabel, actionLabel;
    private TextMeshProUGUI o2Text, ch4Text, coText, o2DetText, detStatusText;
    private Image vignette, alarmFlash, fadeImg, o2Fill, o2Frame, reticleRing, reticleDot, joyBase, joyKnob, detHeader;
    private Image hypoxiaDim;
    private TextMeshProUGUI o2Label;
    private RectTransform joyLayerRT;
    private RectTransform o2Group, joyBaseRT, joyKnobRT, reticleRT, toastRT;
    private Image toastBg;
    private Button actionButton;
    // Critical-action pulse: the WEAR MASK button and its soft halo breathe together so the
    // trainee's eye is pulled to the life-saving control while the screen is going dark.
    private Image maskHalo, maskBtnImg;
    private TextMeshProUGUI maskLabel;
    private Color maskBtnBase = new Color(0.1f, 0.62f, 0.32f, 0.97f);
    private float toastTimer, hintTimer = 16f;
    private Vector2 joyRestPos = new Vector2(230f, 230f);

    // Decision card
    private GameObject cardRoot, cardQuestionGroup, cardFeedbackGroup;
    private TextMeshProUGUI cardStep, cardTitle, cardBody, cardTimerText, optAText, optBText;
    private TextMeshProUGUI cardResultText, cardExplainText, cardRefText;
    private Image cardTimerFill, cardResultBanner;
    private Button optA, optB, cardContinue;
    private bool decisionOpen;
    private Coroutine decisionRoutine;

    // Debrief
    private GameObject debriefRoot;
    private TextMeshProUGUI dbStamp, dbOutcome, dbTime, dbDecisions, dbScore, dbO2, dbSync, dbMeta;
    private Image dbStampFrame;
    private readonly TextMeshProUGUI[] dbRowStatus = new TextMeshProUGUI[TotalDecisions];
    private readonly TextMeshProUGUI[] dbRowNote = new TextMeshProUGUI[TotalDecisions];
    private readonly Image[] dbRowChip = new Image[TotalDecisions];
    private bool debriefOpen;

    // =====================================================================================
    //  LIFECYCLE
    // =====================================================================================
    void CleanStraySceneObjects()
    {
        MineSimulationManager[] allManagers = FindObjectsByType<MineSimulationManager>();
        for (int i = 0; i < allManagers.Length; i++)
        {
            if (allManagers[i] != this)
            {
                Debug.LogWarning("[MineSimulationManager] Disabling duplicate manager on " + allManagers[i].gameObject.name);
                allManagers[i].enabled = false;
                allManagers[i].gameObject.SetActive(false);
            }
        }

        GameObject[] rootObjs = SceneManager.GetActiveScene().GetRootGameObjects();
        for (int i = 0; i < rootObjs.Length; i++)
        {
            GameObject go = rootObjs[i];
            if (go == gameObject || go.name == "Main Camera" || go.name == "EventSystem" || go.name == "Directional Light" || go.name == "BackendManager")
                continue;

            string lower = go.name.ToLower();
            if (lower == "minemanager" || lower.StartsWith("cube") || lower.Contains("stray") || lower == "obstacle")
            {
                Debug.LogWarning("[MineSimulationManager] Disabling stray root scene object: " + go.name);
                go.SetActive(false);
            }
            else
            {
                MeshFilter mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.name.ToLower().Contains("cube") && go.GetComponent<MineSimulationManager>() == null)
                {
                    Debug.LogWarning("[MineSimulationManager] Disabling stray root cube primitive: " + go.name);
                    go.SetActive(false);
                }
            }
        }
    }

    void Start()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.targetFrameRate = 60;
        CleanStraySceneObjects();

        // AR marker mode is chosen in the main menu (PlayerPrefs). Default = classic mode.
        arMarkerMode = PlayerPrefs.GetInt(UseMarkerPrefKey, 0) == 1;

        comps = new[]
        {
            new Competency { code = "C1", title = "Acoustic Strata Sounding" },
            new Competency { code = "C2", title = "Cave-In Retreat Reaction" },
            new Competency { code = "C3", title = "Self-Rescuer Deployment" },
            new Competency { code = "C4", title = "Escapeway Wayfinding" }
        };

        SetupCamera();
        EnsureEventSystem();
        GenerateTextures();
        GenerateSprites();
        SetupAudio();
        BuildMine();
        BuildHUD();
        BuildDecisionCard();
        BuildDebrief();

        SetPhase(Phase.ShiftWalkIn);
        SetObjective("Walk the intake drift. Stop at the barrier tape and SOUND THE ROOF before advancing.");
        StartCoroutine(FadeFrom(Color.black, 1.6f));
        ShowToast("SHIFT START  -  INTAKE DRIFT 3", 3f);

        if (arMarkerMode)
        {
            BuildARScanOverlay();
            StartCoroutine(ARAnchorRoutine());
        }
    }

    void OnDestroy()
    {
        if (gyro != null && gyro.added && gyro.enabled) InputSystem.DisableDevice(gyro);
        UnsubscribeAR();
    }

    void Update()
    {
        float dt = Time.deltaTime;
        // While the trainee is still scanning for the marker the drill timer is paused.
        bool scanning = arMarkerMode && !arAnchored;
        if (phase != Phase.Debrief && !scanning) elapsed += dt;

        if (arRigBuilt) UpdateARTracking();

        HandleBackButton();
        UpdateCanvasLayout();

        bool modal = decisionOpen || debriefOpen || cinematicLock;
        if (!modal) HandleInput(dt);
        else ReleaseInput();

        UpdateFocus(modal);
        UpdateTriggers();
        UpdateGas(dt, modal);
        UpdateEffects(dt);
        UpdateHUD(dt);
        ApplyCamera(dt);
    }

    // =====================================================================================
    //  CAMERA, ATMOSPHERE, EVENT SYSTEM
    // =====================================================================================
    void SetupCamera()
    {
        cam = Camera.main;
        if (cam == null)
        {
            GameObject camObj = new GameObject("Main Camera");
            camObj.tag = "MainCamera";
            cam = camObj.AddComponent<Camera>();
        }
        if (FindFirstObjectByType<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();

        basePos = new Vector3(0f, EyeHeight, -8f);
        cam.transform.SetPositionAndRotation(basePos, Quaternion.identity);
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 120f;
        cam.fieldOfView = 68f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.012f, 0.01f, 0.008f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.035f, 0.03f, 0.025f);
        RenderSettings.fogDensity = 0.05f;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.13f, 0.12f, 0.105f);

        // Miner's cap lamp: dim working beam in Phase 1, 15 m emergency beam after the self-rescuer.
        GameObject lampObj = new GameObject("CapLamp");
        lampObj.transform.SetParent(cam.transform, false);
        lampObj.transform.localPosition = new Vector3(0f, 0.12f, 0.05f);
        capLamp = lampObj.AddComponent<Light>();
        capLamp.type = LightType.Spot;
        capLamp.range = 8f;
        capLamp.spotAngle = 52f;
        capLamp.innerSpotAngle = 22f;
        capLamp.intensity = 0.9f;
        capLamp.color = new Color(1f, 0.96f, 0.88f);
        capLamp.shadows = LightShadows.None;

        gyro = UnityEngine.InputSystem.Gyroscope.current;
        if (useGyro && gyro != null) InputSystem.EnableDevice(gyro);
    }

    void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        GameObject es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
    }

    // =====================================================================================
    //  AR MARKER MODE  (AR Foundation 6 + ARCore image tracking, everything built at runtime)
    // =====================================================================================
    // Full-screen scanning overlay: dim scrim + instruction card + SKIP button.
    // Uses the same procedural uGUI toolkit as the rest of the drill (zero assets).
    void BuildARScanOverlay()
    {
        if (canvas == null) return; // BuildHUD must have run first
        RectTransform rt = UIRect("ARScanOverlay", canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(rt);
        arScanOverlay = rt.gameObject;

        Image scrim = UIImage(rt, null, new Color(0f, 0f, 0f, 0.72f), true);
        Button scrimBtn = rt.gameObject.AddComponent<Button>();
        scrimBtn.transition = Selectable.Transition.None;
        scrimBtn.targetGraphic = scrim;

        RectTransform card = UIPanel("Card", rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(900f, 560f), UIPanelSolid, true);
        Accent(card, UIAmber, 8f);

        UIText("Title", card, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(840f, 54f),
               "AR MARKER ANCHORING", 36f, UIAmber, TextAlignmentOptions.Center, FontStyles.Bold);

        UIText("Body", card, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -92f), new Vector2(840f, 280f),
               "Point the camera at the floor marker (MineAnchorMarker) until it is detected.\n\n" +
               "Print the marker at " + arMarkerWidthMeters.ToString("0.00") + " m wide and lay it flat on the floor in an open area.\n\n" +
               "Once detected, the mine tunnel is anchored to the marker - walk around to explore it in your room.",
               25f, UITextCol, TextAlignmentOptions.TopLeft);

        arScanStatus = UIText("Status", card, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 96f), new Vector2(840f, 40f),
                              "Starting AR camera...", 23f, UIMuted, TextAlignmentOptions.Center, FontStyles.Bold);

        Button skip = UIButton("SkipButton", card, new Vector2(0.5f, 0f), new Vector2(0f, 30f), new Vector2(560f, 78f),
                               "START WITHOUT MARKER", new Color(0.40f, 0.14f, 0.14f, 1f), 26f, out _);
        skip.onClick.AddListener(SkipARMarker);
    }

    void SetARScanStatus(string msg)
    {
        if (arScanStatus != null) arScanStatus.text = msg;
    }

    // Trainee gave up on the marker (or tapped SKIP): restore everything and run classic mode.
    void SkipARMarker()
    {
        if (arAnchored || arScanSkipped) return;
        arScanSkipped = true;
        FallbackToNoMarker("Marker skipped by the trainee.");
    }

    // Waits for AR support + the first locked marker detection, then anchors MineEnvironment.
    // ARSession.CheckAvailability()/Install() are static coroutines; ARSession.state is static too,
    // so this works before (and without) any ARSession instance existing.
    IEnumerator ARAnchorRoutine()
    {
        SetARScanStatus("Checking AR support...");
        yield return null; // let the UI render one frame before the (possibly slow) check
        yield return ARSession.CheckAvailability();

        if (ARSession.state == ARSessionState.Unsupported)
        {
            FallbackToNoMarker("AR is not supported on this device/editor.");
            yield break;
        }

        if (ARSession.state == ARSessionState.NeedsInstall)
        {
            SetARScanStatus("Installing AR support...");
            yield return ARSession.Install();
        }

        if (ARSession.state == ARSessionState.Unsupported || ARSession.state == ARSessionState.NeedsInstall)
        {
            FallbackToNoMarker("AR support unavailable on this device.");
            yield break;
        }

        if (!TryBuildARRig())
        {
            // TryBuildARRig already called FallbackToNoMarker with a reason.
            yield break;
        }

        // Give the tracked-image manager a few seconds to start producing images.
        SetARScanStatus("Looking for the marker...");
        float scan = 0f;
        while (!arAnchored && !arScanSkipped && scan < 60f)
        {
            scan += Time.deltaTime;
            if (scan > 6f) SetARScanStatus("Still looking - hold the marker flat and steady in view.");
            else if (scan > 3f) SetARScanStatus("Scanning... point steadily at the marker.");
            yield return null;
        }

        if (!arAnchored && !arScanSkipped)
            FallbackToNoMarker("Marker not found within 60 s.");
    }

    // Creates ARSession + ARInputManager + XROrigin + TrackedPoseDriver + ARCameraManager/
    // ARCameraBackground + ARTrackedImageManager entirely from code (the VRMode scene has none).
    bool TryBuildARRig()
    {
        if (arRigBuilt) return true;

        GameObject imgGO = null;
        try
        {
            // --- ARSession + ARInputManager -------------------------------------------------
            arSessionGO = new GameObject("ARSession");
            arSession = arSessionGO.AddComponent<ARSession>();
            arSessionGO.AddComponent<ARInputManager>();

            // --- XROrigin + camera ----------------------------------------------------------
            // Build the whole rig on an INACTIVE GameObject so XROrigin.Awake() only runs after
            // we've wired the camera + floor offset (otherwise Awake grabs Camera.main and warns).
            arRigRoot = new GameObject("ARCameraRig");
            arRigRoot.SetActive(false);

            GameObject camOffset = new GameObject("ARCameraOffset");
            camOffset.transform.SetParent(arRigRoot.transform, false);

            arOrigin = arRigRoot.AddComponent<XROrigin>();
            arOrigin.Camera = cam;                      // the scene's Main Camera
            arOrigin.Origin = arRigRoot;                // GameObject, not Transform
            arOrigin.CameraFloorOffsetObject = camOffset;

            // Keep the existing Main Camera (URP + AudioListener + cap lamp live on it);
            // move it under the offset and reset its local pose (TrackedPoseDriver will drive it).
            arCamera = cam;
            arCamera.transform.SetParent(camOffset.transform, false);
            arCamera.transform.localPosition = Vector3.zero;
            arCamera.transform.localRotation = Quaternion.identity;
            arCamera.transform.localScale = Vector3.one;

            // Input System TrackedPoseDriver drives the camera from <XRHMD>/centerEye...
            TrackedPoseDriver poseDriver = arCamera.gameObject.GetComponent<TrackedPoseDriver>();
            if (poseDriver == null) poseDriver = arCamera.gameObject.AddComponent<TrackedPoseDriver>();
            poseDriver.positionInput = new InputActionProperty(new InputAction(binding: "<HandheldARInputDevice>/devicePosition"));
            poseDriver.rotationInput = new InputActionProperty(new InputAction(binding: "<HandheldARInputDevice>/deviceRotation"));

            // AR camera feed (needs the URP ARBackgroundRendererFeature to actually draw).
            if (arCamera.gameObject.GetComponent<ARCameraManager>() == null)
                arCamera.gameObject.AddComponent<ARCameraManager>();
            if (arCamera.gameObject.GetComponent<ARCameraBackground>() == null)
                arCamera.gameObject.AddComponent<ARCameraBackground>();

            arCamera.clearFlags = CameraClearFlags.SolidColor;
            arCamera.backgroundColor = Color.black;
            arCamera.nearClipPlane = 0.03f; // closer clip so nearby tunnel geometry isn't cut

            // --- ARTrackedImageManager (also built inactive, configured, then activated) -----
            imgGO = new GameObject("ARTrackedImageManager");
            imgGO.transform.SetParent(arRigRoot.transform, false);
            imgGO.SetActive(false);
            arImageManager = imgGO.AddComponent<ARTrackedImageManager>();
            RuntimeReferenceImageLibrary markerLib = SetupMarkerLibrary();
            if (markerLib == null)
            {
                FallbackToNoMarker("No marker library or texture configured.");
                return false;
            }
            arImageManager.referenceLibrary = markerLib;
            arImageManager.requestedMaxNumberOfMovingImages = 1;
            arImageManager.trackedImagePrefab = null; // we react via trackablesChanged, not a prefab

            // Everything is wired: activate the rig (XROrigin.Awake now sees a correct camera).
            arRigRoot.SetActive(true);
            imgGO.SetActive(true);

            SubscribeAR();

            arRigBuilt = true;
            SetARScanStatus("Move the phone slowly over the marker.");
            Debug.Log("[MineSimulationManager] AR rig built. Waiting for marker '" + arMarkerImageName + "'.");
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[MineSimulationManager] TryBuildARRig failed: " + e.Message);
            FallbackToNoMarker("AR rig build failed: " + e.Message);
            return false;
        }
    }

    // Returns the reference library to use: Inspector-assigned, Resources lookup, or a
    // runtime library built from arMarkerTexture via ScheduleAddImageWithValidationJob.
    RuntimeReferenceImageLibrary SetupMarkerLibrary()
    {
        try
        {
            if (arMarkerLibrary != null)
                return arImageManager.CreateRuntimeLibrary(arMarkerLibrary);

            XRReferenceImageLibrary fromResources = Resources.Load<XRReferenceImageLibrary>("MineAnchorMarkerLibrary");
            if (fromResources != null)
                return arImageManager.CreateRuntimeLibrary(fromResources);

            if (arMarkerTexture != null)
            {
                if (ARSession.state < ARSessionState.Ready)
                {
                    Debug.LogWarning("[MineSimulationManager] ARSession not Ready yet; cannot add marker texture at runtime.");
                    return null;
                }
                RuntimeReferenceImageLibrary runtimeLib = arImageManager.CreateRuntimeLibrary();
                // ScheduleAddImageWithValidationJob is an extension on MutableRuntimeReferenceImageLibrary
                // (ARCore's ARCoreImageDatabase is mutable); guard the cast for other providers.
                if (runtimeLib is MutableRuntimeReferenceImageLibrary mutableLib)
                {
                    mutableLib.ScheduleAddImageWithValidationJob(arMarkerTexture, arMarkerImageName, arMarkerWidthMeters);
                    return runtimeLib;
                }
                Debug.LogWarning("[MineSimulationManager] Runtime image library is not mutable on this provider.");
                return null;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[MineSimulationManager] SetupMarkerLibrary failed: " + e.Message);
        }
        return null;
    }

    void SubscribeAR()
    {
        if (arImageManager == null) return;
        arImageManager.trackablesChanged.AddListener(OnARTrackablesChanged);
    }

    void UnsubscribeAR()
    {
        if (arImageManager == null) return;
        arImageManager.trackablesChanged.RemoveListener(OnARTrackablesChanged);
    }

    void OnARTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
    {
        for (int i = 0; i < args.added.Count; i++) TryAnchorMine(args.added[i]);
        for (int i = 0; i < args.updated.Count; i++) TryAnchorMine(args.updated[i]);
    }

    bool IsMarkerImage(ARTrackedImage img)
    {
        if (img == null) return false;
        string n = img.referenceImage.name;
        if (string.IsNullOrEmpty(n)) return false;
        return string.Equals(n, arMarkerImageName, System.StringComparison.OrdinalIgnoreCase) ||
               n.IndexOf(arMarkerImageName, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void TryAnchorMine(ARTrackedImage img)
    {
        if (arAnchored || img == null) return;
        if (!IsMarkerImage(img)) return;
        if (img.trackingState == TrackingState.None) return; // not localized yet
        AnchorMineToMarker(img);
    }

    // Parents MineEnvironment to a yaw-only upright anchor at the marker so the tunnel lies
    // flat on the floor. The mine's floor at the entrance (ArStartLocal with y = 0) is placed
    // exactly at the marker, so the trainee's eye height automatically maps to the miner's
    // eye height and they physically walk into the tunnel. Yaw is taken from the marker image
    // (projected to the horizontal plane) so the printed marker decides which way the drift runs.
    void AnchorMineToMarker(ARTrackedImage img)
    {
        if (arAnchored || envRoot == null || img == null) return;

        // Snapshot fog/ambient once so we can restore on fallback.
        if (!arEnvSaved)
        {
            arSavedFog = RenderSettings.fog;
            arSavedFogColor = RenderSettings.fogColor;
            arSavedFogDensity = RenderSettings.fogDensity;
            arSavedAmbientMode = RenderSettings.ambientMode;
            arSavedAmbient = RenderSettings.ambientLight;
            arEnvSaved = true;
        }

        // Upright anchor at the marker, yaw aligned to the marker's tracked forward
        // (projected to the horizontal plane so the tunnel doesn't tilt with the phone).
        Vector3 markerPos = img.transform.position;
        Vector3 markerFwd = img.transform.forward;
        markerFwd.y = 0f;
        if (markerFwd.sqrMagnitude < 1e-4f && arCamera != null)
        {
            // Flat marker can report a degenerate forward; fall back to the camera heading.
            markerFwd = arCamera.transform.forward;
            markerFwd.y = 0f;
        }
        if (markerFwd.sqrMagnitude < 1e-4f) markerFwd = Vector3.forward;
        markerFwd.Normalize();
        Quaternion yawRot = Quaternion.LookRotation(markerFwd, Vector3.up);

        GameObject anchorGO = new GameObject("ARMarkerAnchor");
        arAnchor = anchorGO.transform;
        arAnchor.SetPositionAndRotation(markerPos, yawRot);
        arAnchorHomePos = markerPos;
        arAnchorHomeRot = yawRot;
        arWorldDrift = Vector3.zero;

        // Anchor the mine's floor entrance (ArStartLocal flattened to y = 0) at the marker so
        // the tunnel floor sits on the real floor and the trainee's eyes land at eye height.
        Vector3 floorStart = new Vector3(ArStartLocal.x, 0f, ArStartLocal.z);
        envRoot.SetParent(arAnchor, false);
        envRoot.localPosition = -(floorStart * arMineScale);
        envRoot.localRotation = Quaternion.identity;
        envRoot.localScale = Vector3.one * arMineScale;

        arAnchored = true;

        // Freeze the anchor against marker jitter once locked.
        if (arImageManager != null) arImageManager.enabled = false;

        // Re-assert the mine atmosphere (AR keeps rendering; fog sells the depth).
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.035f, 0.03f, 0.025f);
        RenderSettings.fogDensity = 0.05f;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.13f, 0.12f, 0.105f);

        if (arScanOverlay != null) arScanOverlay.SetActive(false);

        // Seed basePos from the live camera pose so triggers/focus start in the right spot.
        // (The camera itself is NOT teleported - the TrackedPoseDriver owns its pose; the
        // trainee simply walks from wherever they are into the anchored tunnel.)
        if (arCamera != null) basePos = WorldToMine(arCamera.transform.position);
        yaw = markerFwd.sqrMagnitude > 0f ? Mathf.Atan2(markerFwd.x, markerFwd.z) * Mathf.Rad2Deg : yaw;

        Debug.Log("[MineSimulationManager] Mine anchored to marker at " + markerPos + " (scale " + arMineScale + ").");
    }

    // Per-frame: keep basePos in sync with the AR camera pose (in mine-space) while anchored.
    void UpdateARTracking()
    {
        if (!arAnchored || arCamera == null || envRoot == null) return;

        // While the cage/evacuation cinematics drive basePos, don't fight them.
        if (cageEntered || phase == Phase.Evacuation || phase == Phase.Debrief) return;

        // Joystick fallback slides the anchored world instead of moving the player.
        if (arWorldDrift.sqrMagnitude > 1e-8f && arAnchor != null)
        {
            arAnchor.position = arAnchorHomePos + arWorldDrift;
            arWorldDrift = Vector3.zero;
        }

        // basePos = mine-space position of the camera.
        basePos = WorldToMine(arCamera.transform.position);

        // Yaw/pitch follow the physical camera heading so look-based logic stays correct.
        Vector3 fwd = arCamera.transform.forward;
        if (fwd.sqrMagnitude > 1e-6f)
        {
            yaw = Mathf.LerpAngle(yaw, Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg, 0.35f);
            pitch = Mathf.Lerp(pitch, Mathf.Clamp(Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg, -45f, 45f), 0.35f);
        }
    }

    // ---- coordinate helpers (mine-space <-> world) ---------------------------------------
    Vector3 MineToWorld(Vector3 minePos)
    {
        if (!arAnchored || envRoot == null) return minePos;
        return envRoot.TransformPoint(minePos);
    }

    Quaternion MineToWorldRot(Quaternion mineRot)
    {
        if (!arAnchored || envRoot == null) return mineRot;
        return envRoot.rotation * mineRot;
    }

    Vector3 WorldToMine(Vector3 worldPos)
    {
        if (!arAnchored || envRoot == null) return worldPos;
        return envRoot.InverseTransformPoint(worldPos);
    }

    // Move a transform that was positioned in mine-space (as a child of envRoot) into world
    // space at the equivalent mine-space point. Safe no-op in classic mode.
    void Place(Transform t, Vector3 minePos)
    {
        if (t == null) return;
        t.position = MineToWorld(minePos);
    }

    // Move a logical "base position" (the player's mine-space position) - used by cinematics
    // that write basePos directly. In classic mode this is a plain assignment.
    void SetARLogicalPosition(Vector3 minePos)
    {
        basePos = minePos;
    }

    // Torn down on fallback: destroy the AR rig, restore environment, reset envRoot.
    void FallbackToNoMarker(string reason)
    {
        Debug.LogWarning("[MineSimulationManager] Falling back to classic mode: " + reason);
        TeardownARRig();

        if (arEnvSaved)
        {
            RenderSettings.fog = arSavedFog;
            RenderSettings.fogColor = arSavedFogColor;
            RenderSettings.fogDensity = arSavedFogDensity;
            RenderSettings.ambientMode = arSavedAmbientMode;
            RenderSettings.ambientLight = arSavedAmbient;
            arEnvSaved = false;
        }

        // Reset mine-space placement.
        arAnchored = false;
        if (envRoot != null)
        {
            envRoot.SetParent(null, false);
            envRoot.localPosition = Vector3.zero;
            envRoot.localRotation = Quaternion.identity;
            envRoot.localScale = Vector3.one;
        }

        // Restore camera to classic mode: solid dark clear, no AR feed.
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.012f, 0.01f, 0.008f);
            cam.nearClipPlane = 0.05f;
        }

        if (arScanOverlay != null) arScanOverlay.SetActive(false);
        arRigBuilt = false;
        arMarkerMode = false; // fully back to classic mode

        ShowToast("AR marker unavailable - running classic drill. " + reason, 4f, UIAmber);
    }

    void TeardownARRig()
    {
        UnsubscribeAR();
        arImageManager = null;
        arOrigin = null;
        arCamera = null;

        // Reparent the camera back to wherever it was (usually scene root) and restore identity.
        if (cam != null)
        {
            cam.transform.SetParent(null, false);
            cam.transform.localPosition = new Vector3(0f, EyeHeight, -8f);
            cam.transform.localRotation = Quaternion.identity;
            cam.transform.localScale = Vector3.one;
            // Remove AR-only components.
            ARCameraManager mgr = cam.GetComponent<ARCameraManager>();
            if (mgr != null) Destroy(mgr);
            ARCameraBackground bg = cam.GetComponent<ARCameraBackground>();
            if (bg != null) Destroy(bg);
            TrackedPoseDriver tpd = cam.GetComponent<TrackedPoseDriver>();
            if (tpd != null) Destroy(tpd);
        }

        if (arAnchor != null) Destroy(arAnchor.gameObject);
        arAnchor = null;

        if (arRigRoot != null) Destroy(arRigRoot);
        arRigRoot = null;
        if (arSessionGO != null) Destroy(arSessionGO);
        arSessionGO = null;
        arSession = null;
    }

    // =====================================================================================
    //  PROCEDURAL TEXTURES (tileable value-noise fBm, no texture assets)
    // =====================================================================================
    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + seed * 1442695041;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0x7fffffff) / (float)int.MaxValue;
        }
    }

    static float ValueNoise(float x, float y, int period, int seed)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        int xa = ((x0 % period) + period) % period, xb = (xa + 1) % period;
        int ya = ((y0 % period) + period) % period, yb = (ya + 1) % period;
        float a = Mathf.Lerp(Hash(xa, ya, seed), Hash(xb, ya, seed), fx);
        float b = Mathf.Lerp(Hash(xa, yb, seed), Hash(xb, yb, seed), fx);
        return Mathf.Lerp(a, b, fy);
    }

    static float Fbm(float u, float v, int basePeriod, int octaves, int seed)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        int per = basePeriod;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * ValueNoise(u * per, v * per, per, seed + i * 31);
            norm += amp;
            amp *= 0.5f;
            per *= 2;
        }
        return sum / norm;
    }

    Texture2D MakeTexture(string name, int size, System.Func<float, float, int, int, Color> pixel)
    {
        Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, true);
        t.name = name;
        t.wrapMode = TextureWrapMode.Repeat;
        t.filterMode = FilterMode.Bilinear;
        t.anisoLevel = 2;
        Color[] px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = pixel((float)x / size, (float)y / size, x, y);
        t.SetPixels(px);
        t.Apply(true, true);
        return t;
    }

    void GenerateTextures()
    {
        // Coal rib: blue-black matte with strata banding and bright anthracite glints.
        texCoal = MakeTexture("Coal", 128, (u, v, x, y) =>
        {
            float n = Fbm(u, v, 4, 5, 11);
            float band = Mathf.Sin((v * 7f + n * 0.9f) * Mathf.PI * 2f) * 0.5f + 0.5f;
            float b = 0.045f + n * 0.07f + band * 0.025f;
            if (Hash(x, y, 77) > 0.988f) b += 0.22f;
            return new Color(b * 0.95f, b * 0.97f, b * 1.06f, 1f);
        });

        // Roof rock / shale: grey-brown with fine fractures.
        texRock = MakeTexture("Rock", 128, (u, v, x, y) =>
        {
            float n = Fbm(u, v, 4, 5, 23);
            float c = Fbm(u, v, 8, 3, 5);
            float b = 0.15f + n * 0.17f;
            if (Mathf.Abs(c - 0.5f) < 0.018f) b *= 0.45f;
            return new Color(b * 1.05f, b, b * 0.92f, 1f);
        });

        // Floor: wet gravel, coal dust and mud.
        texGravel = MakeTexture("Gravel", 128, (u, v, x, y) =>
        {
            float n = Fbm(u, v, 16, 3, 41);
            float m = Fbm(u, v, 3, 3, 9);
            float b = 0.06f + n * 0.12f + m * 0.04f;
            return new Color(b * 1.08f, b, b * 0.88f, 1f);
        });

        // Timber: rough-sawn grain running along V.
        texWood = MakeTexture("Timber", 128, (u, v, x, y) =>
        {
            float n = Fbm(u, v, 4, 4, 63);
            float grain = Mathf.Sin((u * 26f + n * 5f) * Mathf.PI) * 0.5f + 0.5f;
            float k = 0.68f + grain * 0.28f + (Fbm(u, v, 16, 2, 3) - 0.5f) * 0.1f;
            return new Color(0.42f * k, 0.27f * k, 0.13f * k, 1f);
        });

        // Oxidised steel: dark mill-scale grey with rust blooms.
        texRust = MakeTexture("Rust", 128, (u, v, x, y) =>
        {
            float n = Fbm(u, v, 4, 5, 87);
            float r = Mathf.SmoothStep(0.42f, 0.66f, n);
            Color steel = new Color(0.26f, 0.27f, 0.28f) * (0.85f + Fbm(u, v, 16, 2, 4) * 0.3f);
            Color rust = new Color(0.43f, 0.2f, 0.08f) * (0.8f + Fbm(u, v, 8, 3, 6) * 0.4f);
            Color c = Color.Lerp(steel, rust, r);
            c.a = 1f;
            return c;
        });

        // Shaft lining concrete.
        texConcrete = MakeTexture("Concrete", 128, (u, v, x, y) =>
        {
            float n = Fbm(u, v, 8, 4, 101);
            float b = 0.3f + n * 0.14f;
            if (Hash(x, y, 9) > 0.99f) b -= 0.1f;
            return new Color(b, b * 0.98f, b * 0.95f, 1f);
        });
    }

    // =====================================================================================
    //  PROCEDURAL UI SPRITES (anti-aliased SDF shapes)
    // =====================================================================================
    Sprite MakeSprite(int size, System.Func<float, float, float> alpha, int border = 0, bool repeat = false)
    {
        Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false);
        t.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Bilinear;
        Color[] px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha(x + 0.5f, y + 0.5f)));
        t.SetPixels(px);
        t.Apply(false, true);
        return Sprite.Create(t, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }

    static float RoundRectSdf(float x, float y, float size, float radius)
    {
        float h = size * 0.5f;
        float qx = Mathf.Abs(x - h) - (h - radius);
        float qy = Mathf.Abs(y - h) - (h - radius);
        float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
        return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
    }

    void GenerateSprites()
    {
        const int S = 64;
        const float R = 16f;
        sprRound = MakeSprite(S, (x, y) => 0.5f - RoundRectSdf(x, y, S, R), 20);
        sprRoundLine = MakeSprite(S, (x, y) =>
        {
            float d = RoundRectSdf(x, y, S, R);
            return Mathf.Min(0.5f - d, 0.5f + d + 2.5f);
        }, 20);
        sprCircle = MakeSprite(128, (x, y) => 64f - new Vector2(x - 64f, y - 64f).magnitude);
        sprRing = MakeSprite(128, (x, y) =>
        {
            float d = new Vector2(x - 64f, y - 64f).magnitude;
            return Mathf.Min(63f - d, d - 55f);
        });
        sprSoft = MakeSprite(64, (x, y) =>
        {
            float r = new Vector2(x - 32f, y - 32f).magnitude / 32f;
            return Mathf.Pow(Mathf.Clamp01(1f - r), 2f);
        });
        sprVignette = MakeSprite(256, (x, y) =>
        {
            float r = new Vector2((x - 128f) / 128f, (y - 128f) / 128f).magnitude;
            return Mathf.Pow(Mathf.SmoothStep(0.35f, 1.25f, r), 1.2f);
        });
        sprStripes = MakeSprite(32, (x, y) => ((Mathf.FloorToInt((x + y) / 16f) & 1) == 0) ? 1f : 0f, 0, true);
    }

    // =====================================================================================
    //  MATERIALS & PRIMITIVE HELPERS
    // =====================================================================================
    static Shader FindShader(params string[] names)
    {
        foreach (string n in names)
        {
            Shader s = Shader.Find(n);
            if (s != null) return s;
        }
        return null;
    }

    Material Lit(Color color, Texture2D tex = null, float tiling = 1f, float smooth = 0.15f, float metal = 0f)
    {
        Shader s = FindShader("Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard", "Sprites/Default");
        Material m = new Material(s);
        m.color = color;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (tex != null)
        {
            m.mainTexture = tex;
            m.mainTextureScale = Vector2.one * tiling;
            if (m.HasProperty("_BaseMap"))
            {
                m.SetTexture("_BaseMap", tex);
                m.SetTextureScale("_BaseMap", Vector2.one * tiling);
            }
        }
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        return m;
    }

    // Unlit = self-illuminated look (LED lenses, reflective signs) without relying on
    // emission keywords that can be stripped from mobile builds.
    Material Glow(Color color)
    {
        Shader s = FindShader("Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default");
        Material m = new Material(s);
        m.color = color;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        return m;
    }

    static void SetMatColor(Material m, Color c)
    {
        if (m == null) return;
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
    }

    GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat,
                    Quaternion? rot = null, bool keepCollider = false)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.rotation = rot ?? Quaternion.identity;
        go.transform.localScale = scale;
        Renderer r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        if (!keepCollider) Destroy(go.GetComponent<Collider>());
        return go;
    }

    GameObject Box(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, Quaternion? rot = null, bool keepCollider = false)
        => Prim(PrimitiveType.Cube, name, parent, pos, scale, mat, rot, keepCollider);

    // Cylinder laid between two points (cables, pipes, rope, rails).
    GameObject Rod(string name, Transform parent, Vector3 a, Vector3 b, float diameter, Material mat)
    {
        Vector3 d = b - a;
        return Prim(PrimitiveType.Cylinder, name, parent, (a + b) * 0.5f,
                    new Vector3(diameter, d.magnitude * 0.5f, diameter), mat,
                    Quaternion.FromToRotation(Vector3.up, d.normalized));
    }

    Light PointLight(string name, Transform parent, Vector3 pos, Color color, float range, float intensity)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        Light l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.range = range;
        l.intensity = intensity;
        l.color = color;
        l.shadows = LightShadows.None;
        return l;
    }

    // World-space signboard: backing board + TextMeshPro face.
    GameObject Sign(string name, Transform parent, Vector3 pos, Quaternion rot, Vector2 size, Color board, Color textColor,
                    string text, float fontSize, bool glow = false, Color? border = null)
    {
        GameObject root = new GameObject(name);
        root.transform.SetParent(parent, false);
        root.transform.SetPositionAndRotation(pos, rot);

        Material boardMat = glow ? Glow(board) : Lit(board, null, 1f, 0.35f);
        GameObject b = Box(name + "_Board", root.transform, pos, new Vector3(size.x, size.y, 0.03f), boardMat, rot);
        if (border.HasValue)
        {
            Material bm = Glow(border.Value);
            float t = 0.035f;
            Vector3 f = rot * Vector3.back * 0.018f;
            Box("Edge_T", root.transform, pos + rot * new Vector3(0f, size.y * 0.5f - t * 0.5f, 0f) + f, new Vector3(size.x, t, 0.005f), bm, rot);
            Box("Edge_B", root.transform, pos + rot * new Vector3(0f, -size.y * 0.5f + t * 0.5f, 0f) + f, new Vector3(size.x, t, 0.005f), bm, rot);
            Box("Edge_L", root.transform, pos + rot * new Vector3(-size.x * 0.5f + t * 0.5f, 0f, 0f) + f, new Vector3(t, size.y, 0.005f), bm, rot);
            Box("Edge_R", root.transform, pos + rot * new Vector3(size.x * 0.5f - t * 0.5f, 0f, 0f) + f, new Vector3(t, size.y, 0.005f), bm, rot);
        }

        GameObject label = new GameObject(name + "_Text", typeof(RectTransform));
        label.transform.SetParent(root.transform, false);
        label.transform.localPosition = new Vector3(0f, 0f, -0.025f);
        TextMeshPro tmp = label.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = textColor;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        ((RectTransform)label.transform).sizeDelta = size * 0.92f;
        return root;
    }

    // =====================================================================================
    //  PROCEDURAL AUDIO SYNTHESIS (PCM written with AudioClip.Create)
    // =====================================================================================
    const int SR = 44100;

    AudioClip BuildClip(string name, float seconds, System.Func<int, float, float> sample, bool loopable = false)
    {
        int n = Mathf.CeilToInt(SR * seconds);
        float[] d = new float[n];
        for (int i = 0; i < n; i++) d[i] = sample(i, (float)i / SR);
        if (loopable)
        {
            // Crossfade the tail into the head so noise-based loops are seamless.
            int fade = Mathf.Min(n / 4, SR / 5);
            for (int i = 0; i < fade; i++)
            {
                float k = (float)i / fade;
                d[i] = d[i] * k + d[n - fade + i] * (1f - k);
            }
            System.Array.Resize(ref d, n - fade);
            n -= fade;
        }
        for (int i = 0; i < n; i++) d[i] = Mathf.Clamp(d[i], -1f, 1f);
        AudioClip c = AudioClip.Create(name, n, 1, SR, false);
        c.SetData(d, 0);
        return c;
    }

    // Stateful filtered-noise generator used by the synthesis lambdas.
    class Noise
    {
        private uint s;
        private float lp1, lp2;
        public Noise(uint seed) { s = seed == 0 ? 1u : seed; }
        public float White() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s / (float)uint.MaxValue) * 2f - 1f; }
        public float Low(float a) { lp1 += a * (White() - lp1); return lp1; }
        public float Band(float aLow, float aHigh) { float w = White(); lp1 += aHigh * (w - lp1); lp2 += aLow * (w - lp2); return lp1 - lp2; }
    }

    static float Sine(float hz, float t) => Mathf.Sin(2f * Mathf.PI * hz * t);

    void SetupAudio()
    {
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        ambSrc = MakeLoopSource(0.5f);
        alarmSrc = MakeLoopSource(0f);
        breathSrc = MakeLoopSource(0f);
        winchSrc = MakeLoopSource(0f);

        // 55 Hz ventilation fan rumble with motor harmonics and moving air.
        Noise nv = new Noise(7);
        clipVent = BuildClip("VentRumble", 4f, (i, t) =>
            Sine(55f, t) * 0.22f + Sine(110f, t) * 0.08f + Sine(165f, t) * 0.03f +
            nv.Low(0.02f) * 1.6f * (0.8f + 0.2f * Sine(0.5f, t)), true);

        // Timber creak: stick-slip pulses through a groaning resonance.
        Noise nc = new Noise(19);
        clipCreak = BuildClip("TimberCreak", 1.5f, (i, t) =>
        {
            float env = Mathf.Sin(Mathf.PI * t / 1.5f);
            float f = 140f + 70f * Mathf.Sin(t * 3.1f) + 25f * nc.Low(0.001f);
            float pulse = Mathf.Pow(Mathf.Abs(Mathf.Sin(Mathf.PI * t * (38f + 14f * Mathf.Sin(t * 5f)))), 9f);
            return (Sine(f, t) * 0.6f + Sine(f * 2.7f, t) * 0.25f + nc.White() * 0.15f) * pulse * env * 0.8f;
        });

        // Timber snap: hard transient + splintering crackle.
        Noise ns = new Noise(23);
        clipSnap = BuildClip("TimberSnap", 0.7f, (i, t) =>
            ns.White() * Mathf.Exp(-t * 28f) * 1.1f + Sine(85f, t) * Mathf.Exp(-t * 9f) * 0.7f +
            ns.Band(0.05f, 0.5f) * Mathf.Exp(-t * 5f) * (Hash(i / 300, 0, 3) > 0.6f ? 0.8f : 0.1f));

        // Seismic cave-in: crack, deep concussive boom, then a long rock tumble roar.
        Noise nk = new Noise(31);
        clipCaveIn = BuildClip("CaveIn", 4.5f, (i, t) =>
        {
            float crack = nk.White() * Mathf.Exp(-t * 18f) * 1.2f;
            float boom = (Sine(38f, t) * 0.9f + Sine(52f, t) * 0.5f) * Mathf.Exp(-t * 1.6f);
            float roar = nk.Low(0.04f) * 5f * Mathf.SmoothStep(0f, 1f, t * 3f) * Mathf.Exp(-t * 0.6f);
            float impacts = (Hash(i / 1500, 1, 8) > 0.82f ? 1f : 0f) * nk.Low(0.2f) * 2f * Mathf.Exp(-t * 0.5f);
            return crack + boom + roar + impacts;
        });

        // Methane / CO alarm: screeching two-tone warble (loop).
        clipAlarm = BuildClip("GasAlarm", 1f, (i, t) =>
        {
            float f = (t % 0.5f) < 0.25f ? 2100f : 1550f;
            float sq = Mathf.Sign(Sine(f, t)) * 0.35f + Sine(f * 2f, t) * 0.1f;
            return sq * 0.55f;
        });

        // Panicked hypoxic breathing: two noisy breaths per second-ish (loop, pitch-shifted live).
        Noise nb = new Noise(43);
        clipBreathHeavy = BuildClip("BreathHeavy", 1.6f, (i, t) =>
        {
            float ph = (t % 0.8f) / 0.8f;
            float env = ph < 0.42f ? Mathf.Sin(Mathf.PI * ph / 0.42f) : Mathf.Sin(Mathf.PI * (ph - 0.42f) / 0.58f) * 0.8f;
            float tone = ph < 0.42f ? nb.Band(0.03f, 0.25f) : nb.Band(0.02f, 0.12f);
            return tone * env * env * 2.2f;
        });

        // Self-rescuer respirator: slow breath through a canister, valve clicks and hiss (loop).
        Noise nr = new Noise(47);
        clipRespirator = BuildClip("Respirator", 3.6f, (i, t) =>
        {
            float ph = t / 3.6f;
            float env = ph < 0.45f ? Mathf.Sin(Mathf.PI * ph / 0.45f) : Mathf.Sin(Mathf.PI * (ph - 0.45f) / 0.55f) * 0.7f;
            float click = (Mathf.Abs(ph - 0.45f) < 0.004f || ph < 0.004f) ? nr.White() * 0.5f : 0f;
            return nr.Band(0.01f, 0.08f) * env * 1.8f + click + nr.White() * 0.012f;
        });

        // Rescue cage chime: struck-bell partials (shaft signal).
        clipChime = BuildClip("Chime", 1.6f, (i, t) =>
        {
            float e = Mathf.Exp(-t * 3f);
            return (Sine(659.3f, t) * 0.5f + Sine(1318.5f, t) * 0.18f + Sine(1976f, t) * 0.08f) * e +
                   (t > 0.35f ? (Sine(523.3f, t) * 0.45f + Sine(1046.5f, t) * 0.15f) * Mathf.Exp(-(t - 0.35f) * 3f) : 0f);
        });

        // Hoist winch: drum motor hum, cable ratchet ticks (loop).
        Noise nw = new Noise(53);
        clipWinch = BuildClip("Winch", 2f, (i, t) =>
            Sine(98f, t) * 0.3f + Sine(196f, t) * 0.12f + Sine(294f, t) * 0.05f + nw.Low(0.05f) * 0.6f +
            ((i % 2205) < 90 ? nw.White() * 0.35f : 0f), true);

        // Drummy roof: hollow, resonant thud of loose strata under a sounding bar.
        Noise nd = new Noise(59);
        clipDrummy = BuildClip("Drummy", 0.8f, (i, t) =>
            nd.White() * Mathf.Exp(-t * 90f) * 0.8f +
            (Sine(92f, t) * 0.7f + Sine(181f, t) * 0.35f + Sine(267f, t) * 0.2f) * Mathf.Exp(-t * 5.5f));

        // Steel gate slam: inharmonic metallic partials.
        Noise ng = new Noise(61);
        clipClang = BuildClip("Clang", 1.4f, (i, t) =>
            (Sine(420f, t) * 0.4f + Sine(1130f, t) * 0.25f + Sine(2210f, t) * 0.12f + Sine(3170f, t) * 0.06f) * Mathf.Exp(-t * 3.2f) +
            ng.White() * Mathf.Exp(-t * 40f) * 0.7f + Sine(70f, t) * Mathf.Exp(-t * 8f) * 0.5f);

        // Electrical short / sparks.
        Noise nz = new Noise(67);
        clipSpark = BuildClip("Spark", 0.9f, (i, t) =>
            nz.White() * (Hash(i / 220, 2, 5) > 0.55f ? 1f : 0.05f) * Mathf.Exp(-t * 4f) * 0.8f + Sine(50f, t) * 0.2f * Mathf.Exp(-t * 6f));

        // Brattice tear + gas outburst hiss.
        Noise nh = new Noise(71);
        clipHiss = BuildClip("GasHiss", 2.5f, (i, t) =>
            nh.Band(0.15f, 0.9f) * Mathf.SmoothStep(0f, 1f, t * 6f) * Mathf.Exp(-t * 0.7f) * 1.3f);

        clipBeep = BuildClip("UIBeep", 0.09f, (i, t) => Sine(1320f, t) * 0.3f * (1f - t / 0.09f));
        clipTick = BuildClip("UITick", 0.04f, (i, t) => Sine(2400f, t) * 0.18f * (1f - t / 0.04f));
        clipGood = BuildClip("UIGood", 0.45f, (i, t) =>
            (t < 0.15f ? Sine(880f, t) : Sine(1318.5f, t)) * 0.3f * Mathf.Exp(-t * 4f));
        clipBad = BuildClip("UIBad", 0.55f, (i, t) =>
            (Mathf.Sign(Sine(150f, t)) * 0.18f + Sine(155f, t) * 0.2f) * (1f - t / 0.55f));

        // Competency PASS signature: a bright rising triad with bell partials. Deliberately
        // unlike every hazard sound in the drill, so a correct call is unmistakable.
        clipPass = BuildClip("UIPass", 0.9f, (i, t) =>
        {
            float a = t < 0.10f ? 1f : 0f;
            float b = (t >= 0.10f && t < 0.22f) ? 1f : 0f;
            float c = t >= 0.22f ? 1f : 0f;
            float n1 = (Sine(659.3f, t) * 0.55f + Sine(1318.5f, t) * 0.14f) * Mathf.Exp(-t * 7f) * a;
            float n2 = (Sine(830.6f, t) * 0.55f + Sine(1661.2f, t) * 0.14f) * Mathf.Exp(-(t - 0.10f) * 7f) * b;
            float n3 = (Sine(987.8f, t) * 0.60f + Sine(1975.5f, t) * 0.18f + Sine(2963f, t) * 0.07f) *
                       Mathf.Exp(-(t - 0.22f) * 3.2f) * c;
            return (n1 + n2 + n3) * 0.6f;
        });

        // Safety-equipment latch: case lid clack, then the buckle seating. Played whenever the
        // trainee actually operates a piece of equipment.
        Noise nl = new Noise(89);
        clipLatch = BuildClip("UILatch", 0.34f, (i, t) =>
        {
            float k1 = nl.White() * Mathf.Exp(-t * 150f) + Sine(260f, t) * Mathf.Exp(-t * 55f) * 0.5f;
            float d = t - 0.11f;
            float k2 = d > 0f ? nl.White() * Mathf.Exp(-d * 95f) * 0.75f + Sine(190f, d) * Mathf.Exp(-d * 38f) * 0.42f : 0f;
            return (k1 + k2) * 0.85f;
        });

        ambSrc.clip = clipVent;
        ambSrc.Play();
    }

    AudioSource MakeLoopSource(float volume)
    {
        AudioSource s = gameObject.AddComponent<AudioSource>();
        s.loop = true;
        s.playOnAwake = false;
        s.volume = volume;
        s.spatialBlend = 0f;
        return s;
    }

    void Play(AudioClip clip, float volume = 1f)
    {
        if (sfx != null && clip != null) sfx.PlayOneShot(clip, volume);
    }

    void StartLoop(AudioSource src, AudioClip clip, float volume, float pitch = 1f)
    {
        if (src == null) return;
        src.clip = clip;
        src.volume = volume;
        src.pitch = pitch;
        if (!src.isPlaying) src.Play();
    }

    // =====================================================================================
    //  HAPTICS  (distinct vibration signatures, not one generic buzz)
    // =====================================================================================
    //  Android exposes amplitude-controlled waveforms from API 26 (VibrationEffect), which is
    //  below our API 29 floor, so every pattern below is available on target hardware. The
    //  whole call is wrapped in try/catch and falls back to Handheld.Vibrate(), so an OEM that
    //  refuses the service can never break the drill.
    private enum Haptic
    {
        Tick,       // equipment operated / UI acknowledged
        Confirm,    // a step completed (respirator donned, cage signalled)
        Success,    // competency check passed
        Warning,    // a scored decision just opened, air running out
        Failure,    // unsafe decision / fatality
        Impact      // rock strike, gate slam, cave-in
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static AndroidJavaObject androidVibrator;
    private static int androidApi = -1;
    private static bool androidHapticsDead;
#endif

    // timings / amplitudes (milliseconds, 0..255) per signature
    private static void HapticPattern(Haptic kind, out long[] timings, out int[] amplitudes)
    {
        switch (kind)
        {
            case Haptic.Tick:    timings = new long[] { 0, 18 };                 amplitudes = new int[] { 0, 110 }; break;
            case Haptic.Confirm: timings = new long[] { 0, 26, 50, 40 };         amplitudes = new int[] { 0, 150, 0, 190 }; break;
            case Haptic.Success: timings = new long[] { 0, 30, 45, 30, 45, 70 }; amplitudes = new int[] { 0, 170, 0, 200, 0, 255 }; break;
            case Haptic.Warning: timings = new long[] { 0, 45, 70, 45 };         amplitudes = new int[] { 0, 200, 0, 200 }; break;
            case Haptic.Failure: timings = new long[] { 0, 260 };                amplitudes = new int[] { 0, 255 }; break;
            default:             timings = new long[] { 0, 90 };                 amplitudes = new int[] { 0, 255 }; break;
        }
    }

    private static void Buzz(Haptic kind)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (androidHapticsDead) { Handheld.Vibrate(); return; }
        try
        {
            if (androidApi < 0)
                using (AndroidJavaClass build = new AndroidJavaClass("android.os.Build$VERSION"))
                    androidApi = build.GetStatic<int>("SDK_INT");

            if (androidVibrator == null)
                using (AndroidJavaClass player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    androidVibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");

            if (androidVibrator == null) { androidHapticsDead = true; Handheld.Vibrate(); return; }

            long[] timings;
            int[] amplitudes;
            HapticPattern(kind, out timings, out amplitudes);

            if (androidApi >= 26)
            {
                using (AndroidJavaClass fx = new AndroidJavaClass("android.os.VibrationEffect"))
                using (AndroidJavaObject effect = fx.CallStatic<AndroidJavaObject>("createWaveform", timings, amplitudes, -1))
                    androidVibrator.Call("vibrate", effect);
            }
            else androidVibrator.Call("vibrate", timings, -1);
        }
        catch (System.Exception e)
        {
            androidHapticsDead = true;
            Debug.LogWarning("[MineSimulationManager] Haptics unavailable, falling back: " + e.Message);
            Handheld.Vibrate();
        }
#elif UNITY_IOS && !UNITY_EDITOR
        Handheld.Vibrate();
#else
        // Editor / desktop: nothing to shake.
#endif
    }

    // Kept so every existing call site still compiles: a plain thud.
    static void Vibrate()
    {
        Buzz(Haptic.Impact);
    }

    // =====================================================================================
    //  PROCEDURAL MINE ENVIRONMENT
    // =====================================================================================
    Material mCoal, mRoof, mFloor, mTimber, mSteel, mRail, mSleeper, mConcrete, mRubber, mPipe, mYellow, mDarkMetal;

    void BuildMine()
    {
        envRoot = new GameObject("MineEnvironment").transform;
        staticRoot = new GameObject("StaticGeometry").transform;
        staticRoot.SetParent(envRoot, false);

        mCoal = Lit(Color.white, texCoal, 2f, 0.32f);
        mRoof = Lit(Color.white, texRock, 2f, 0.12f);
        mFloor = Lit(Color.white, texGravel, 6f, 0.22f);
        mTimber = Lit(Color.white, texWood, 1f, 0.08f);
        mSteel = Lit(Color.white, texRust, 1f, 0.3f, 0.55f);
        mRail = Lit(new Color(0.55f, 0.56f, 0.58f), null, 1f, 0.65f, 0.9f);
        mSleeper = Lit(new Color(0.55f, 0.5f, 0.45f), texWood, 1f, 0.05f);
        mConcrete = Lit(Color.white, texConcrete, 2f, 0.1f);
        mRubber = Lit(new Color(0.04f, 0.04f, 0.045f), null, 1f, 0.4f);
        mPipe = Lit(new Color(0.72f, 0.52f, 0.1f), null, 1f, 0.4f, 0.3f);
        mYellow = Lit(new Color(0.92f, 0.72f, 0.08f), null, 1f, 0.35f);
        mDarkMetal = Lit(new Color(0.16f, 0.17f, 0.18f), null, 1f, 0.45f, 0.7f);
        lampLensOff = Lit(new Color(0.18f, 0.17f, 0.15f), null, 1f, 0.8f);

        BuildDrift();
        BuildCrossCut();
        BuildServices();
        BuildStartArea();
        BuildSoundingStation();
        BuildCaveInZone();
        BuildGasZone();
        BuildSelfRescuerStation();
        BuildJunctionSignage();
        BuildShaftAndCage();

        // Merge all static meshes that share materials -> far fewer draw calls on mobile.
        // TextMeshPro meshes are rebuilt at runtime, so they are excluded from the batch.
        List<GameObject> batch = new List<GameObject>();
        foreach (MeshRenderer mr in staticRoot.GetComponentsInChildren<MeshRenderer>())
            if (mr.GetComponent<TMP_Text>() == null) batch.Add(mr.gameObject);
        StaticBatchingUtility.Combine(batch.ToArray(), staticRoot.gameObject);
    }

    // Irregular coal rib built from jittered slabs plus protruding lumps.
    void BuildRib(Vector3 from, Vector3 to, float inwardSign, Vector3 inward, int seed)
    {
        Vector3 dir = (to - from);
        float len = dir.magnitude;
        dir.Normalize();
        int segs = Mathf.Max(1, Mathf.CeilToInt(len / 1.5f));
        float segLen = len / segs;
        Quaternion baseRot = Quaternion.LookRotation(dir, Vector3.up);
        for (int i = 0; i < segs; i++)
        {
            float j = (Hash(i, 1, seed) - 0.5f) * 0.16f;
            Vector3 c = from + dir * (segLen * (i + 0.5f)) + inward * j;
            Quaternion r = baseRot * Quaternion.Euler(0f, (Hash(i, 2, seed) - 0.5f) * 5f, 0f);
            Box("Rib", staticRoot, new Vector3(c.x, RoofHeight * 0.5f, c.z), new Vector3(0.7f, RoofHeight + 0.4f, segLen + 0.25f), mCoal, r);

            // Lumps / cleat blocks sticking out of the face.
            int lumps = 2 + Mathf.FloorToInt(Hash(i, 3, seed) * 3f);
            for (int k = 0; k < lumps; k++)
            {
                float h = 0.25f + Hash(i, 10 + k, seed) * 2.8f;
                float along = (Hash(i, 20 + k, seed) - 0.5f) * segLen;
                float s = 0.18f + Hash(i, 30 + k, seed) * 0.32f;
                Vector3 p = c + dir * along + inward * (0.33f + s * 0.25f);
                p.y = h;
                Box("Lump", staticRoot, p, new Vector3(s * 1.4f, s, s * 1.2f), mCoal,
                    Quaternion.Euler(Hash(i, 40 + k, seed) * 40f, Hash(i, 50 + k, seed) * 90f, Hash(i, 60 + k, seed) * 30f));
            }
        }
    }

    // Arched steel support set (legs + elliptical crown) with timber lagging pads.
    void BuildArch(Vector3 centre, float yawDeg, float halfSpan)
    {
        Quaternion q = Quaternion.Euler(0f, yawDeg, 0f);
        float legH = 2.25f, ry = RoofHeight - legH - 0.12f;
        Vector3 Local(float x, float y) => centre + q * new Vector3(x, y, 0f);

        Box("ArchLegL", staticRoot, Local(-halfSpan, legH * 0.5f), new Vector3(0.16f, legH, 0.13f), mSteel, q);
        Box("ArchLegR", staticRoot, Local(halfSpan, legH * 0.5f), new Vector3(0.16f, legH, 0.13f), mSteel, q);
        Box("ArchFootL", staticRoot, Local(-halfSpan, 0.04f), new Vector3(0.34f, 0.08f, 0.3f), mSteel, q);
        Box("ArchFootR", staticRoot, Local(halfSpan, 0.04f), new Vector3(0.34f, 0.08f, 0.3f), mSteel, q);

        const int segs = 10;
        Vector3 prev = Local(-halfSpan, legH);
        for (int i = 1; i <= segs; i++)
        {
            float a = Mathf.PI * (1f - (float)i / segs);
            Vector3 p = Local(Mathf.Cos(a) * halfSpan, legH + Mathf.Sin(a) * ry);
            Vector3 d = p - prev;
            Box("ArchSeg", staticRoot, (p + prev) * 0.5f, new Vector3(d.magnitude + 0.04f, 0.16f, 0.13f), mSteel,
                q * Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, Vector3.Dot(d, q * Vector3.right)) * Mathf.Rad2Deg));
            prev = p;
        }
        // Fish-plate joints at the leg/crown splice.
        Box("SpliceL", staticRoot, Local(-halfSpan, legH), new Vector3(0.22f, 0.26f, 0.17f), mDarkMetal, q);
        Box("SpliceR", staticRoot, Local(halfSpan, legH), new Vector3(0.22f, 0.26f, 0.17f), mDarkMetal, q);
    }

    void BuildDrift()
    {
        float len = DriftEndZ - DriftStartZ;
        float midZ = (DriftStartZ + DriftEndZ) * 0.5f;

        // Floor keeps its collider: loose rocks land on it.
        Box("DriftFloor", staticRoot, new Vector3(0f, -0.1f, midZ), new Vector3(DriftHalfWidth * 2f + 0.6f, 0.2f, len), mFloor, null, true);
        Box("DriftRoof", staticRoot, new Vector3(0f, RoofHeight + 0.1f, midZ), new Vector3(DriftHalfWidth * 2f + 0.6f, 0.2f, len), mRoof);
        // Drainage gutter on the left side.
        Box("Gutter", staticRoot, new Vector3(-2.05f, 0.005f, midZ), new Vector3(0.28f, 0.02f, len), Lit(new Color(0.03f, 0.03f, 0.035f), null, 1f, 0.9f));

        BuildRib(new Vector3(-DriftHalfWidth - 0.3f, 0f, DriftStartZ), new Vector3(-DriftHalfWidth - 0.3f, 0f, DriftEndZ), 1f, Vector3.right, 101);
        BuildRib(new Vector3(DriftHalfWidth + 0.3f, 0f, DriftStartZ), new Vector3(DriftHalfWidth + 0.3f, 0f, DriftEndZ), -1f, Vector3.left, 202);
        Box("DriftEndFace", staticRoot, new Vector3(0f, RoofHeight * 0.5f, DriftStartZ - 0.3f), new Vector3(DriftHalfWidth * 2f + 1f, RoofHeight + 0.4f, 0.6f), mCoal);

        // Steel arches every 3 m, timber lagging boards spanning between them.
        for (float z = DriftStartZ + 1.5f; z <= DriftEndZ - 1f; z += 3f)
        {
            BuildArch(new Vector3(0f, 0f, z), 0f, 2.2f);
            for (int k = -2; k <= 2; k++)
            {
                float x = k * 0.75f;
                float y = RoofHeight - 0.08f - Mathf.Abs(k) * 0.12f;
                Box("Lagging", staticRoot, new Vector3(x, y, z + 1.5f), new Vector3(0.2f, 0.05f, 3.1f), mTimber,
                    Quaternion.Euler(0f, 0f, -k * 7f));
            }
        }

        // Track: rails on wooden sleepers with ballast.
        Box("Ballast", staticRoot, new Vector3(0f, 0.01f, midZ), new Vector3(1.9f, 0.04f, len), Lit(new Color(0.2f, 0.19f, 0.18f), texGravel, 10f, 0.1f));
        for (float z = DriftStartZ + 0.4f; z < DriftEndZ - 0.3f; z += 0.65f)
            Box("Sleeper", staticRoot, new Vector3(0f, 0.05f, z), new Vector3(1.55f, 0.08f, 0.2f), mSleeper,
                Quaternion.Euler(0f, (Hash(Mathf.RoundToInt(z * 10f), 4, 5) - 0.5f) * 4f, 0f));
        foreach (float x in new[] { -0.55f, 0.55f })
        {
            Box("RailHead", staticRoot, new Vector3(x, 0.15f, midZ), new Vector3(0.06f, 0.05f, len), mRail);
            Box("RailWeb", staticRoot, new Vector3(x, 0.115f, midZ), new Vector3(0.025f, 0.05f, len), mDarkMetal);
            Box("RailFoot", staticRoot, new Vector3(x, 0.095f, midZ), new Vector3(0.11f, 0.015f, len), mDarkMetal);
        }

        // Bulkhead roof lamps every 6 m.
        for (float z = DriftStartZ + 3f; z <= DriftEndZ - 1f; z += 6f) BuildRoofLamp(new Vector3(0.9f, RoofHeight - 0.18f, z));
    }

    void BuildRoofLamp(Vector3 pos)
    {
        Box("LampHousing", staticRoot, pos + Vector3.up * 0.06f, new Vector3(0.22f, 0.1f, 0.5f), mDarkMetal);
        Material lens = Glow(new Color(1f, 0.86f, 0.6f));
        lampLensMats.Add(lens);
        GameObject l = Box("LampLens", envRoot, pos, new Vector3(0.17f, 0.03f, 0.42f), lens);
        l.name = "LampLens_" + lampLensMats.Count;
        Box("LampGuard", staticRoot, pos + Vector3.down * 0.03f, new Vector3(0.2f, 0.015f, 0.04f), mDarkMetal);
        roofLights.Add(PointLight("RoofLight", envRoot, pos + Vector3.down * 0.2f, new Color(1f, 0.82f, 0.55f), 9.5f, 1.7f));
    }

    void BuildCrossCut()
    {
        float zMin = XcutZ - XcutHalfDepth, zMax = XcutZ + XcutHalfDepth;
        float width = XcutMaxX - XcutMinX;
        float midX = (XcutMinX + XcutMaxX) * 0.5f;

        Box("XFloor", staticRoot, new Vector3(midX, -0.1f, XcutZ), new Vector3(width, 0.2f, zMax - zMin + 0.6f), mFloor, null, true);
        // Roof everywhere except over the hoist shaft opening.
        float shaftL = ShaftX - 1.6f;
        Box("XRoofMain", staticRoot, new Vector3((XcutMinX + shaftL) * 0.5f, RoofHeight + 0.1f, XcutZ), new Vector3(shaftL - XcutMinX, 0.2f, zMax - zMin + 0.6f), mRoof);

        BuildRib(new Vector3(XcutMinX, 0f, zMax + 0.3f), new Vector3(XcutMaxX, 0f, zMax + 0.3f), -1f, Vector3.back, 303);
        BuildRib(new Vector3(XcutMinX, 0f, zMin - 0.3f), new Vector3(-DriftHalfWidth - 0.6f, 0f, zMin - 0.3f), 1f, Vector3.forward, 404);
        BuildRib(new Vector3(DriftHalfWidth + 0.6f, 0f, zMin - 0.3f), new Vector3(XcutMaxX, 0f, zMin - 0.3f), 1f, Vector3.forward, 505);
        Box("XEndLeft", staticRoot, new Vector3(XcutMinX - 0.3f, RoofHeight * 0.5f, XcutZ), new Vector3(0.6f, RoofHeight + 0.4f, zMax - zMin + 1f), mCoal);
        Box("XEndRight", staticRoot, new Vector3(XcutMaxX + 0.3f, RoofHeight * 0.5f, XcutZ), new Vector3(0.6f, RoofHeight + 0.4f, zMax - zMin + 1f), mConcrete);

        // Arches across the cross-cut (rotated 90 degrees), skipping the drift mouth and shaft.
        for (float x = XcutMinX + 1.5f; x <= ShaftX - 2.5f; x += 3f)
        {
            if (Mathf.Abs(x) < 3.2f) continue;
            BuildArch(new Vector3(x, 0f, XcutZ), 90f, 2.2f);
        }
        // Lintel / girder over the drift mouth.
        Box("MouthGirder", staticRoot, new Vector3(0f, RoofHeight - 0.15f, zMin), new Vector3(DriftHalfWidth * 2f + 1.2f, 0.3f, 0.3f), mSteel);
    }

    // Power cable on the right rib, compressed-air pipe on the left, ventilation duct overhead.
    void BuildServices()
    {
        for (float z = DriftStartZ + 1f; z < DriftEndZ - 0.5f; z += 2f)
        {
            float sag = 0.07f;
            Rod("PowerCable", staticRoot, new Vector3(2.08f, 2.65f, z), new Vector3(2.08f, 2.65f - sag, z + 1f), 0.045f, mRubber);
            Rod("PowerCable", staticRoot, new Vector3(2.08f, 2.65f - sag, z + 1f), new Vector3(2.08f, 2.65f, z + 2f), 0.045f, mRubber);
            Box("CableHook", staticRoot, new Vector3(2.12f, 2.7f, z), new Vector3(0.04f, 0.12f, 0.04f), mDarkMetal);
        }
        Rod("AirPipe", staticRoot, new Vector3(-2.02f, 0.55f, DriftStartZ + 0.5f), new Vector3(-2.02f, 0.55f, DriftEndZ - 0.6f), 0.12f, mPipe);
        for (float z = DriftStartZ + 2f; z < DriftEndZ; z += 4f)
            Box("PipeFlange", staticRoot, new Vector3(-2.02f, 0.55f, z), new Vector3(0.18f, 0.18f, 0.04f), mPipe);
        Rod("VentDuct", staticRoot, new Vector3(-1.45f, 3.05f, DriftStartZ + 0.5f), new Vector3(-1.45f, 3.05f, 16f), 0.5f,
            Lit(new Color(0.85f, 0.62f, 0.12f), null, 1f, 0.2f));
    }

    void BuildStartArea()
    {
        // Man-riding tub / mine car on the rails.
        Transform tub = new GameObject("MineCar").transform;
        tub.SetParent(staticRoot, false);
        Material tubMat = Lit(new Color(0.3f, 0.32f, 0.34f), texRust, 1f, 0.3f, 0.5f);
        Box("TubBody", tub, new Vector3(0f, 0.75f, -4.6f), new Vector3(1.2f, 0.8f, 2.2f), tubMat);
        Box("TubCoal", tub, new Vector3(0f, 1.12f, -4.6f), new Vector3(1.05f, 0.12f, 2.0f), mCoal);
        foreach (float x in new[] { -0.55f, 0.55f })
            foreach (float z in new[] { -5.3f, -3.9f })
                Prim(PrimitiveType.Cylinder, "Wheel", tub, new Vector3(x, 0.28f, z), new Vector3(0.36f, 0.04f, 0.36f), mDarkMetal, Quaternion.Euler(0f, 0f, 90f));

        Sign("EntrySign", staticRoot, new Vector3(-0.6f, 2.55f, -9.6f), Quaternion.Euler(0f, 180f, 0f), new Vector2(2.4f, 0.5f),
             new Color(0.05f, 0.25f, 0.12f), Color.white, "INTAKE DRIFT 3  -  SEAM II", 2.2f);
        Sign("PPESign", staticRoot, new Vector3(-2.12f, 1.75f, -6.5f), Quaternion.Euler(0f, -90f, 0f), new Vector2(1.1f, 0.75f),
             new Color(0.08f, 0.28f, 0.62f), Color.white, "MANDATORY\nHELMET  CAP LAMP\nSELF-RESCUER\nGAS DETECTOR", 1.25f, false, Color.white);
        Sign("DGMSBoard", staticRoot, new Vector3(2.12f, 1.75f, -3f), Quaternion.Euler(0f, 90f, 0f), new Vector2(1.3f, 0.8f),
             new Color(0.92f, 0.9f, 0.84f), new Color(0.1f, 0.1f, 0.1f),
             "<size=140%>SAFETY FIRST</size>\nTEST ROOF BEFORE YOU ADVANCE\nREPORT HOLLOW ROOF\nKNOW YOUR ESCAPE ROUTE", 1.05f, false, new Color(0.75f, 0.1f, 0.1f));
    }

    Interactable RegisterInteractable(GameObject go, string label, string action, Material glowMat, Color glowColor, float range, System.Action onUse)
    {
        Collider c = go.GetComponent<Collider>();
        if (c == null) c = go.AddComponent<BoxCollider>();
        Interactable it = new Interactable
        {
            label = label, action = action, collider = c, glowMat = glowMat, glowColor = glowColor, range = range, onUse = onUse
        };
        interactables[c] = it;
        return it;
    }

    // ---------------------------------------------------------------- Phase 1 props
    void BuildSoundingStation()
    {
        Transform root = new GameObject("StrataSoundingStation").transform;
        root.SetParent(envRoot, false);

        // Red/white barrier tape across the drift with a hanging STOP board.
        soundingTape = new GameObject("BarrierTape");
        soundingTape.transform.SetParent(root, false);
        Material red = Lit(new Color(0.8f, 0.08f, 0.06f), null, 1f, 0.5f), white = Lit(new Color(0.9f, 0.9f, 0.88f), null, 1f, 0.5f);
        float tz = SoundingGateZ + 0.35f;
        for (int i = 0; i < 16; i++)
        {
            float x = -2.2f + i * 0.275f + 0.1375f;
            Box("Tape", soundingTape.transform, new Vector3(x, 1.15f + Mathf.Sin(i * 0.4f) * 0.015f, tz), new Vector3(0.275f, 0.07f, 0.005f), (i & 1) == 0 ? red : white);
        }
        Box("TapePostL", soundingTape.transform, new Vector3(-2.2f, 0.6f, tz), new Vector3(0.06f, 1.2f, 0.06f), mYellow);
        Box("TapePostR", soundingTape.transform, new Vector3(2.2f, 0.6f, tz), new Vector3(0.06f, 1.2f, 0.06f), mYellow);
        GameObject stop = Sign("StopBoard", root, new Vector3(0f, 1.5f, tz + 0.01f), Quaternion.identity, new Vector2(1.2f, 0.5f),
                               new Color(0.8f, 0.07f, 0.05f), Color.white, "STOP\nTEST ROOF BEFORE ADVANCING", 1.3f, false, Color.white);
        stop.transform.SetParent(soundingTape.transform, true);

        // Loose, fractured roof slab (the sounding target).
        // Unscaled pivot so child details keep their own scale while the slab animates.
        Quaternion slabRot = Quaternion.Euler(3f, 0f, -2.5f);
        strataSlab = new GameObject("LooseStrataSlab").transform;
        strataSlab.SetParent(root, false);
        strataSlab.SetPositionAndRotation(new Vector3(0.25f, RoofHeight - 0.16f, SlabZ), slabRot);
        Vector3 sp = strataSlab.position;
        GameObject slabMesh = Box("SlabMesh", strataSlab, sp, new Vector3(1.8f, 0.17f, 1.4f), mRoof, slabRot, true);
        Material crack = Lit(new Color(0.02f, 0.02f, 0.02f));
        Box("Fracture1", strataSlab, sp + slabRot * new Vector3(-0.2f, -0.09f, 0.1f), new Vector3(1.3f, 0.01f, 0.025f), crack, slabRot * Quaternion.Euler(0f, 24f, 0f));
        Box("Fracture2", strataSlab, sp + slabRot * new Vector3(0.3f, -0.09f, -0.25f), new Vector3(0.9f, 0.01f, 0.02f), crack, slabRot * Quaternion.Euler(0f, -38f, 0f));
        Box("Fracture3", strataSlab, sp + slabRot * new Vector3(0.1f, -0.09f, 0.4f), new Vector3(0.6f, 0.01f, 0.02f), crack, slabRot * Quaternion.Euler(0f, 70f, 0f));

        // Spray-painted inspection outline on the slab underside (pulses while untested).
        Material paint = Glow(UIAmber);
        Box("PaintN", strataSlab, sp + slabRot * new Vector3(0f, -0.09f, 0.62f), new Vector3(1.6f, 0.005f, 0.04f), paint, slabRot);
        Box("PaintS", strataSlab, sp + slabRot * new Vector3(0f, -0.09f, -0.62f), new Vector3(1.6f, 0.005f, 0.04f), paint, slabRot);
        Box("PaintE", strataSlab, sp + slabRot * new Vector3(0.8f, -0.09f, 0f), new Vector3(0.04f, 0.005f, 1.24f), paint, slabRot);
        Box("PaintW", strataSlab, sp + slabRot * new Vector3(-0.8f, -0.09f, 0f), new Vector3(0.04f, 0.005f, 1.24f), paint, slabRot);

        // Unstable leaning timber prop with split cap board.
        Prim(PrimitiveType.Cylinder, "LeaningProp", root, new Vector3(1.35f, 1.62f, SlabZ + 0.7f), new Vector3(0.2f, 1.62f, 0.2f), mTimber, Quaternion.Euler(4f, 0f, 9f));
        Box("PropCap", root, new Vector3(1.48f, RoofHeight - 0.1f, SlabZ + 0.72f), new Vector3(0.5f, 0.08f, 0.22f), mTimber, Quaternion.Euler(0f, 0f, 6f));
        Box("PropSplit", root, new Vector3(1.3f, 1.2f, SlabZ + 0.61f), new Vector3(0.015f, 0.7f, 0.03f), crack, Quaternion.Euler(0f, 0f, 9f));

        slabDust = Particles("SlabDust", root, new Vector3(0.25f, RoofHeight - 0.3f, SlabZ), new Color(0.45f, 0.42f, 0.38f, 0.5f),
                             new Color(0.3f, 0.28f, 0.25f, 0f), 2.2f, 0.25f, new Vector2(0.2f, 0.6f), 0f, 0.15f,
                             ParticleSystemShapeType.Box, new Vector3(1.5f, 0.05f, 1.2f), 60, false, 2.5f);

        slabIt = RegisterInteractable(slabMesh, "LOOSE ROOF STRATA", "SOUND ROOF", paint, UIAmber, 4.5f, OnSoundRoof);
    }

    // ---------------------------------------------------------------- Phase 2 props
    void BuildCaveInZone()
    {
        Transform root = new GameObject("CaveInZone").transform;
        root.SetParent(envRoot, false);

        // Boulders that will seal the intake drift behind the miner (hidden until the fall).
        int idx = 0;
        for (int layer = 0; layer < 4; layer++)
        {
            int count = 9 - layer;
            for (int k = 0; k < count; k++, idx++)
            {
                float x = -2.1f + (k + 0.5f) * (4.2f / count) + (Hash(idx, 1, 900) - 0.5f) * 0.35f;
                float z = CollapseZ + (Hash(idx, 2, 900) - 0.5f) * (3.2f - layer * 0.5f);
                float s = 0.75f + Hash(idx, 3, 900) * 0.6f - layer * 0.08f;
                Vector3 target = new Vector3(x, 0.32f + layer * 0.72f + Hash(idx, 4, 900) * 0.15f, z);
                GameObject b = Prim(PrimitiveType.Sphere, "Boulder_" + idx, root, new Vector3(x, RoofHeight - 0.2f, z),
                                    new Vector3(s * 1.25f, s * 0.8f, s), (idx % 3 == 0) ? mCoal : mRoof, Random.rotationUniform);
                b.SetActive(false);
                rubble.Add(b.transform);
                rubbleTargets.Add(target);
            }
        }
        // Flat slabs to close the top of the pile right up to the roof.
        for (int k = 0; k < 4; k++, idx++)
        {
            Vector3 target = new Vector3(-1.6f + k * 1.05f, RoofHeight - 0.45f, CollapseZ + (Hash(k, 5, 901) - 0.5f) * 0.8f);
            GameObject slab = Box("FallenSlab_" + k, root, new Vector3(target.x, RoofHeight - 0.1f, target.z), new Vector3(1.2f, 0.45f, 1.6f), mRoof,
                                  Quaternion.Euler(Hash(k, 6, 901) * 20f - 10f, Hash(k, 7, 901) * 40f, Hash(k, 8, 901) * 20f - 10f));
            slab.SetActive(false);
            rubble.Add(slab.transform);
            rubbleTargets.Add(target);
        }

        // Smaller loose rocks with real physics that bounce around the miner.
        for (int i = 0; i < 12; i++)
        {
            float s = 0.12f + Hash(i, 9, 902) * 0.2f;
            GameObject r = Prim(PrimitiveType.Sphere, "LooseRock_" + i, root,
                                new Vector3((Hash(i, 10, 902) - 0.5f) * 3.2f, RoofHeight - 0.25f, CollapseZ + 1.4f + Hash(i, 11, 902) * 3.2f),
                                new Vector3(s * 1.3f, s, s * 1.1f), mRoof, Random.rotationUniform, true);
            r.layer = 2; // Ignore Raycast: never blocks interaction rays
            Rigidbody rb = r.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.mass = 15f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            r.SetActive(false);
            looseRocks.Add(rb);
        }

        dustBurst = Particles("CaveInDust", root, new Vector3(0f, 1.6f, CollapseZ + 0.8f), new Color(0.42f, 0.39f, 0.34f, 0.85f),
                              new Color(0.25f, 0.23f, 0.2f, 0f), 5.5f, 2.2f, new Vector2(1.2f, 2.6f), 0f, -0.02f,
                              ParticleSystemShapeType.Box, new Vector3(4.2f, 2.8f, 2.5f), 160, false, 2.2f);
        gritFall = Particles("RoofGrit", root, new Vector3(0f, RoofHeight - 0.05f, CaveTriggerZ), new Color(0.32f, 0.3f, 0.27f, 0.9f),
                             new Color(0.25f, 0.23f, 0.2f, 0f), 1.2f, 0.1f, new Vector2(0.02f, 0.06f), 0f, 1f,
                             ParticleSystemShapeType.Box, new Vector3(4f, 0.05f, 4f), 300, true, 1f);
    }

    // ---------------------------------------------------------------- Phase 3 props
    void BuildGasZone()
    {
        Transform root = new GameObject("GasZone").transform;
        root.SetParent(envRoot, false);

        // Ventilation brattice cloth (jute sheet on a timber frame) on the left rib.
        Box("BratticeFrameTop", staticRoot, new Vector3(-1.95f, 2.95f, 19.5f), new Vector3(0.1f, 0.1f, 2.2f), mTimber);
        Box("BratticeFrameL", staticRoot, new Vector3(-1.95f, 1.5f, 18.4f), new Vector3(0.1f, 3f, 0.1f), mTimber);
        Box("BratticeFrameR", staticRoot, new Vector3(-1.95f, 1.5f, 20.6f), new Vector3(0.1f, 3f, 0.1f), mTimber);
        brattice = Box("BratticeCloth", root, new Vector3(-1.93f, 1.6f, 19.5f), new Vector3(0.02f, 2.6f, 2.1f),
                       Lit(new Color(0.62f, 0.52f, 0.3f), texWood, 3f, 0.02f)).transform;
        Sign("BratticeTag", staticRoot, new Vector3(-1.9f, 2.4f, 19.5f), Quaternion.Euler(0f, -90f, 0f), new Vector2(0.8f, 0.3f),
             new Color(0.95f, 0.85f, 0.1f), Color.black, "VENTILATION BRATTICE\nDO NOT DAMAGE", 0.9f);

        // Gas haze filling the drift once the pocket ruptures.
        gasHaze = Particles("GasHaze", root, new Vector3(0f, 1.4f, 23.5f), new Color(0.45f, 0.5f, 0.4f, 0.22f),
                            new Color(0.3f, 0.34f, 0.28f, 0f), 7f, 0.15f, new Vector2(2f, 3.5f), 18f, -0.005f,
                            ParticleSystemShapeType.Box, new Vector3(4.2f, 2.4f, 16f), 140, true, 1.6f);
        sparks = Particles("ShortCircuitSparks", root, new Vector3(0.9f, RoofHeight - 0.25f, 20f), new Color(1f, 0.85f, 0.4f, 1f),
                           new Color(1f, 0.35f, 0.05f, 0f), 0.7f, 3.5f, new Vector2(0.02f, 0.05f), 0f, 1.6f,
                           ParticleSystemShapeType.Cone, new Vector3(1f, 1f, 1f), 120, false, 0.6f);
        sparks.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        // Battery-backed red strobe beacons (off until the gas alarm trips).
        Vector3[] beacons = { new Vector3(-2.05f, 2.75f, 17.5f), new Vector3(2.05f, 2.75f, 24f), new Vector3(-2.05f, 2.75f, 31f), new Vector3(2.05f, 2.75f, 9.5f) };
        foreach (Vector3 p in beacons)
        {
            Box("BeaconBase", staticRoot, p + Vector3.down * 0.1f, new Vector3(0.16f, 0.08f, 0.16f), mDarkMetal);
            Material dome = Glow(new Color(0.25f, 0.03f, 0.03f));
            beaconMats.Add(dome);
            Prim(PrimitiveType.Sphere, "BeaconDome", root, p, new Vector3(0.14f, 0.12f, 0.14f), dome);
            GameObject sl = new GameObject("BeaconSpot");
            sl.transform.SetParent(root, false);
            sl.transform.position = p;
            Light l = sl.AddComponent<Light>();
            l.type = LightType.Spot;
            l.spotAngle = 70f;
            l.range = 14f;
            l.intensity = 0f;
            l.color = new Color(1f, 0.08f, 0.04f);
            l.shadows = LightShadows.None;
            beaconLights.Add(l);
        }

        Sign("RescuerDirection", staticRoot, new Vector3(2.12f, 2.1f, 18f), Quaternion.Euler(0f, 90f, 0f), new Vector2(1.2f, 0.4f),
             new Color(0.0f, 0.45f, 0.2f), Color.white, "SELF-RESCUER CACHE  >>  9 m", 1.15f, true);
    }

    void BuildSelfRescuerStation()
    {
        Transform root = new GameObject("SelfRescuerStation").transform;
        root.SetParent(envRoot, false);
        Vector3 p = new Vector3(2.0f, 1.25f, StationZ);

        Material body = Lit(new Color(0.06f, 0.42f, 0.2f), null, 1f, 0.45f, 0.2f);
        GameObject cabinet = Box("SCSRCabinet", root, p, new Vector3(0.4f, 1.1f, 0.9f), body, null, true);
        Box("CabinetDoorGlass", root, p + new Vector3(-0.205f, 0.05f, 0f), new Vector3(0.01f, 0.85f, 0.75f),
            Glow(new Color(0.55f, 0.72f, 0.62f)));
        Material canisterMat = Lit(new Color(0.95f, 0.75f, 0.1f), null, 1f, 0.5f, 0.3f);
        for (int i = 0; i < 3; i++)
            Prim(PrimitiveType.Cylinder, "Canister", root, p + new Vector3(-0.1f, 0.05f, -0.24f + i * 0.24f), new Vector3(0.17f, 0.2f, 0.17f), canisterMat);
        Box("CabinetHandle", root, p + new Vector3(-0.22f, 0.05f, 0.33f), new Vector3(0.03f, 0.22f, 0.04f), mDarkMetal);

        // Pulsing green LED strip (interaction glow) and battery-backed station lamp.
        Material led = Glow(UIGreen);
        Box("LEDStripTop", root, p + new Vector3(-0.21f, 0.6f, 0f), new Vector3(0.02f, 0.04f, 0.92f), led);
        Box("LEDStripBottom", root, p + new Vector3(-0.21f, -0.6f, 0f), new Vector3(0.02f, 0.04f, 0.92f), led);
        Sign("SCSRSign", root, p + new Vector3(-0.22f, 0.9f, 0f), Quaternion.Euler(0f, 90f, 0f), new Vector2(0.95f, 0.38f),
             new Color(0.02f, 0.55f, 0.25f), Color.white, "SELF-RESCUER\nSCSR  O2  CACHE", 1.05f, true, Color.white);
        stationLight = PointLight("StationLight", root, p + new Vector3(-0.8f, 0.8f, 0f), new Color(0.55f, 1f, 0.7f), 11f, 2.8f);

        stationIt = RegisterInteractable(cabinet, "SELF-RESCUER CACHE", "DON RESPIRATOR", led, UIGreen, 4.2f, OnSelfRescuer);
    }

    // ---------------------------------------------------------------- Phase 4 props
    void BuildJunctionSignage()
    {
        Transform root = new GameObject("JunctionSignage").transform;
        root.SetParent(envRoot, false);
        float wallZ = XcutZ + XcutHalfDepth - 0.45f;

        Sign("JunctionLeft", staticRoot, new Vector3(-1.05f, 2.3f, wallZ), Quaternion.identity, new Vector2(1.9f, 0.62f),
             new Color(0.62f, 0.06f, 0.05f), Color.white, "<<  RETURN AIRWAY\nABANDONED WORKINGS", 1.2f, false, Color.white);
        Sign("JunctionRight", staticRoot, new Vector3(1.05f, 2.3f, wallZ), Quaternion.identity, new Vector2(1.9f, 0.62f),
             new Color(0.0f, 0.5f, 0.22f), Color.white, "INTAKE ESCAPEWAY  >>\nSHAFT BOTTOM 14 m", 1.2f, true, Color.white);

        // Left: abandoned workings - broken timber barricade, danger boards, smoke, dim red glow.
        float bx = -5.6f;
        Box("BarricadeX1", staticRoot, new Vector3(bx, 1.3f, XcutZ), new Vector3(0.1f, 0.18f, 5.6f), mTimber, Quaternion.Euler(28f, 0f, 0f));
        Box("BarricadePost1", staticRoot, new Vector3(bx, 1.5f, XcutZ - 2.2f), new Vector3(0.18f, 3f, 0.18f), mTimber);
        Box("BarricadePost2", staticRoot, new Vector3(bx, 1.5f, XcutZ + 2.2f), new Vector3(0.18f, 3f, 0.18f), mTimber);
        Box("BarricadeFallen", staticRoot, new Vector3(bx + 0.6f, 0.12f, XcutZ + 0.4f), new Vector3(0.1f, 0.18f, 4f), mTimber, Quaternion.Euler(0f, 18f, 0f));
        Sign("DangerBoard1", staticRoot, new Vector3(bx + 0.1f, 2.35f, XcutZ - 1.1f), Quaternion.Euler(0f, -90f, 0f), new Vector2(1.6f, 0.75f),
             new Color(0.95f, 0.92f, 0.88f), new Color(0.75f, 0.05f, 0.05f), "DANGER\nABANDONED WORKINGS\nNO ENTRY", 1.25f, false, new Color(0.75f, 0.05f, 0.05f));
        Sign("DangerBoard2", staticRoot, new Vector3(bx + 0.1f, 2.35f, XcutZ + 1.1f), Quaternion.Euler(0f, -90f, 0f), new Vector2(1.6f, 0.75f),
             new Color(0.95f, 0.8f, 0.05f), Color.black, "UNVENTILATED DIP\nBLACKDAMP / CO\nFATAL ATMOSPHERE", 1.2f, false, Color.black);
        for (int i = 0; i < 10; i++)
        {
            float s = 0.4f + Hash(i, 1, 950) * 0.7f;
            Prim(PrimitiveType.Sphere, "OldFall", staticRoot, new Vector3(XcutMinX + 0.8f + Hash(i, 2, 950) * 2.5f, s * 0.35f, XcutZ + (Hash(i, 3, 950) - 0.5f) * 4f),
                 new Vector3(s * 1.4f, s * 0.8f, s), mRoof, Quaternion.Euler(0f, Hash(i, 4, 950) * 180f, 0f));
        }
        leftSmoke = Particles("ReturnSmoke", root, new Vector3(-11.5f, 1.4f, XcutZ), new Color(0.22f, 0.22f, 0.2f, 0.55f),
                              new Color(0.1f, 0.1f, 0.1f, 0f), 6f, 0.35f, new Vector2(1.8f, 3.2f), 14f, -0.01f,
                              ParticleSystemShapeType.Box, new Vector3(10f, 2.4f, 4.2f), 120, true, 1.5f);
        leftSmoke.Play();
        PointLight("ReturnGlow", root, new Vector3(-12f, 1.2f, XcutZ), new Color(0.8f, 0.15f, 0.05f), 7f, 0.5f);

        // Right: reflective green chevrons and an illuminated tactile lifeline with direction cones.
        Material green = Glow(new Color(0.15f, 1f, 0.45f));
        for (float x = 3.5f; x <= 12.5f; x += 2.25f)
        {
            Vector3 c = new Vector3(x, 1.95f, wallZ + 0.02f);
            Box("ChevronUp", staticRoot, c + new Vector3(0f, 0.09f, 0f), new Vector3(0.32f, 0.07f, 0.02f), green, Quaternion.Euler(0f, 0f, -40f));
            Box("ChevronDn", staticRoot, c + new Vector3(0f, -0.09f, 0f), new Vector3(0.32f, 0.07f, 0.02f), green, Quaternion.Euler(0f, 0f, 40f));
        }
        Material line = Glow(new Color(0.85f, 0.95f, 0.2f));
        Rod("Lifeline", staticRoot, new Vector3(2.8f, 1.25f, wallZ - 0.05f), new Vector3(ShaftX - 1.8f, 1.25f, wallZ - 0.05f), 0.03f, line);
        for (float x = 4f; x < ShaftX - 2f; x += 2.5f)
        {
            // Cone pair: the wide end points AWAY from the exit (tactile convention).
            Prim(PrimitiveType.Cylinder, "LifelineCone", staticRoot, new Vector3(x, 1.25f, wallZ - 0.05f), new Vector3(0.09f, 0.06f, 0.09f), line, Quaternion.Euler(0f, 0f, 90f));
            Prim(PrimitiveType.Cylinder, "LifelineConeTip", staticRoot, new Vector3(x + 0.08f, 1.25f, wallZ - 0.05f), new Vector3(0.05f, 0.05f, 0.05f), line, Quaternion.Euler(0f, 0f, 90f));
        }
        PointLight("EscapewayGlow", root, new Vector3(9f, 2.4f, XcutZ), new Color(0.2f, 1f, 0.45f), 7f, 0.6f);
    }

    // ---------------------------------------------------------------- Phase 5 props
    void BuildShaftAndCage()
    {
        float zMin = XcutZ - XcutHalfDepth, zMax = XcutZ + XcutHalfDepth;
        float shaftL = ShaftX - 1.6f, shaftR = XcutMaxX;
        const float shaftTop = 42f;

        // Concrete-lined vertical shaft rising out of the cross-cut.
        Box("ShaftWallIn", staticRoot, new Vector3(shaftL - 0.2f, (RoofHeight + shaftTop) * 0.5f, XcutZ), new Vector3(0.4f, shaftTop - RoofHeight, zMax - zMin + 0.8f), mConcrete);
        Box("ShaftWallOut", staticRoot, new Vector3(shaftR + 0.2f, shaftTop * 0.5f, XcutZ), new Vector3(0.4f, shaftTop, zMax - zMin + 0.8f), mConcrete);
        Box("ShaftWallN", staticRoot, new Vector3((shaftL + shaftR) * 0.5f, shaftTop * 0.5f, zMax + 0.2f), new Vector3(shaftR - shaftL + 0.8f, shaftTop, 0.4f), mConcrete);
        Box("ShaftWallS", staticRoot, new Vector3((shaftL + shaftR) * 0.5f, shaftTop * 0.5f, zMin - 0.2f), new Vector3(shaftR - shaftL + 0.8f, shaftTop, 0.4f), mConcrete);
        for (float y = RoofHeight + 1.5f; y < shaftTop; y += 3f)
        {
            Box("ShaftBuntonN", staticRoot, new Vector3((shaftL + shaftR) * 0.5f, y, zMax - 0.05f), new Vector3(shaftR - shaftL, 0.16f, 0.1f), mSteel);
            Box("ShaftBuntonS", staticRoot, new Vector3((shaftL + shaftR) * 0.5f, y, zMin + 0.05f), new Vector3(shaftR - shaftL, 0.16f, 0.1f), mSteel);
        }
        // Guide rails the cage rides on.
        Box("GuideN", staticRoot, new Vector3(ShaftX, shaftTop * 0.5f, zMax - 0.12f), new Vector3(0.12f, shaftTop, 0.06f), mRail);
        Box("GuideS", staticRoot, new Vector3(ShaftX, shaftTop * 0.5f, zMin + 0.12f), new Vector3(0.12f, shaftTop, 0.06f), mRail);
        for (float y = 9f; y < shaftTop; y += 9f) PointLight("ShaftLight", envRoot, new Vector3(shaftR - 0.4f, y, XcutZ), new Color(1f, 0.9f, 0.7f), 7f, 0.8f);

        // Landing: steel deck with yellow/black edge marking, shaft-bottom sign, signal box.
        Box("LandingDeck", staticRoot, new Vector3(shaftL - 0.8f, 0.02f, XcutZ), new Vector3(1.6f, 0.04f, zMax - zMin - 0.4f), mDarkMetal);
        Material hz1 = Glow(new Color(0.85f, 0.65f, 0.05f)), hz2 = Lit(new Color(0.03f, 0.03f, 0.03f));
        for (int i = 0; i < 12; i++)
            Box("EdgeStripe", staticRoot, new Vector3(shaftL - 0.08f, 0.05f, zMin + 0.4f + i * 0.35f + 0.175f), new Vector3(0.12f, 0.02f, 0.35f), (i & 1) == 0 ? hz1 : hz2);
        Sign("ShaftBottomSign", staticRoot, new Vector3(shaftL - 0.25f, 2.95f, XcutZ), Quaternion.Euler(0f, 90f, 0f), new Vector2(2.6f, 0.5f),
             new Color(0.0f, 0.45f, 0.2f), Color.white, "SHAFT BOTTOM  -  MAN RIDING CAGE", 1.35f, true, Color.white);
        Box("SignalBox", staticRoot, new Vector3(shaftL - 0.6f, 1.4f, zMax - 0.35f), new Vector3(0.4f, 0.5f, 0.2f), mYellow);
        Box("SignalButton", staticRoot, new Vector3(shaftL - 0.6f, 1.45f, zMax - 0.46f), new Vector3(0.08f, 0.08f, 0.03f), Glow(new Color(0.9f, 0.1f, 0.1f)));
        PointLight("ShaftBottomLight", envRoot, new Vector3(shaftL - 0.8f, 3.0f, XcutZ), new Color(0.75f, 1f, 0.8f), 8f, 1.6f);

        // The cage itself: deck, corner posts, lattice sides, roof, crosshead, drop gate, rope.
        cage = new GameObject("RescueCage").transform;
        cage.SetParent(envRoot, false);
        cage.position = new Vector3(ShaftX, 0f, XcutZ);
        Vector3 c = cage.position;
        Material cageMat = Lit(new Color(0.55f, 0.58f, 0.6f), null, 1f, 0.5f, 0.8f);
        Material cageDeck = Lit(new Color(0.22f, 0.23f, 0.24f), texRust, 2f, 0.3f, 0.6f);
        const float hx = 1.15f, hz = 1.35f, h = 2.6f;

        Box("CageDeck", cage, c + new Vector3(0f, 0.06f, 0f), new Vector3(hx * 2f, 0.12f, hz * 2f), cageDeck);
        Box("CageRoof", cage, c + new Vector3(0f, h, 0f), new Vector3(hx * 2f, 0.08f, hz * 2f), cageDeck);
        Box("CageCrosshead", cage, c + new Vector3(0f, h + 0.3f, 0f), new Vector3(0.3f, 0.5f, hz * 2f), cageMat);
        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
                Box("CagePost", cage, c + new Vector3(sx * hx, h * 0.5f, sz * hz), new Vector3(0.08f, h, 0.08f), cageMat);
        for (float z = -hz + 0.2f; z < hz; z += 0.22f)
            Box("BackBar", cage, c + new Vector3(hx, h * 0.5f, z), new Vector3(0.025f, h, 0.025f), cageMat);
        for (float x = -hx + 0.2f; x < hx; x += 0.22f)
        {
            Box("SideBarN", cage, c + new Vector3(x, h * 0.5f, hz), new Vector3(0.025f, h, 0.025f), cageMat);
            Box("SideBarS", cage, c + new Vector3(x, h * 0.5f, -hz), new Vector3(0.025f, h, 0.025f), cageMat);
        }
        foreach (float y in new[] { 0.9f, 1.8f })
        {
            Box("RailN", cage, c + new Vector3(0f, y, hz), new Vector3(hx * 2f, 0.04f, 0.04f), cageMat);
            Box("RailS", cage, c + new Vector3(0f, y, -hz), new Vector3(hx * 2f, 0.04f, 0.04f), cageMat);
            Box("RailBack", cage, c + new Vector3(hx, y, 0f), new Vector3(0.04f, 0.04f, hz * 2f), cageMat);
        }
        Box("HandGrip", cage, c + new Vector3(hx - 0.1f, 1.45f, 0f), new Vector3(0.05f, 0.05f, hz * 1.6f), mYellow);
        Sign("CagePlate", cage, c + new Vector3(hx - 0.04f, 2.2f, 0f), Quaternion.Euler(0f, 90f, 0f), new Vector2(1.3f, 0.35f),
             new Color(0.9f, 0.7f, 0.05f), Color.black, "MAX 12 PERSONS  -  HOLD GRIP", 0.95f);
        Rod("HoistRope", cage, c + new Vector3(0f, h + 0.55f, 0f), c + new Vector3(0f, 44f, 0f), 0.06f, mDarkMetal);

        // Drop gate: lattice raised above the opening, slams down when the miner boards.
        cageGate = new GameObject("CageGate").transform;
        cageGate.SetParent(cage, false);
        cageGate.position = c + new Vector3(-hx, GateRaisedY, 0f);
        Vector3 g = cageGate.position;
        for (float z = -hz + 0.12f; z < hz; z += 0.2f)
            Box("GateBar", cageGate, g + new Vector3(0f, 0f, z), new Vector3(0.025f, 2.1f, 0.025f), mYellow, Quaternion.Euler(18f, 0f, 0f));
        for (float z = -hz + 0.12f; z < hz; z += 0.2f)
            Box("GateBarX", cageGate, g + new Vector3(0f, 0f, z), new Vector3(0.025f, 2.1f, 0.025f), mYellow, Quaternion.Euler(-18f, 0f, 0f));
        Box("GateTop", cageGate, g + new Vector3(0f, 1.05f, 0f), new Vector3(0.06f, 0.06f, hz * 2f), mYellow);
        Box("GateBottom", cageGate, g + new Vector3(0f, -1.05f, 0f), new Vector3(0.06f, 0.06f, hz * 2f), mYellow);

        cageLight = PointLight("CageLamp", cage, c + new Vector3(0f, h - 0.15f, 0f), new Color(1f, 0.95f, 0.85f), 5f, 1.2f);
        Box("CageLampLens", cage, c + new Vector3(0f, h - 0.06f, 0f), new Vector3(0.3f, 0.03f, 0.2f), Glow(new Color(1f, 0.95f, 0.8f)));
    }

    // =====================================================================================
    //  PARTICLES
    // =====================================================================================
    Material GetParticleMaterial()
    {
        if (particleMat != null) return particleMat;
        // Sprites/Default renders in URP's unlit pass, supports vertex colour + alpha and is never stripped.
        Shader s = FindShader("Sprites/Default", "Universal Render Pipeline/Particles/Unlit", "Universal Render Pipeline/Unlit");
        particleMat = new Material(s);
        particleMat.mainTexture = sprSoft.texture;
        if (particleMat.HasProperty("_BaseMap")) particleMat.SetTexture("_BaseMap", sprSoft.texture);
        return particleMat;
    }

    ParticleSystem Particles(string name, Transform parent, Vector3 pos, Color start, Color end, float life, float speed,
                             Vector2 size, float rate, float gravity, ParticleSystemShapeType shapeType, Vector3 shapeScale,
                             int max, bool loop, float growth)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.rotation = shapeType == ParticleSystemShapeType.Box ? Quaternion.identity : Quaternion.Euler(-90f, 0f, 0f);

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.playOnAwake = false;
        main.loop = loop;
        main.duration = loop ? 2f : 1f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.6f, life);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.4f, speed);
        main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = start;
        main.gravityModifier = gravity;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = max;

        var em = ps.emission;
        em.rateOverTime = rate;

        var shape = ps.shape;
        shape.shapeType = shapeType;
        if (shapeType == ParticleSystemShapeType.Box)
        {
            shape.scale = shapeScale;
            shape.randomDirectionAmount = 1f;
        }
        else
        {
            shape.angle = 25f;
            shape.radius = 0.1f;
        }

        var col = ps.colorOverLifetime;
        col.enabled = true;
        Gradient grad = new Gradient();
        grad.SetKeys(new[] { new GradientColorKey(start, 0f), new GradientColorKey(end, 1f) },
                     new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(start.a, 0.12f), new GradientAlphaKey(0f, 1f) });
        col.color = grad;

        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, growth));

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.25f;
        noise.frequency = 0.4f;

        ParticleSystemRenderer psr = ps.GetComponent<ParticleSystemRenderer>();
        psr.sharedMaterial = GetParticleMaterial();
        psr.sortMode = ParticleSystemSortMode.Distance;
        return ps;
    }

    // =====================================================================================
    //  UI TOOLKIT (procedural uGUI + TextMeshPro)
    // =====================================================================================
    RectTransform UIRect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    static void Stretch(RectTransform rt, float inset = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    Image UIImage(RectTransform rt, Sprite sprite, Color color, bool raycast = false)
    {
        Image img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = raycast;
        if (sprite != null && sprite.border.sqrMagnitude > 0f) img.type = Image.Type.Sliced;
        return img;
    }

    // Rounded glass panel with a hairline border.
    RectTransform UIPanel(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, Color bg, bool raycast = false)
    {
        RectTransform rt = UIRect(name, parent, anchor, pivot, pos, size);
        UIImage(rt, sprRound, bg, raycast);
        RectTransform line = UIRect("Border", rt, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(line);
        UIImage(line, sprRoundLine, UILine);
        return rt;
    }

    TextMeshProUGUI UIText(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, string text,
                           float fontSize, Color color, TextAlignmentOptions align, FontStyles style = FontStyles.Normal)
    {
        RectTransform rt = UIRect(name, parent, anchor, pivot, pos, size);
        TextMeshProUGUI t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = fontSize;
        t.color = color;
        t.alignment = align;
        t.fontStyle = style;
        t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.Normal;
        t.overflowMode = TextOverflowModes.Overflow;
        t.richText = true;
        return t;
    }

    Button UIButton(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, string label, Color bg, float fontSize,
                    out TextMeshProUGUI labelText)
    {
        RectTransform rt = UIRect(name, parent, anchor, new Vector2(0.5f, 0.5f), pos, size);
        Image img = UIImage(rt, sprRound, Color.white, true);
        Button b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        ColorBlock cb = b.colors;
        cb.normalColor = bg;
        cb.highlightedColor = Color.Lerp(bg, Color.white, 0.12f);
        cb.pressedColor = Color.Lerp(bg, Color.black, 0.25f);
        cb.selectedColor = bg;
        cb.disabledColor = new Color(bg.r, bg.g, bg.b, 0.35f);
        cb.fadeDuration = 0.06f;
        b.colors = cb;
        Navigation nav = b.navigation;
        nav.mode = Navigation.Mode.None;
        b.navigation = nav;

        RectTransform line = UIRect("Border", rt, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(line);
        UIImage(line, sprRoundLine, new Color(1f, 1f, 1f, 0.16f));

        labelText = UIText("Label", rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size - new Vector2(36f, 12f),
                           label, fontSize, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
        labelText.enableAutoSizing = true;
        // Tight range: shrink enough to fit a narrow button, never to unreadable.
        labelText.fontSizeMin = Mathf.Max(16f, fontSize * 0.78f);
        labelText.fontSizeMax = fontSize;
        return b;
    }

    // Small coloured accent bar used on the left edge of HUD plates.
    void Accent(Transform parent, Color c, float width = 6f)
    {
        RectTransform a = UIRect("Accent", parent, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(10f, 0f), Vector2.zero);
        a.anchorMin = new Vector2(0f, 0f);
        a.anchorMax = new Vector2(0f, 1f);
        a.offsetMin = new Vector2(10f, 16f);
        a.offsetMax = new Vector2(10f + width, -16f);
        UIImage(a, sprRound, c);
    }

    // =====================================================================================
    //  HUD
    // =====================================================================================
    void BuildHUD()
    {
        GameObject canvasObj = new GameObject("SimulationHUD");
        canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        bool isPortrait = Screen.width < Screen.height;
        scaler.referenceResolution = isPortrait ? new Vector2(1080f, 1920f) : new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = isPortrait ? 0.5f : 1f;
        canvasObj.AddComponent<GraphicRaycaster>();
        Transform c = canvasObj.transform;
        Vector2 mid = new Vector2(0.5f, 0.5f);

        // Full-screen effect layers (outside the safe area so they cover the cutout too).
        RectTransform vig = UIRect("HypoxiaVignette", c, mid, mid, Vector2.zero, Vector2.zero);
        Stretch(vig, -40f);
        vignette = UIImage(vig, sprVignette, new Color(0f, 0f, 0f, 0.55f));
        RectTransform dim = UIRect("HypoxiaDim", c, mid, mid, Vector2.zero, Vector2.zero);
        Stretch(dim, -40f);
        hypoxiaDim = UIImage(dim, null, new Color(0.02f, 0f, 0f, 0f));
        RectTransform flash = UIRect("AlarmFlash", c, mid, mid, Vector2.zero, Vector2.zero);
        Stretch(flash, -40f);
        alarmFlash = UIImage(flash, sprVignette, new Color(0.9f, 0.05f, 0.02f, 0f));

        // Safe-area root: respects camera cut-outs and gesture bars (Galaxy S24 FE etc.).
        safeRoot = UIRect("SafeArea", c, mid, mid, Vector2.zero, Vector2.zero);
        Stretch(safeRoot);
        Transform s = safeRoot;

        // --- Row 1 (Top, Y = -20): Status plate on Top-Left (680 x 114) & Timer plate on Top-Right (280 x 114)
        RectTransform status = UIPanel("StatusPlate", s, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -20f), new Vector2(680f, 114f), UIPanelBg);
        Accent(status, UIAmber);
        UIText("Org", status, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -10f), new Vector2(630f, 28f),
               "COALMINEVR  //  DGMS EVACUATION DRILL", 21f, UIMuted, TextAlignmentOptions.Left, FontStyles.Bold);
        phaseText = UIText("Phase", status, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -38f), new Vector2(630f, 44f),
                           PhaseNames[0], 34f, UIAmber, TextAlignmentOptions.Left, FontStyles.Bold);
        phaseText.textWrappingMode = TextWrappingModes.NoWrap;
        phaseText.enableAutoSizing = true;
        phaseText.fontSizeMin = 27f;
        phaseText.fontSizeMax = 34f;
        phaseText.overflowMode = TextOverflowModes.Ellipsis;
        UIText("Trainee", status, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -82f), new Vector2(630f, 26f),
               "TRAINEE  " + traineeId + "   |   SEAM II  -  INTAKE DRIFT 3", 21f, UIMuted, TextAlignmentOptions.Left);

        RectTransform timer = UIPanel("TimerPlate", s, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-20f, -20f), new Vector2(280f, 114f), UIPanelBg);
        UIText("TimerLabel", timer, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(240f, 26f),
               "ELAPSED", 20f, UIMuted, TextAlignmentOptions.Center, FontStyles.Bold);
        timerText = UIText("Timer", timer, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(240f, 68f),
                           "00:00", 50f, UITextCol, TextAlignmentOptions.Center, FontStyles.Bold);

        // --- Row 2 (Below Row 1, Y = -146): Objective strip spanning full safe width (980 x 86) centered
        RectTransform obj = UIPanel("ObjectiveStrip", s, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -146f), new Vector2(980f, 86f), UIPanelBg);
        RectTransform chip = UIRect("ObjChip", obj, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(14f, 0f), new Vector2(160f, 48f));
        UIImage(chip, sprRound, UIAmber);
        UIText("ObjChipText", chip, mid, mid, Vector2.zero, new Vector2(150f, 48f), "OBJECTIVE", 20f, new Color(0.08f, 0.06f, 0.02f), TextAlignmentOptions.Center, FontStyles.Bold);
        objectiveText = UIText("Objective", obj, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(188f, 0f), new Vector2(774f, 76f),
                               "", 27f, UITextCol, TextAlignmentOptions.Left);
        objectiveText.enableAutoSizing = true;
        objectiveText.fontSizeMin = 23f;
        objectiveText.fontSizeMax = 29f;

        // --- Row 3 (Y = -246): GasDetector compact panel on Top-Left (290 x 200) & OxygenGroup on Top-Right (650 x 104)
        RectTransform det = UIPanel("GasDetector", s, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -246f), new Vector2(290f, 200f), UIPanelBg);
        RectTransform dh = UIRect("DetHeader", det, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -6f), new Vector2(274f, 38f));
        detHeader = UIImage(dh, sprRound, new Color(1f, 1f, 1f, 0.06f));
        UIText("DetTitle", dh, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12f, 0f), new Vector2(150f, 30f),
               "MULTI-GAS", 20f, UIMuted, TextAlignmentOptions.Left, FontStyles.Bold);
        detStatusText = UIText("DetStatus", dh, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-12f, 0f), new Vector2(110f, 30f),
                               "NORMAL", 22f, UIGreen, TextAlignmentOptions.Right, FontStyles.Bold);
        ch4Text = DetectorRow(det, "CH4", -54f, out _);
        coText = DetectorRow(det, "CO", -102f, out _);
        o2DetText = DetectorRow(det, "O2", -150f, out _);

        o2Group = UIPanel("OxygenGroup", s, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-20f, -246f), new Vector2(650f, 104f), UIPanelBg);
        o2Label = UIText("O2Label", o2Group, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -12f), new Vector2(400f, 32f),
               "BREATHABLE AIR  -  HYPOXIA COUNTDOWN", 23f, new Color(1f, 0.55f, 0.5f), TextAlignmentOptions.Left, FontStyles.Bold);
        o2Text = UIText("O2Value", o2Group, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-22f, -8f), new Vector2(200f, 42f),
                        "25.0 s", 34f, UITextCol, TextAlignmentOptions.Right, FontStyles.Bold);
        RectTransform frame = UIRect("O2Frame", o2Group, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 14f), new Vector2(610f, 26f));
        o2Frame = UIImage(frame, sprRound, new Color(1f, 1f, 1f, 0.08f));
        RectTransform fill = UIRect("O2Fill", frame, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = Vector2.one;
        fill.offsetMin = new Vector2(3f, 3f);
        fill.offsetMax = new Vector2(-3f, -3f);
        o2Fill = UIImage(fill, sprRound, UIRed);
        o2Group.gameObject.SetActive(false);

        // --- Reticle + focus label (centre)
        reticleRT = UIRect("Reticle", s, mid, mid, Vector2.zero, new Vector2(34f, 34f));
        reticleRing = UIImage(reticleRT, sprRing, new Color(1f, 1f, 1f, 0.35f));
        RectTransform dot = UIRect("Dot", reticleRT, mid, mid, Vector2.zero, new Vector2(7f, 7f));
        reticleDot = UIImage(dot, sprCircle, new Color(1f, 1f, 1f, 0.8f));
        focusLabel = UIText("FocusLabel", s, mid, new Vector2(0.5f, 1f), new Vector2(0f, -44f), new Vector2(840f, 96f), "",
                            31f, UITextCol, TextAlignmentOptions.Center, FontStyles.Bold);

        // --- Context action button (bottom-right, thumb reach)
        actionButton = UIButton("ActionButton", s, new Vector2(1f, 0f), new Vector2(-210f, 150f), new Vector2(320f, 112f), "INSPECT",
                                new Color(0.86f, 0.58f, 0.04f, 0.95f), 29f, out actionLabel);
        actionLabel.color = new Color(0.08f, 0.06f, 0.02f);
        actionButton.onClick.AddListener(() => { if (focused != null) UseInteractable(focused); });
        actionButton.gameObject.SetActive(false);

        // --- WEAR MASK button: shown during the gas leak so the miner can don the self-rescuer
        //     from anywhere (belt-worn unit). Built halo-first so the glow renders BEHIND the
        //     button, then both are pulsed from UpdateCriticalPulse().
        RectTransform haloRT = UIRect("WearMaskHalo", s, new Vector2(1f, 0f), new Vector2(0.5f, 0.5f),
                                      new Vector2(-210f, 290f), new Vector2(392f, 176f));
        maskHalo = UIImage(haloRT, sprRound, new Color(0.22f, 0.95f, 0.48f, 0f));
        haloRT.gameObject.SetActive(false);

        maskButton = UIButton("WearMaskButton", s, new Vector2(1f, 0f), new Vector2(-210f, 290f), new Vector2(320f, 112f), "WEAR MASK",
                              maskBtnBase, 30f, out maskLabel);
        maskLabel.color = Color.white;
        maskBtnImg = maskButton.targetGraphic as Image;
        // UpdateCriticalPulse() owns this button's tint, so disable the built-in colour
        // transition: the two writing to targetGraphic.color would fight each other.
        maskButton.transition = Selectable.Transition.None;
        maskButton.onClick.AddListener(() => { if (phase == Phase.ToxicGas && gasArmed && !maskOn && !decisionOpen && !debriefOpen) OnSelfRescuer(); });
        maskButton.gameObject.SetActive(false);

        // --- Floating joystick (bottom-left), drawn on a bottom-left pivot layer.
        RectTransform joyLayer = UIRect("JoystickLayer", s, mid, Vector2.zero, Vector2.zero, Vector2.zero);
        Stretch(joyLayer);
        joyLayer.pivot = Vector2.zero;
        joyLayerRT = joyLayer;
        joyBaseRT = UIRect("JoyBase", joyLayer, Vector2.zero, mid, joyRestPos, new Vector2(joystickRadius * 2.2f, joystickRadius * 2.2f));
        joyBase = UIImage(joyBaseRT, sprRing, new Color(1f, 1f, 1f, 0.22f));
        RectTransform joyFill = UIRect("JoyBaseFill", joyBaseRT, mid, mid, Vector2.zero, new Vector2(joystickRadius * 2.1f, joystickRadius * 2.1f));
        UIImage(joyFill, sprCircle, new Color(0f, 0f, 0f, 0.22f));
        joyKnobRT = UIRect("JoyKnob", joyLayer, Vector2.zero, mid, joyRestPos, new Vector2(104f, 104f));
        joyKnob = UIImage(joyKnobRT, sprCircle, new Color(1f, 1f, 1f, 0.35f));

        // --- Toast + control hints (bottom-centre)
        toastRT = UIPanel("Toast", s, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 300f), new Vector2(960f, 88f), new Color(0.05f, 0.05f, 0.06f, 0.88f));
        toastBg = toastRT.GetComponent<Image>();
        toastText = UIText("ToastText", toastRT, mid, mid, Vector2.zero, new Vector2(920f, 80f), "", 31f, UITextCol, TextAlignmentOptions.Center, FontStyles.Bold);
        toastRT.gameObject.SetActive(false);

        hintText = UIText("Hints", s, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 30f), new Vector2(1300f, 54f),
            "LEFT THUMB: WALK   |   RIGHT THUMB: LOOK  -  TAP TO INTERACT   |   PC: WASD + MOUSE, F TO INTERACT",
            21f, new Color(1f, 1f, 1f, 0.6f), TextAlignmentOptions.Center, FontStyles.Bold);

        // Fade layer for scene transitions (on top of everything but non-blocking).
        RectTransform fade = UIRect("Fade", c, mid, mid, Vector2.zero, Vector2.zero);
        Stretch(fade, -40f);
        fadeImg = UIImage(fade, null, new Color(0f, 0f, 0f, 0f));
        fadeImg.transform.SetAsLastSibling();

        UpdateCanvasLayout(true);
    }

    TextMeshProUGUI DetectorRow(Transform det, string gas, float y, out TextMeshProUGUI label)
    {
        label = UIText(gas + "Label", det, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, y), new Vector2(100f, 36f),
                       gas, 26f, UIMuted, TextAlignmentOptions.Left, FontStyles.Bold);
        RectTransform sep = UIRect(gas + "Sep", det, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y - 38f), new Vector2(262f, 2f));
        UIImage(sep, null, new Color(1f, 1f, 1f, 0.05f));
        return UIText(gas + "Value", det, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-14f, y), new Vector2(150f, 36f),
                      "--", 30f, UITextCol, TextAlignmentOptions.Right, FontStyles.Bold);
    }

    void UpdateCanvasLayout(bool force = false)
    {
        if (safeRoot == null || scaler == null) return;
        Rect sa = Screen.safeArea;
        Vector2Int scr = new Vector2Int(Screen.width, Screen.height);
        if (!force && sa == lastSafeArea && scr == lastScreen) return;
        lastSafeArea = sa;
        lastScreen = scr;
        if (scr.x <= 0 || scr.y <= 0) return;

        safeRoot.anchorMin = new Vector2(sa.xMin / scr.x, sa.yMin / scr.y);
        safeRoot.anchorMax = new Vector2(sa.xMax / scr.x, sa.yMax / scr.y);
        safeRoot.offsetMin = Vector2.zero;
        safeRoot.offsetMax = Vector2.zero;

        bool isPortrait = scr.x < scr.y;
        if (isPortrait)
        {
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;
        }
        else
        {
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
        }
    }

    void SetPhase(Phase p)
    {
        phase = p;
        if (phaseText != null) phaseText.text = PhaseNames[(int)p];
    }

    void SetObjective(string text)
    {
        if (objectiveText != null) objectiveText.text = text;
    }

    void ShowToast(string message, float seconds = 2.6f, Color? accent = null)
    {
        if (toastText == null) return;
        toastText.text = message;
        toastText.color = accent ?? UITextCol;
        toastTimer = seconds;
        toastRT.gameObject.SetActive(true);
    }

    static string FormatTime(float seconds)
    {
        int s = Mathf.Max(0, Mathf.FloorToInt(seconds));
        return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
    }

    // =====================================================================================
    //  TACTICAL DECISION CARD
    // =====================================================================================
    void HazardStripe(Transform parent, float inset)
    {
        RectTransform st = UIRect("HazardStripe", parent, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);
        st.anchorMin = new Vector2(0f, 1f);
        st.anchorMax = new Vector2(1f, 1f);
        st.offsetMin = new Vector2(inset, -14f);
        st.offsetMax = new Vector2(-inset, 0f);
        Image img = UIImage(st, sprStripes, UIAmber);
        img.type = Image.Type.Tiled;
        img.pixelsPerUnitMultiplier = 2f;
    }

    Button OptionButton(string name, Transform parent, float y, string letter, out TextMeshProUGUI label)
    {
        Button b = UIButton(name, parent, new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(900f, 124f), "",
                            new Color(0.13f, 0.15f, 0.18f, 1f), 29f, out label);
        RectTransform lrt = label.rectTransform;
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = new Vector2(116f, 6f);
        lrt.offsetMax = new Vector2(-20f, -6f);
        label.alignment = TextAlignmentOptions.Left;
        label.fontStyle = FontStyles.Normal;
        label.enableAutoSizing = true;
        label.fontSizeMin = 24f;
        label.fontSizeMax = 29f;

        RectTransform badge = UIRect("Badge", b.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(24f, 0f), new Vector2(72f, 72f));
        UIImage(badge, sprRound, UIAmber);
        UIText("Letter", badge, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(72f, 72f), letter,
               32f, new Color(0.08f, 0.06f, 0.02f), TextAlignmentOptions.Center, FontStyles.Bold);
        return b;
    }

    void BuildDecisionCard()
    {
        Vector2 mid = new Vector2(0.5f, 0.5f), top = new Vector2(0.5f, 1f);
        RectTransform backdrop = UIRect("DecisionCard", canvas.transform, mid, mid, Vector2.zero, Vector2.zero);
        Stretch(backdrop, -40f);
        UIImage(backdrop, null, new Color(0f, 0f, 0f, 0.55f), true);
        cardRoot = backdrop.gameObject;

        RectTransform panel = UIPanel("CardPanel", backdrop, mid, mid, new Vector2(0f, 0f), new Vector2(980f, 860f), UIPanelSolid, true);
        HazardStripe(panel, 18f);

        cardStep = UIText("Step", panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -26f), new Vector2(680f, 36f),
                          "", 24f, UIAmber, TextAlignmentOptions.Left, FontStyles.Bold);
        cardTimerText = UIText("TimerText", panel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -22f), new Vector2(200f, 42f),
                               "", 30f, UITextCol, TextAlignmentOptions.Right, FontStyles.Bold);
        cardTitle = UIText("Title", panel, top, top, new Vector2(0f, -70f), new Vector2(900f, 80f), "", 44f, UITextCol, TextAlignmentOptions.Left, FontStyles.Bold);
        cardTitle.enableAutoSizing = true;
        cardTitle.fontSizeMin = 34f;
        cardTitle.fontSizeMax = 44f;

        // Question view
        cardQuestionGroup = UIRect("QuestionGroup", panel, mid, mid, Vector2.zero, Vector2.zero).gameObject;
        Stretch((RectTransform)cardQuestionGroup.transform);
        Transform q = cardQuestionGroup.transform;
        cardBody = UIText("Scenario", q, top, top, new Vector2(0f, -156f), new Vector2(900f, 240f), "", 30f, new Color(0.82f, 0.84f, 0.87f), TextAlignmentOptions.TopLeft);
        cardBody.enableAutoSizing = true;
        cardBody.fontSizeMin = 25f;
        cardBody.fontSizeMax = 30f;
        cardBody.lineSpacing = 6f;
        RectTransform tf = UIRect("TimerFrame", q, top, top, new Vector2(0f, -404f), new Vector2(900f, 12f));
        UIImage(tf, sprRound, new Color(1f, 1f, 1f, 0.07f));
        RectTransform tfill = UIRect("TimerFill", tf, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
        Stretch(tfill);
        cardTimerFill = UIImage(tfill, sprRound, UIAmber);
        optA = OptionButton("OptionA", q, -490f, "A", out optAText);
        optB = OptionButton("OptionB", q, -630f, "B", out optBText);

        // Feedback view
        cardFeedbackGroup = UIRect("FeedbackGroup", panel, mid, mid, Vector2.zero, Vector2.zero).gameObject;
        Stretch((RectTransform)cardFeedbackGroup.transform);
        Transform f = cardFeedbackGroup.transform;
        RectTransform banner = UIRect("ResultBanner", f, top, top, new Vector2(0f, -152f), new Vector2(900f, 84f));
        cardResultBanner = UIImage(banner, sprRound, UIGreen);
        cardResultText = UIText("ResultText", banner, mid, mid, Vector2.zero, new Vector2(860f, 76f), "", 30f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold);
        cardExplainText = UIText("Explain", f, top, top, new Vector2(0f, -256f), new Vector2(900f, 276f), "", 28f, new Color(0.85f, 0.87f, 0.9f), TextAlignmentOptions.TopLeft);
        cardExplainText.enableAutoSizing = true;
        cardExplainText.fontSizeMin = 24f;
        cardExplainText.fontSizeMax = 28f;
        cardExplainText.lineSpacing = 6f;
        RectTransform refBox = UIPanel("RefBox", f, top, top, new Vector2(0f, -546f), new Vector2(900f, 80f), new Color(1f, 1f, 1f, 0.04f));
        cardRefText = UIText("Ref", refBox, mid, mid, Vector2.zero, new Vector2(860f, 74f), "", 21f, UIMuted, TextAlignmentOptions.Left, FontStyles.Italic);
        cardContinue = UIButton("Continue", f, top, new Vector2(0f, -690f), new Vector2(520f, 104f), "CONTINUE", new Color(0.12f, 0.45f, 0.28f, 1f), 29f, out _);

        cardRoot.SetActive(false);
    }

    // =====================================================================================
    //  DGMS DEBRIEF CERTIFICATE
    // =====================================================================================
    void BuildDebrief()
    {
        Vector2 mid = new Vector2(0.5f, 0.5f), tl = new Vector2(0f, 1f);
        RectTransform backdrop = UIRect("DebriefCertificate", canvas.transform, mid, mid, Vector2.zero, Vector2.zero);
        Stretch(backdrop, -40f);
        UIImage(backdrop, null, new Color(0.015f, 0.016f, 0.02f, 0.94f), true);
        debriefRoot = backdrop.gameObject;

        RectTransform panel = UIPanel("CertPanel", backdrop, mid, mid, Vector2.zero, new Vector2(1000f, 960f), UIPanelSolid, true);
        HazardStripe(panel, 18f);

        UIText("Org", panel, tl, tl, new Vector2(40f, -36f), new Vector2(620f, 28f),
               "DIRECTORATE GENERAL OF MINES SAFETY  -  EMERGENCY EVACUATION DRILL", 20f, UIMuted, TextAlignmentOptions.Left, FontStyles.Bold);
        UIText("Title", panel, tl, tl, new Vector2(40f, -64f), new Vector2(620f, 80f),
               "PERFORMANCE & EVACUATION CERTIFICATE", 36f, UITextCol, TextAlignmentOptions.Left, FontStyles.Bold);
        dbMeta = UIText("Meta", panel, tl, tl, new Vector2(40f, -150f), new Vector2(620f, 30f), "", 21f, UIMuted, TextAlignmentOptions.Left);

        RectTransform stamp = UIRect("Stamp", panel, new Vector2(1f, 1f), mid, new Vector2(-160f, -94f), new Vector2(300f, 114f));
        stamp.localRotation = Quaternion.Euler(0f, 0f, 5f);
        dbStampFrame = UIImage(stamp, sprRoundLine, UIGreen);
        RectTransform stampInner = UIRect("StampInner", stamp, mid, mid, Vector2.zero, new Vector2(284f, 98f));
        UIImage(stampInner, sprRoundLine, new Color(1f, 1f, 1f, 0.25f));
        dbStamp = UIText("StampText", stamp, mid, mid, Vector2.zero, new Vector2(280f, 96f), "", 44f, UIGreen, TextAlignmentOptions.Center, FontStyles.Bold);
        dbStamp.enableAutoSizing = true;
        dbStamp.fontSizeMin = 26f;
        dbStamp.fontSizeMax = 44f;

        string[] tileNames = { "ESCAPE TIME", "DECISIONS", "SAFETY SCORE", "O2 RESERVE" };
        TextMeshProUGUI[] tileVals = new TextMeshProUGUI[4];
        float tileW = 220f, tileGap = 13.3f, startX = 40f;
        for (int i = 0; i < 4; i++)
        {
            RectTransform tile = UIPanel("Tile" + i, panel, tl, tl, new Vector2(startX + i * (tileW + tileGap), -182f), new Vector2(tileW, 116f), new Color(1f, 1f, 1f, 0.04f));
            UIText("TileLabel", tile, tl, tl, new Vector2(14f, -12f), new Vector2(192f, 26f), tileNames[i], 19f, UIMuted, TextAlignmentOptions.Left, FontStyles.Bold);
            tileVals[i] = UIText("TileValue", tile, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(14f, 10f), new Vector2(192f, 66f), "--",
                                 50f, UITextCol, TextAlignmentOptions.Left, FontStyles.Bold);
        }
        dbTime = tileVals[0]; dbDecisions = tileVals[1]; dbScore = tileVals[2]; dbO2 = tileVals[3];

        UIText("TableHeader", panel, tl, tl, new Vector2(40f, -314f), new Vector2(800f, 32f), "COMPETENCY ASSESSMENT", 21f, UIMuted, TextAlignmentOptions.Left, FontStyles.Bold);
        for (int i = 0; i < TotalDecisions; i++)
        {
            RectTransform row = UIPanel("Row" + i, panel, tl, tl, new Vector2(40f, -352f - i * 82f), new Vector2(920f, 74f), new Color(1f, 1f, 1f, 0.03f));
            RectTransform chipRt = UIRect("Chip", row, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(14f, 0f), new Vector2(8f, 46f));
            dbRowChip[i] = UIImage(chipRt, sprRound, UIMuted);
            UIText("Code", row, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(34f, 0f), new Vector2(50f, 44f), comps[i].code, 21f, UIMuted, TextAlignmentOptions.Left, FontStyles.Bold);
            UIText("Title", row, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(90f, 0f), new Vector2(296f, 56f), comps[i].title, 24f, UITextCol, TextAlignmentOptions.Left, FontStyles.Bold);
            dbRowNote[i] = UIText("Note", row, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(398f, 0f), new Vector2(338f, 64f), "", 22f, UIMuted, TextAlignmentOptions.Left);
            dbRowNote[i].enableAutoSizing = true;
            dbRowNote[i].fontSizeMin = 19f;
            dbRowNote[i].fontSizeMax = 22f;
            dbRowStatus[i] = UIText("Status", row, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-16f, 0f), new Vector2(156f, 44f), "", 22f, UIMuted, TextAlignmentOptions.Right, FontStyles.Bold);
        }

        dbOutcome = UIText("Outcome", panel, tl, tl, new Vector2(40f, -694f), new Vector2(920f, 70f), "", 23f, UITextCol, TextAlignmentOptions.Left);
        dbOutcome.enableAutoSizing = true;
        dbOutcome.fontSizeMin = 20f;
        dbOutcome.fontSizeMax = 23f;
        dbSync = UIText("Sync", panel, tl, tl, new Vector2(40f, -770f), new Vector2(920f, 30f), "", 20f, UIMuted, TextAlignmentOptions.Left, FontStyles.Italic);

        Button retry = UIButton("RetryBtn", panel, new Vector2(0.5f, 1f), new Vector2(-235f, -846f), new Vector2(440f, 92f), "RETRY DRILL",
                                new Color(0.2f, 0.21f, 0.24f, 1f), 27f, out _);
        retry.onClick.AddListener(() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex));
        Button ret = UIButton("ReturnBtn", panel, new Vector2(0.5f, 1f), new Vector2(235f, -846f), new Vector2(440f, 92f), "SAVE & RETURN TO DISPATCH",
                              new Color(0.1f, 0.38f, 0.7f, 1f), 27f, out _);
        ret.onClick.AddListener(SaveAndReturn);

        debriefRoot.SetActive(false);
    }

    // =====================================================================================
    //  INPUT (New Input System: Touchscreen multi-touch, Pointer, Keyboard, Gyroscope)
    // =====================================================================================
    private bool moving;

    void HandleInput(float dt)
    {
        moveVec = Vector2.zero;

        // While the trainee is still scanning for the marker, ignore gameplay input.
        if (arMarkerMode && !arAnchored) return;

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.f12Key.wasPressedThisFrame) showDebugPanel = !showDebugPanel;
            float fwd = 0f, strafe = 0f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) fwd += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) fwd -= 1f;
            if (kb.dKey.isPressed) strafe += 1f;
            if (kb.aKey.isPressed) strafe -= 1f;
            if (!arRigBuilt)
            {
                if (kb.rightArrowKey.isPressed || kb.eKey.isPressed) yaw += 95f * dt;
                if (kb.leftArrowKey.isPressed || kb.qKey.isPressed) yaw -= 95f * dt;
            }
            moveVec += new Vector2(strafe, fwd);
            if ((kb.fKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame) && focused != null)
                UseInteractable(focused);
        }

        Touchscreen ts = Touchscreen.current;
        if (ts != null) HandleTouches(ts);

        // Mouse / pen through the generic Pointer device (touch is handled above with multi-touch).
        Pointer p = Pointer.current;
        if (p != null && !(p is Touchscreen)) HandlePointer(p);

        if (!arRigBuilt && useGyro && gyro != null && gyro.enabled) ApplyGyro(dt);

        moving = moveVec.sqrMagnitude > 0.0004f;
        if (moving)
        {
            moveVec = Vector2.ClampMagnitude(moveVec, 1f);
            float speed = (phase == Phase.ToxicGas && !maskOn) ? hypoxiaWalkSpeed : walkSpeed;
            Quaternion yr = Quaternion.Euler(0f, yaw, 0f);
            Vector3 delta = (yr * Vector3.forward * moveVec.y + yr * Vector3.right * moveVec.x) * speed * dt;

            if (arRigBuilt && arAnchored)
            {
                // Anchored: the world slides under the camera (physical walking already
                // moves the camera via TrackedPoseDriver); joystick pans the mine instead.
                arWorldDrift -= delta;
                // Keep the slide bounded so the trainee can't push the mine infinitely far.
                arWorldDrift = Vector3.ClampMagnitude(arWorldDrift, 60f);
                bobPhase += dt * speed * 3.4f * moveVec.magnitude;
            }
            else
            {
                basePos = ClampToWalkable(basePos + delta);
                bobPhase += dt * speed * 3.4f * moveVec.magnitude;
            }
        }
    }

    void HandleTouches(Touchscreen ts)
    {
        bool joySeen = false, lookSeen = false;
        float lookScale = touchLookSensitivity * (1080f / Mathf.Max(1, Screen.height));
        float tapSlop = 16f * Mathf.Max(1f, Screen.dpi / 160f);

        foreach (TouchControl t in ts.touches)
        {
            UnityEngine.InputSystem.TouchPhase ph = t.phase.ReadValue();
            if (ph == UnityEngine.InputSystem.TouchPhase.None) continue;
            int id = t.touchId.ReadValue();
            Vector2 pos = t.position.ReadValue();
            bool ended = ph == UnityEngine.InputSystem.TouchPhase.Ended || ph == UnityEngine.InputSystem.TouchPhase.Canceled;

            if (ph == UnityEngine.InputSystem.TouchPhase.Began && id != joyTouchId && id != lookTouchId)
            {
                if (IsOverUI(pos)) continue;
                if (pos.x < Screen.width * 0.55f && pos.y < Screen.height * 0.45f && joyTouchId < 0)
                {
                    joyTouchId = id;
                    joyOrigin = pos;
                    joyCurrent = pos;
                }
                else if (lookTouchId < 0)
                {
                    lookTouchId = id;
                    lookStart = lookLast = pos;
                    lookStartTime = Time.time;
                    lookDragged = false;
                }
            }

            if (id == joyTouchId)
            {
                joyCurrent = pos;
                if (ended) joyTouchId = -1; else joySeen = true;
            }
            else if (id == lookTouchId)
            {
                if (!ended)
                {
                    lookSeen = true;
                    if (!lookDragged && (pos - lookStart).magnitude > tapSlop)
                    {
                        lookDragged = true;
                        if (autoLevelRoutine != null) { StopCoroutine(autoLevelRoutine); autoLevelRoutine = null; }
                    }
                    if (lookDragged)
                    {
                        Vector2 d = pos - lookLast;
                        // Physical look is owned by the AR TrackedPoseDriver; only apply
                        // touch look in classic mode or while still scanning for the marker.
                        if (!arRigBuilt)
                        {
                            yaw += d.x * lookScale;
                            pitch -= d.y * lookScale * 0.65f;
                        }
                    }
                    lookLast = pos;
                }
                else
                {
                    if (!lookDragged && Time.time - lookStartTime < 0.4f) TryInteractAt(pos);
                    lookTouchId = -1;
                }
            }
        }
        if (!joySeen) joyTouchId = -1;
        if (!lookSeen && lookTouchId >= 0) lookTouchId = -1;

        if (joyTouchId >= 0)
        {
            float radiusPx = joystickRadius * (canvas != null ? canvas.scaleFactor : 1f);
            Vector2 v = Vector2.ClampMagnitude((joyCurrent - joyOrigin) / Mathf.Max(1f, radiusPx), 1f);
            if (v.magnitude < 0.12f) v = Vector2.zero;
            moveVec += v;
        }
    }

    void HandlePointer(Pointer p)
    {
        Vector2 pos = p.position.ReadValue();
        if (p.press.wasPressedThisFrame)
        {
            if (IsOverUI(pos)) return;
            mouseHeld = true;
            mouseDragged = false;
            mouseStart = mouseLast = pos;
            mouseStartTime = Time.time;
        }
        else if (mouseHeld && p.press.isPressed)
        {
            if (!mouseDragged && (pos - mouseStart).magnitude > 6f) mouseDragged = true;
            if (mouseDragged)
            {
                Vector2 d = pos - mouseLast;
                if (!arRigBuilt)
                {
                    yaw += d.x * mouseLookSensitivity;
                    pitch -= d.y * mouseLookSensitivity;
                }
            }
            mouseLast = pos;
        }
        if (mouseHeld && p.press.wasReleasedThisFrame)
        {
            mouseHeld = false;
            if (!mouseDragged && Time.time - mouseStartTime < 0.5f) TryInteractAt(pos);
        }
    }

    // Angular velocity is in device space; remap to screen axes for the current orientation.
    void ApplyGyro(float dt)
    {
        Vector3 r = gyro.angularVelocity.ReadValue();
        if (r.sqrMagnitude < 0.0009f) return;
        float k = Mathf.Rad2Deg * dt * gyroSensitivity;
        switch (Screen.orientation)
        {
            case ScreenOrientation.LandscapeLeft:
                yaw += -r.x * k; pitch += r.y * k; break;
            case ScreenOrientation.LandscapeRight:
                yaw += r.x * k; pitch += -r.y * k; break;
            case ScreenOrientation.PortraitUpsideDown:
                yaw += r.y * k; pitch += r.x * k; break;
            default:
                yaw += -r.y * k; pitch += -r.x * k; break;
        }
    }

    void ReleaseInput()
    {
        joyTouchId = -1;
        lookTouchId = -1;
        mouseHeld = false;
        moveVec = Vector2.zero;
        moving = false;
    }

    bool IsOverUI(Vector2 screenPos)
    {
        EventSystem es = EventSystem.current;
        if (es == null) return false;
        PointerEventData ped = new PointerEventData(es) { position = screenPos };
        uiHits.Clear();
        es.RaycastAll(ped, uiHits);
        return uiHits.Count > 0;
    }

    void HandleBackButton()
    {
        Keyboard kb = Keyboard.current;  // Android BACK is delivered as Escape by the Input System
        if (kb == null || !kb.escapeKey.wasPressedThisFrame) return;
        if (Time.unscaledTime - lastBackPress < 2f) ReturnToMenu();
        else
        {
            lastBackPress = Time.unscaledTime;
            ShowToast("Press BACK again to exit to Main Menu", 2f, UIAmber);
        }
    }

    Vector3 ClampToWalkable(Vector3 p)
    {
        if (cageEntered) return basePos;
        p.z = Mathf.Clamp(p.z, minZ, maxZ);
        float xcutNear = XcutZ - XcutHalfDepth + 0.65f, xcutFar = XcutZ + XcutHalfDepth - 0.7f;
        if (p.z < xcutNear) p.x = Mathf.Clamp(p.x, -WalkHalfWidth, WalkHalfWidth);
        else
        {
            p.x = Mathf.Clamp(p.x, XcutMinX + 1.4f, ShaftX + 0.7f);
            p.z = Mathf.Min(p.z, xcutFar);
        }
        if (Mathf.Abs(p.x) > WalkHalfWidth) p.z = Mathf.Clamp(p.z, xcutNear, xcutFar);
        return p;
    }

    private Coroutine autoLevelRoutine;
    void SmoothAutoLevelPitch(float duration = 0.5f)
    {
        if (autoLevelRoutine != null) StopCoroutine(autoLevelRoutine);
        autoLevelRoutine = StartCoroutine(AutoLevelPitchRoutine(duration));
    }

    IEnumerator AutoLevelPitchRoutine(float duration)
    {
        float t = 0f;
        float p0 = pitch;
        while (t < duration)
        {
            t += Time.deltaTime;
            pitch = Mathf.Lerp(p0, 0f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration)));
            yield return null;
        }
        pitch = 0f;
        autoLevelRoutine = null;
    }

    void ApplyCamera(float dt)
    {
        // In AR marker mode the Input System TrackedPoseDriver owns the camera pose
        // (physical walking + phone orientation); never overwrite it from here.
        if (arRigBuilt) return;

        pitch = Mathf.Clamp(pitch, -45f, 45f);
        float hyp = HypoxiaLevel();
        float roll = Mathf.Sin(Time.time * 0.9f) * hyp * 5f;
        cam.transform.rotation = Quaternion.Euler(pitch, yaw, roll);

        shake = Mathf.MoveTowards(shake, 0f, dt * 0.3f);
        Vector3 bob = moving ? new Vector3(Mathf.Cos(bobPhase * 0.5f) * 0.018f, Mathf.Sin(bobPhase) * 0.032f, 0f) : Vector3.zero;
        Vector3 sh = Random.insideUnitSphere * (shake + hyp * 0.02f);
        cam.transform.position = basePos + Vector3.up * heightOffset + Quaternion.Euler(0f, yaw, 0f) * bob + sh;
    }

    // =====================================================================================
    //  INTERACTION & FOCUS
    // =====================================================================================
    void UpdateFocus(bool modal)
    {
        Interactable best = null;
        if (!modal)
        {
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            if (Physics.SphereCast(ray, 0.15f, out RaycastHit hit, 6f, ~(1 << 2), QueryTriggerInteraction.Ignore) &&
                interactables.TryGetValue(hit.collider, out Interactable it) && it.active && hit.distance <= it.range)
                best = it;

            // Proximity assist for small screens: nearest active item close in front of the miner.
            if (best == null)
            {
                float bestD = 2.8f;
                foreach (Interactable i in interactables.Values)
                {
                    if (!i.active) continue;
                    Vector3 to = i.collider.bounds.center - cam.transform.position;
                    float d = to.magnitude;
                    if (d < bestD && Vector3.Dot(to.normalized, cam.transform.forward) > 0.35f) { bestD = d; best = i; }
                }
            }
        }

        focused = best;
        bool has = focused != null;
        actionButton.gameObject.SetActive(has);
        if (has)
        {
            actionLabel.text = focused.action;
            focusLabel.text = focused.label + "\n<size=62%><color=#C9CDD2>TAP OBJECT  /  " + focused.action + " BUTTON  /  [F]</color></size>";
        }
        else focusLabel.text = "";

        float target = has ? 1.4f : 1f;
        reticleRT.localScale = Vector3.one * Mathf.Lerp(reticleRT.localScale.x, target, Time.deltaTime * 12f);
        reticleRing.color = has ? new Color(UIAmber.r, UIAmber.g, UIAmber.b, 0.9f) : new Color(1f, 1f, 1f, 0.3f);
        reticleRT.gameObject.SetActive(!modal);
    }

    void TryInteractAt(Vector2 screenPos)
    {
        Ray ray = cam.ScreenPointToRay(screenPos);
        if (Physics.SphereCast(ray, 0.12f, out RaycastHit hit, 7f, ~(1 << 2), QueryTriggerInteraction.Ignore) &&
            interactables.TryGetValue(hit.collider, out Interactable it) && it.active)
        {
            if (hit.distance <= it.range + 0.5f) UseInteractable(it);
            else ShowToast("Move closer to " + it.label.ToLowerInvariant(), 1.8f);
        }
    }

    void UseInteractable(Interactable it)
    {
        if (it == null || !it.active || decisionOpen || debriefOpen || cinematicLock) return;
        // Safety equipment answers back: mechanical latch + a short haptic tick, so the
        // trainee feels the interaction land even with the phone held at arm's length.
        Play(clipLatch, 0.9f);
        Play(clipTick, 0.5f);
        Buzz(Haptic.Tick);
        it.onUse?.Invoke();
    }

    // =====================================================================================
    //  DECISION CARD LOGIC
    // =====================================================================================
    private string pendingCorrectText, pendingExplainOk, pendingExplainBad, pendingRef;
    private System.Action<bool> pendingCallback;

    void ShowDecision(int index, string category, string title, string body, string correctOpt, string wrongOpt, float seconds,
                      string explainOk, string explainBad, string reference, System.Action<bool> onResolved)
    {
        decisionOpen = true;
        ReleaseInput();
        cardRoot.SetActive(true);
        cardRoot.transform.SetAsLastSibling();
        cardStep.text = "COMPETENCY CHECK " + (index + 1) + " / " + TotalDecisions + "   -   " + category;
        cardTitle.text = title;
        cardBody.text = body;
        cardQuestionGroup.SetActive(true);
        cardFeedbackGroup.SetActive(false);

        pendingCorrectText = correctOpt;
        pendingExplainOk = explainOk;
        pendingExplainBad = explainBad;
        pendingRef = reference;
        pendingCallback = onResolved;

        bool flip = shuffleOptions && Random.value > 0.5f;
        optAText.text = flip ? wrongOpt : correctOpt;
        optBText.text = flip ? correctOpt : wrongOpt;
        optA.onClick.RemoveAllListeners();
        optB.onClick.RemoveAllListeners();
        optA.onClick.AddListener(() => ResolveDecision(!flip, false));
        optB.onClick.AddListener(() => ResolveDecision(flip, false));

        Play(clipBeep);
        Buzz(Haptic.Warning);   // double pulse: a scored decision is live
        if (decisionRoutine != null) StopCoroutine(decisionRoutine);
        decisionRoutine = StartCoroutine(DecisionTimer(seconds));
        StartCoroutine(PopIn(cardRoot.transform.GetChild(0)));
    }

    IEnumerator PopIn(Transform t)
    {
        float a = 0f;
        while (a < 1f)
        {
            a += Time.deltaTime / 0.18f;
            float e = 1f - Mathf.Pow(1f - Mathf.Clamp01(a), 3f);
            t.localScale = Vector3.one * Mathf.Lerp(0.92f, 1f, e);
            yield return null;
        }
        t.localScale = Vector3.one;
    }

    IEnumerator DecisionTimer(float seconds)
    {
        float t = seconds;
        int lastWhole = Mathf.CeilToInt(t);
        RectTransform fill = cardTimerFill.rectTransform;
        while (t > 0f)
        {
            t -= Time.deltaTime;
            float frac = Mathf.Clamp01(t / seconds);
            fill.anchorMax = new Vector2(Mathf.Max(0.002f, frac), 1f);
            cardTimerFill.color = frac < 0.3f ? Color.Lerp(UIRed, Color.white, Mathf.PingPong(Time.time * 4f, 0.3f)) : UIAmber;
            cardTimerText.text = Mathf.Max(0f, t).ToString("0.0") + " s";
            cardTimerText.color = frac < 0.3f ? UIRed : UITextCol;
            int whole = Mathf.CeilToInt(t);
            if (whole != lastWhole && whole <= 5) Play(clipTick, 1f);
            lastWhole = whole;
            yield return null;
        }
        decisionRoutine = null;
        ResolveDecision(false, true);
    }

    void ResolveDecision(bool correct, bool timedOut)
    {
        if (!decisionOpen || cardFeedbackGroup.activeSelf) return;
        if (decisionRoutine != null) { StopCoroutine(decisionRoutine); decisionRoutine = null; }

        cardQuestionGroup.SetActive(false);
        cardFeedbackGroup.SetActive(true);
        cardTimerText.text = "";
        cardResultBanner.color = correct ? new Color(0.12f, 0.55f, 0.32f) : new Color(0.62f, 0.14f, 0.13f);
        cardResultText.text = correct ? "SAFE DECISION" : (timedOut ? "NO ACTION TAKEN  -  TIME EXPIRED" : "UNSAFE DECISION");
        cardExplainText.text = correct ? pendingExplainOk
            : pendingExplainBad + "\n\n<color=#F5AB0D><b>Correct action:</b></color> " + pendingCorrectText;
        cardRefText.text = pendingRef;

        // Distinct pass / fail signature. PASS: rising triad + triple haptic pulse.
        // FAIL: low buzz + one long haptic. Nothing else in the drill sounds like these.
        if (correct)
        {
            Play(clipPass, 1f);
            Play(clipGood, 0.45f);
            Buzz(Haptic.Success);
        }
        else
        {
            Play(clipBad, 1f);
            Buzz(Haptic.Failure);
        }

        System.Action<bool> cb = pendingCallback;
        cardContinue.onClick.RemoveAllListeners();
        cardContinue.onClick.AddListener(() =>
        {
            cardRoot.SetActive(false);
            decisionOpen = false;
            cb?.Invoke(correct);
        });
    }

    void Record(int i, bool correct, float points, string note)
    {
        Competency c = comps[i];
        if (c.done) return;
        c.done = true;
        c.correct = correct;
        c.points = Mathf.Clamp(points, 0f, PointsPerCheck);
        c.note = note;
    }

    // =====================================================================================
    //  PHASE 1 - STRATA SOUNDING
    // =====================================================================================
    void OnSoundRoof()
    {
        if (soundingDone || phase != Phase.ShiftWalkIn) return;
        StartCoroutine(SoundingSequence());
    }

    IEnumerator SoundingSequence()
    {
        slabIt.active = false;
        cinematicLock = true;
        ShowToast("SOUNDING THE ROOF WITH THE BAR...", 2f);
        for (int i = 0; i < 3; i++)
        {
            Play(clipDrummy, 1f);
            shake += 0.025f;
            if (slabDust != null) slabDust.Emit(5);
            yield return new WaitForSeconds(0.6f);
        }
        ShowToast("HOLLOW  /  DRUMMY RESPONSE  -  LOOSE STRATA", 3f, UIAmber);
        yield return new WaitForSeconds(0.7f);
        cinematicLock = false;

        ShowDecision(0, "STRATA CONTROL", "The roof sounds DRUMMY",
            "Three blows with the sounding bar return a <b>hollow, drummy ring</b> instead of a solid ring. A fracture has separated a shale slab from the solid roof, and the nearby timber prop is leaning and split.\n\nWhat do you do before anyone advances?",
            "Stop. Withdraw from under it, set temporary support (prop + header) and report the defect to the Mining Sirdar.",
            "The roof looks intact. Walk quickly beneath the slab and continue the inspection.",
            decisionSeconds,
            "A drummy roof means the strata has already parted. Work stops, the place is fenced off and temporary support is set as per the Systematic Support Rules before anyone passes beneath. The defect is reported and recorded.",
            "A hollow sound means the slab is already detached and can fall without warning. Most roof-fall fatalities happen under unsupported roof that 'looked fine'. Never advance under drummy roof: support it first, then report.",
            "Ref: Coal Mines Regulations, 2017 - support of roof and sides; Systematic Support Rules; pre-shift examination by competent person.",
            ok => StartCoroutine(SoundingOutcome(ok)));
    }

    IEnumerator SoundingOutcome(bool ok)
    {
        if (ok)
        {
            Record(0, true, PointsPerCheck, "Identified drummy roof; set temporary support and reported.");
            yield return StartCoroutine(InstallTemporarySupport());
            ShowToast("TEMPORARY SUPPORT SET  -  DEFECT REPORTED", 3f, UIGreen);
        }
        else
        {
            Record(0, false, 0f, "Advanced under drummy roof - slab fell (near miss).");
            yield return StartCoroutine(SlabNearMiss());
            ShowToast("NEAR MISS!  The slab fell where you were about to walk.", 3.5f, UIRed);
        }
        CompleteSounding();
    }

    void CompleteSounding()
    {
        if (soundingDone) return;
        soundingDone = true;
        if (slabIt != null) slabIt.active = false;
        if (soundingTape != null) StartCoroutine(DropTape());
        maxZ = CaveTriggerZ + 1.5f;
        SetObjective("Continue inbye along the drift. Watch and listen for signs of roof weight.");
    }

    IEnumerator InstallTemporarySupport()
    {
        Transform root = strataSlab.parent;
        Vector3 basePt = new Vector3(0.25f, 0f, SlabZ);
        float topY = RoofHeight - 0.26f;
        GameObject propA = Prim(PrimitiveType.Cylinder, "TempProp", root, basePt + new Vector3(-0.45f, 0f, 0f), new Vector3(0.19f, 0.01f, 0.19f), mTimber);
        GameObject propB = Prim(PrimitiveType.Cylinder, "TempProp", root, basePt + new Vector3(0.55f, 0f, 0.1f), new Vector3(0.19f, 0.01f, 0.19f), mTimber);
        GameObject header = Box("TempHeader", root, new Vector3(0.3f, topY + 0.04f, SlabZ + 0.05f), new Vector3(1.6f, 0.1f, 0.24f), mTimber);
        header.SetActive(false);
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / 0.9f;
            float h = Mathf.Lerp(0.01f, topY, Mathf.SmoothStep(0f, 1f, t));
            propA.transform.localScale = new Vector3(0.19f, h * 0.5f, 0.19f);
            // h is a mine-space height: convert world -> mine, set y, convert back (no-op in classic mode).
            Vector3 mineA = WorldToMine(propA.transform.position);
            propA.transform.position = MineToWorld(new Vector3(mineA.x, h * 0.5f, mineA.z));
            propB.transform.localScale = new Vector3(0.19f, h * 0.5f, 0.19f);
            Vector3 mineB = WorldToMine(propB.transform.position);
            propB.transform.position = MineToWorld(new Vector3(mineB.x, h * 0.5f, mineB.z));
            yield return null;
        }
        header.SetActive(true);
        Play(clipDrummy, 0.5f);
        Play(clipClang, 0.15f);
    }

    IEnumerator SlabNearMiss()
    {
        Vector3 from = strataSlab.position;              // already world-space
        Quaternion r0 = strataSlab.rotation;             // already world-space
        Vector3 to = MineToWorld(new Vector3(0.85f, 0.12f, SlabZ + 0.2f));
        Quaternion r1 = MineToWorldRot(Quaternion.Euler(6f, 25f, 14f));
        Play(clipSnap, 1f);
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / 0.55f;
            float e = t * t;
            strataSlab.SetPositionAndRotation(Vector3.Lerp(from, to, e), Quaternion.Slerp(r0, r1, e));
            yield return null;
        }
        Play(clipCaveIn, 0.45f);
        shake = 0.28f;
        Vibrate();
        if (slabDust != null) { slabDust.transform.position = to + Vector3.up * 0.4f; slabDust.Emit(40); }
        yield return new WaitForSeconds(0.5f);
    }

    IEnumerator DropTape()
    {
        Transform tape = soundingTape.transform;
        float t = 0f;
        Vector3 p0 = tape.position;
        while (t < 1f)
        {
            t += Time.deltaTime / 0.6f;
            tape.position = p0 + Vector3.down * 1.6f * t * t;
            yield return null;
        }
        soundingTape.SetActive(false);
    }

    // =====================================================================================
    //  PHASE 2 - STRATA FAILURE & CAVE-IN
    // =====================================================================================
    void UpdateTriggers()
    {
        if (debriefOpen) return;

        if (phase == Phase.ShiftWalkIn && soundingDone && !caveInStarted && basePos.z >= CaveTriggerZ)
            StartCoroutine(CaveInSequence());

        if (phase == Phase.Wayfinding && !cinematicLock)
        {
            float xcutNear = XcutZ - XcutHalfDepth + 0.65f;
            if (!wayfindCommitted && basePos.z >= xcutNear - 0.05f)
            {
                if (basePos.x < -3.4f)
                {
                    wayfindCommitted = true;
                    Record(3, false, 0f, "Turned into the smoke-logged return airway / abandoned workings.");
                    Play(clipBad);
                    Buzz(Haptic.Failure);
                }
                else if (basePos.x > 3.4f)
                {
                    wayfindCommitted = true;
                    Record(3, true, PointsPerCheck, "Chose the intake escapeway (green chevrons + lifeline).");
                    Play(clipPass, 0.9f);
                    Play(clipGood);
                    Buzz(Haptic.Success);
                    ShowToast("INTAKE ESCAPEWAY  -  FRESH AIR. FOLLOW THE LIFELINE.", 3f, UIGreen);
                    SetObjective("Follow the green chevrons and lifeline to the SHAFT BOTTOM and step into the cage.");
                }
            }
            if (basePos.x < -4.2f && !leftWarned)
            {
                leftWarned = true;
                ShowToast("CO > 900 ppm  -  BLACKDAMP!  TURN BACK NOW!", 4f, UIRed);
                SetObjective("WRONG AIRWAY. Turn back to the junction and take the INTAKE ESCAPEWAY (right).");
            }
            if (basePos.x < -9f)
                Fatal("You entered sealed abandoned workings. Oxygen-deficient blackdamp, dense smoke and a run-in of old roof trapped you beyond the reach of rescue teams, who search the designated escapeways first.");
            if (basePos.x > ShaftX - 1.0f && !cageEntered)
                StartCoroutine(CageSequence());
        }
    }

    IEnumerator CaveInSequence()
    {
        caveInStarted = true;
        SetPhase(Phase.StrataFailure);
        minZ = basePos.z - 0.4f;
        maxZ = basePos.z + 1.2f;
        SetObjective("ROOF WEIGHT! The strata is working - be ready to react.");

        if (gritFall != null)
        {
            Vector3 gritMine = WorldToMine(cam.transform.position); // falls just in front of the miner
            Place(gritFall.transform, new Vector3(gritMine.x, RoofHeight - 0.05f, gritMine.z - 1f));
            gritFall.Play();
        }
        Play(clipCreak, 0.9f);
        shake = 0.05f;
        yield return new WaitForSeconds(1.0f);
        Play(clipCreak, 1f);
        shake = 0.09f;
        StartCoroutine(FlickerLights(roofLights.FindAll(l => Mathf.Abs(WorldToMine(l.transform.position).z - CollapseZ) < 7f), 1.2f, false));
        yield return new WaitForSeconds(0.9f);
        Play(clipSnap, 1f);
        shake = 0.14f;
        yield return new WaitForSeconds(0.55f);

        // Main fall: boulders seal the intake drift behind the miner.
        Play(clipCaveIn, 1f);
        shake = 0.5f;
        Vibrate();
        for (int i = 0; i < roofLights.Count; i++)
        {
            if (Mathf.Abs(WorldToMine(roofLights[i].transform.position).z - CollapseZ) < 4.5f)
            {
                roofLights[i].intensity = 0f;
                SetLens(i, false);
            }
        }
        capFailRoutine = StartCoroutine(CapLampFailure());
        for (int i = 0; i < rubble.Count; i++) StartCoroutine(DropBoulder(rubble[i], rubbleTargets[i], i * 0.035f));
        foreach (Rigidbody rb in looseRocks)
        {
            rb.gameObject.SetActive(true);
            rb.isKinematic = false;
            rb.AddForce(new Vector3(Random.Range(-1f, 1f), -2f, Random.Range(-1f, 1f)), ForceMode.Impulse);
            rb.AddTorque(Random.insideUnitSphere * 5f, ForceMode.Impulse);
        }
        if (dustBurst != null) dustBurst.Emit(140);
        StartCoroutine(LerpFog(0.12f, new Color(0.09f, 0.08f, 0.07f), 0.8f));
        minZ = CollapseZ + 2.1f;
        yield return new WaitForSeconds(1.0f);

        ShowDecision(1, "STRATA RUPTURE REACTION", "ROOF COLLAPSING BEHIND YOU",
            "A loud crack - the roof between you and the intake has failed and rock is still falling. The steel arch above you is taking weight and dust is blinding.\n\nYou have seconds. Where do you go?",
            "Dive forward under the nearest standing steel arch set and keep low - stay under supported roof.",
            "Run back toward the intake through the fall zone to reach fresh air.",
            caveInDecisionSeconds,
            "Standing arch supports are the safest refuge during an active fall. Running through or under an active shear plane exposes you to secondary falls. Once the fall stops, take stock and use the alternative escapeway.",
            "The fall zone is still active - secondary falls and run-ins of rock kill escapees and would-be rescuers. Never pass under falling or unsupported roof; retreat to the nearest standing support.",
            "Ref: Mine Emergency Action Plan (DGMS) - roof-fall response; Coal Mines Regulations, 2017 - escape and refuge.",
            ok => StartCoroutine(CaveInOutcome(ok)));
    }

    IEnumerator CaveInOutcome(bool ok)
    {
        maxZ = StationZ + 4f;
        minZ = CollapseZ + 2.1f;
        SmoothAutoLevelPitch(0.4f);

        if (ok)
        {
            Record(1, true, PointsPerCheck, "Retreated under standing arch support during the fall.");
            ShowToast("SHELTERED UNDER THE ARCH  -  THE FALL HAS SEALED THE INTAKE", 3f, UIGreen);
        }
        else
        {
            Record(1, false, 0f, "Moved into the active fall zone - struck by debris.");
            shake = 0.35f;
            Play(clipSnap, 1f);
            Vibrate();
            ShowToast("STRUCK BY FALLING ROCK  -  INJURED, BUT CONSCIOUS", 3.5f, UIRed);
            StartCoroutine(FlashScreen(new Color(0.6f, 0f, 0f, 0.55f), 0.8f));
        }
        if (gritFall != null) gritFall.Stop();
        StartCoroutine(LerpFog(0.07f, new Color(0.05f, 0.045f, 0.04f), 4f));
        yield return new WaitForSeconds(0.4f);
        StartCoroutine(GasSequence());
    }

    IEnumerator DropBoulder(Transform b, Vector3 mineTarget, float delay)
    {
        yield return new WaitForSeconds(delay);
        b.gameObject.SetActive(true);
        Vector3 from = b.position;                                  // world-space
        Vector3 target = MineToWorld(mineTarget);                   // mine-space -> world (no-op in classic mode)
        Quaternion r0 = b.rotation, r1 = r0 * Quaternion.Euler(Random.Range(-60f, 60f), Random.Range(-90f, 90f), Random.Range(-60f, 60f));
        float fallTime = Mathf.Sqrt(2f * Mathf.Max(0.1f, from.y - target.y) / 9.81f);
        float t = 0f;
        while (t < fallTime)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / fallTime);
            b.SetPositionAndRotation(Vector3.Lerp(from, target, k * k), Quaternion.Slerp(r0, r1, k));
            yield return null;
        }
        // Small settle bounce.
        float bt = 0f;
        while (bt < 0.25f)
        {
            bt += Time.deltaTime;
            b.position = target + Vector3.up * Mathf.Sin(bt / 0.25f * Mathf.PI) * 0.08f;
            yield return null;
        }
        b.position = target;
    }

    IEnumerator CapLampFailure()
    {
        // Cap-lamp cable is damaged by the fall: violent flicker, then dim emergency backup glow.
        float t = 0f;
        while (t < 0.9f)
        {
            t += Time.deltaTime;
            capLamp.intensity = Random.value > 0.5f ? 0.9f : 0.15f;
            yield return new WaitForSeconds(0.05f);
        }
        capLamp.intensity = 0.45f;
        capLamp.range = 6.5f;
    }

    // =====================================================================================
    //  PHASE 3 - TOXIC GAS, BLACKOUT & HYPOXIA
    // =====================================================================================
    IEnumerator GasSequence()
    {
        if (gasStarted) yield break;
        gasStarted = true;
        gasArmed = false;
        gasTimer = OxyTotal;          // armed BEFORE the phase flips - fixes the instant hypoxia death
        gasStartTime = elapsed;
        SmoothAutoLevelPitch(0.5f);
        SetPhase(Phase.ToxicGas);
        SetObjective("GAS LEAK! Tap WEAR MASK (or the green cache) - you have " + Mathf.RoundToInt(OxyTotal) + " s of breathable air.");
        caveInStarted = true;
        soundingDone = true;

        // The fall shears the brattice and ruptures a gas pocket; cables short out.
        Play(clipHiss, 1f);
        if (gasHaze != null) gasHaze.Play();
        yield return new WaitForSeconds(0.5f);
        Play(clipSpark, 1f);
        if (sparks != null) sparks.Emit(90);
        yield return StartCoroutine(FlickerLights(roofLights, 0.9f, false));
        for (int i = 0; i < roofLights.Count; i++) SetLens(i, false);

        // Blackout: only battery beacons and the self-rescuer cache remain lit.
        RenderSettings.ambientLight = new Color(0.035f, 0.02f, 0.018f);
        StartCoroutine(LerpFog(0.075f, new Color(0.02f, 0.018f, 0.015f), 1.5f));
        foreach (Light l in beaconLights) l.intensity = 5.5f;
        StartLoop(alarmSrc, clipAlarm, 0.5f);
        StartLoop(breathSrc, clipBreathHeavy, 0.55f);
        if (ambSrc != null) ambSrc.volume = 0.12f; // main fan tripped - ventilation lost

        gasTimer = OxyTotal;
        gasStartTime = elapsed;
        gasArmed = true;              // countdown only runs once the blackout cinematic has finished
        maskNudgeTimer = 0f;
        o2Group.gameObject.SetActive(true);
        minZ = Mathf.Max(minZ, CollapseZ + 2.1f);
        maxZ = StationZ + 4f;
        Vibrate();
        ShowToast("METHANE ALARM  -  CH4 > 1.25 %  -  CO RISING", 3.5f, UIRed);
        SetObjective("TOXIC GAS + BLACKOUT! Tap WEAR MASK now - or walk to the green SELF-RESCUER CACHE - before your air runs out.");
        ShowToast("TAP  WEAR MASK  (bottom-right)", 4f, UIGreen);
    }

    private float OxyTotal { get { return Mathf.Max(oxygenSeconds, 60f); } }

    float HypoxiaLevel()
    {
        if (!gasStarted || maskOn || OxyTotal <= 0f) return 0f;
        return 1f - Mathf.Clamp01(gasTimer / OxyTotal);
    }

    void UpdateGas(float dt, bool modal)
    {
        if (phase != Phase.ToxicGas || maskOn || debriefOpen || !gasArmed) return;
        if (!modal) gasTimer -= dt;
        if (!modal)
        {
            maskNudgeTimer += dt;
            if (maskNudgeTimer > 10f && gasTimer > 0f)
            {
                maskNudgeTimer = 0f;
                ShowToast("AIR RUNNING LOW  -  TAP WEAR MASK!", 2.5f, UIRed);
                Play(clipBeep, 0.8f);
            }
        }
        float hyp = HypoxiaLevel();
        if (breathSrc != null && breathSrc.isPlaying)
        {
            breathSrc.pitch = Mathf.Lerp(1f, 1.4f, hyp);
            breathSrc.volume = Mathf.Lerp(0.45f, 0.85f, hyp);
        }
        if (gasTimer <= 0f)
            Fatal("Hypoxia. Breathable air ran out before the self-rescuer was donned. Toxic CO and oxygen-deficient air cause collapse within minutes - the self-rescuer must be worn at the first sign of gas.");
    }

    void OnSelfRescuer()
    {
        if (phase == Phase.ToxicGas && !maskOn) StartCoroutine(DonRespirator());
        else ShowToast("Self-rescuer cache: seals intact, units in date.", 2f);
    }

    IEnumerator DonRespirator()
    {
        maskOn = true;
        if (stationIt != null) stationIt.active = false;
        if (maskButton != null) maskButton.gameObject.SetActive(false);
        if (capFailRoutine != null) { StopCoroutine(capFailRoutine); capFailRoutine = null; }
        cinematicLock = true;
        maskDonFraction = Mathf.Clamp01(gasTimer / OxyTotal);
        float donTime = elapsed - gasStartTime;

        string[] steps = { "1/4  OPEN THE CASE - PULL THE RED TAB", "2/4  BITE THE MOUTHPIECE - SEAL YOUR LIPS",
                           "3/4  FIT THE NOSE CLIP", "4/4  GOGGLES ON - ADJUST NECK STRAP" };
        foreach (string s in steps)
        {
            ShowToast(s, 0.9f, UIGreen);
            Play(clipTick, 1f);
            Buzz(Haptic.Tick);              // one crisp tick per donning step
            yield return new WaitForSeconds(0.55f);
        }

        // Respirator sealed: the single most important action in the drill gets its own chime.
        Play(clipPass, 0.85f);
        Play(clipGood, 1f);
        Buzz(Haptic.Confirm);
        StartLoop(breathSrc, clipRespirator, 0.5f, 1f);
        if (alarmSrc != null) alarmSrc.volume = 0.22f;

        // Cap lamp re-engaged on the emergency 15 m beam.
        capLamp.range = 15f;
        capLamp.spotAngle = 40f;
        capLamp.innerSpotAngle = 16f;
        for (int i = 0; i < 6; i++)
        {
            capLamp.intensity = (i & 1) == 0 ? 2.8f : 0.4f;
            yield return new WaitForSeconds(0.06f);
        }
        capLamp.intensity = 2.8f;

        float points = maskDonFraction >= respiratorFullCreditFraction
            ? PointsPerCheck
            : PointsPerCheck * Mathf.Clamp01(Mathf.Lerp(0.3f, 1f, maskDonFraction / respiratorFullCreditFraction));
        bool good = maskDonFraction >= 0.3f;
        Record(2, good, points, "Donned in " + donTime.ToString("0.0") + " s with " + Mathf.RoundToInt(maskDonFraction * 100f) +
               "% breathable air left" + (good ? "." : " - too slow, risk of incapacitation."));

        o2Label.text = "SELF-RESCUER  -  CHEMICAL O2 SUPPLY";
        o2Label.color = new Color(0.55f, 1f, 0.7f);
        cinematicLock = false;
        SetPhase(Phase.Wayfinding);
        maxZ = XcutZ + XcutHalfDepth;
        ShowToast("SELF-RESCUER ON  -  CAP LAMP: 15 m EMERGENCY BEAM", 3f, UIGreen);
        SetObjective("Walk inbye to the T-JUNCTION. Identify the INTAKE ESCAPEWAY to the shaft - avoid the return airway.");
    }

    // =====================================================================================
    //  PHASE 5 - SHAFT CAGE & ASCENT
    // =====================================================================================
    IEnumerator CageSequence()
    {
        cageEntered = true;
        cinematicLock = true;
        SetPhase(Phase.Evacuation);
        if (!comps[3].done) Record(3, true, PointsPerCheck, "Reached the shaft via the intake escapeway.");
        SetObjective("Cage gate latched. Signalling the banksman: 3 bells - man riding.");

        // Step into the centre of the cage, turn to face the landing.
        Vector3 p0 = basePos, p1 = new Vector3(ShaftX + 0.1f, EyeHeight, XcutZ);
        float y0 = yaw, y1 = -90f, pt0 = pitch;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / 0.9f;
            float e = Mathf.SmoothStep(0f, 1f, t);
            SetARLogicalPosition(Vector3.Lerp(p0, p1, e));
            yaw = Mathf.LerpAngle(y0, y1, e);
            pitch = Mathf.Lerp(pt0, 0f, e);
            yield return null;
        }

        // Drop gate slams down.
        Vector3 g0 = cageGate.localPosition, g1 = g0 + Vector3.down * (GateRaisedY - GateClosedY);
        t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / 0.35f;
            cageGate.localPosition = Vector3.Lerp(g0, g1, t * t);
            yield return null;
        }
        Play(clipClang, 1f);
        shake = 0.08f;
        Buzz(Haptic.Impact);                // the drop gate lands

        for (int i = 0; i < 3; i++)
        {
            Play(clipChime, 0.8f);
            Buzz(Haptic.Tick);              // three bells, three taps: man riding
            yield return new WaitForSeconds(0.7f);
        }
        ShowToast("BANKSMAN ACKNOWLEDGED  -  RAISING CAGE", 2.5f, UIGreen);
        StartLoop(winchSrc, clipWinch, 0.7f);
        if (alarmSrc != null) alarmSrc.Stop();

        // Ascent with ease-in/out; fade to surface daylight near the top.
        const float rideTime = 8f, rise = 30f;
        t = 0f;
        bool fading = false;
        while (t < rideTime)
        {
            t += Time.deltaTime;
            float e = Mathf.SmoothStep(0f, 1f, t / rideTime);
            float y = e * rise;
            Place(cage, new Vector3(ShaftX, y, XcutZ));
            heightOffset = y;
            shake = Mathf.Max(shake, 0.006f);
            if (winchSrc != null) winchSrc.pitch = 0.85f + 0.3f * Mathf.Sin(Mathf.PI * t / rideTime);
            if (!fading && t > rideTime - 2.6f)
            {
                fading = true;
                StartCoroutine(FadeTo(new Color(1f, 0.98f, 0.93f, 1f), 2.4f));
            }
            yield return null;
        }
        if (winchSrc != null) winchSrc.Stop();
        Play(clipChime, 1f);
        Play(clipPass, 0.7f);
        Buzz(Haptic.Confirm);               // banked at the surface

        survived = true;
        outcomeReason = "Evacuated through the intake escapeway and raised to the surface in the shaft cage.";
        ShowDebrief();
        yield return StartCoroutine(FadeFrom(new Color(1f, 0.98f, 0.93f, 1f), 0.9f));
    }

    void Fatal(string reason)
    {
        if (debriefOpen || phase == Phase.Debrief) return;
        survived = false;
        outcomeReason = reason;
        if (decisionRoutine != null) { StopCoroutine(decisionRoutine); decisionRoutine = null; }
        cardRoot.SetActive(false);
        decisionOpen = false;
        Play(clipBad, 1f);
        Buzz(Haptic.Failure);
        StartCoroutine(FatalSequence());
    }

    IEnumerator FatalSequence()
    {
        phase = Phase.Debrief;
        cinematicLock = true;
        if (breathSrc != null) breathSrc.pitch = 0.7f;
        float t = 0f;
        float p0 = pitch;
        while (t < 1.6f)
        {
            t += Time.deltaTime;
            heightOffset = Mathf.Lerp(0f, -1.25f, Mathf.SmoothStep(0f, 1f, t / 1.6f));
            pitch = Mathf.Lerp(p0, 40f, t / 1.6f);
            yield return null;
        }
        yield return StartCoroutine(FadeTo(Color.black, 1.2f));
        ShowDebrief();
        yield return StartCoroutine(FadeFrom(Color.black, 0.6f));
    }

    // =====================================================================================
    //  LIVE EFFECTS
    // =====================================================================================
    void UpdateEffects(float dt)
    {
        float time = Time.time;

        // Interaction glow pulses on anything still usable.
        foreach (Interactable it in interactables.Values)
        {
            if (it.glowMat == null) continue;
            float k = it.active ? 0.55f + 0.45f * Mathf.Sin(time * 4f) : 0.25f;
            Color c = it.glowColor * k;
            c.a = 1f;
            SetMatColor(it.glowMat, c);
        }

        bool alarmActive = gasStarted && phase != Phase.Evacuation && phase != Phase.Debrief;
        for (int i = 0; i < beaconLights.Count; i++)
        {
            Light l = beaconLights[i];
            if (alarmActive)
            {
                float spin = time * 300f + i * 90f;
                l.transform.rotation = MineToWorldRot(Quaternion.Euler(12f, spin, 0f));
                float face = Mathf.Abs(Mathf.Sin(spin * Mathf.Deg2Rad));
                SetMatColor(beaconMats[i], new Color(0.35f + 0.65f * face, 0.04f, 0.02f));
            }
            else if (gasStarted) l.intensity = Mathf.MoveTowards(l.intensity, 0f, dt * 3f);
        }

        float hyp = HypoxiaLevel();
        float flash = 0f;
        if (alarmActive) flash = (maskOn ? 0.06f : 0.16f + 0.12f * hyp) * (0.5f + 0.5f * Mathf.Sin(time * Mathf.PI * 2.2f));
        alarmFlash.color = new Color(0.9f, 0.05f, 0.02f, flash);

        float vigA = 0.5f;
        if (phase == Phase.ToxicGas && !maskOn) vigA = Mathf.Lerp(0.6f, 1f, hyp);
        else if (maskOn && phase != Phase.Debrief) vigA = 0.72f; // respirator goggles frame
        vignette.color = Color.Lerp(new Color(0f, 0f, 0f, vigA), new Color(0.12f, 0f, 0f, vigA), hyp);
        // Tunnel vision: periodic hypoxic "grey-out" pulses as air runs low.
        float dim = hyp * hyp * (0.45f + 0.25f * Mathf.Sin(time * 2.4f));
        hypoxiaDim.color = new Color(0.02f, 0f, 0f, Mathf.Clamp01(dim));

        if (brattice != null && gasStarted)
            brattice.rotation = MineToWorldRot(Quaternion.Euler(Mathf.Sin(time * 7f) * 6f, Mathf.Sin(time * 3.1f) * 10f, 24f + Mathf.Sin(time * 5.3f) * 12f));

        if (gritFall != null && phase == Phase.StrataFailure)
        {
            Vector3 gritMine = WorldToMine(cam.transform.position);
            Place(gritFall.transform, new Vector3(gritMine.x, RoofHeight - 0.05f, gritMine.z - 0.5f));
        }

        if (stationLight != null)
        {
            stationLight.range = 11f;
            stationLight.intensity = (phase == Phase.ToxicGas && !maskOn) ? 2.8f + 0.8f * Mathf.Sin(time * 5f) : 2.8f;
        }
    }

    void SetLens(int i, bool on)
    {
        if (i < 0 || i >= lampLensMats.Count) return;
        SetMatColor(lampLensMats[i], on ? new Color(1f, 0.86f, 0.6f) : new Color(0.12f, 0.11f, 0.1f));
    }

    IEnumerator FlickerLights(List<Light> lights, float seconds, bool endOn)
    {
        Dictionary<Light, float> baseI = new Dictionary<Light, float>();
        foreach (Light l in lights) if (l != null) baseI[l] = Mathf.Max(l.intensity, 1.7f);
        float t = 0f;
        while (t < seconds)
        {
            t += 0.06f;
            foreach (KeyValuePair<Light, float> kv in baseI)
                kv.Key.intensity = Random.value > 0.45f ? kv.Value * Random.Range(0.3f, 1.1f) : 0f;
            yield return new WaitForSeconds(0.06f);
        }
        foreach (KeyValuePair<Light, float> kv in baseI) kv.Key.intensity = endOn ? kv.Value : 0f;
    }

    IEnumerator LerpFog(float density, Color color, float seconds)
    {
        float d0 = RenderSettings.fogDensity;
        Color c0 = RenderSettings.fogColor;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / Mathf.Max(0.01f, seconds);
            RenderSettings.fogDensity = Mathf.Lerp(d0, density, t);
            RenderSettings.fogColor = Color.Lerp(c0, color, t);
            yield return null;
        }
    }

    IEnumerator FlashScreen(Color c, float seconds)
    {
        fadeImg.transform.SetAsLastSibling();
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / seconds;
            fadeImg.color = new Color(c.r, c.g, c.b, c.a * (1f - t));
            yield return null;
        }
        fadeImg.color = Color.clear;
    }

    IEnumerator FadeTo(Color c, float seconds)
    {
        fadeImg.transform.SetAsLastSibling();
        Color from = new Color(c.r, c.g, c.b, fadeImg.color.a);
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / seconds;
            fadeImg.color = Color.Lerp(from, new Color(c.r, c.g, c.b, 1f), t);
            yield return null;
        }
    }

    IEnumerator FadeFrom(Color c, float seconds)
    {
        fadeImg.transform.SetAsLastSibling();
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / seconds;
            fadeImg.color = new Color(c.r, c.g, c.b, 1f - Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }
        fadeImg.color = Color.clear;
    }

    // =====================================================================================
    //  HUD UPDATE
    // =====================================================================================
    private float ch4, co, o2 = 20.9f;

    void UpdateHUD(float dt)
    {
        timerText.text = FormatTime(elapsed);

        if (toastTimer > 0f)
        {
            toastTimer -= dt;
            float a = Mathf.Clamp01(toastTimer * 2.5f);
            toastText.alpha = a;
            toastBg.color = new Color(0.05f, 0.05f, 0.06f, 0.88f * a);
            if (toastTimer <= 0f) toastRT.gameObject.SetActive(false);
        }
        if (hintTimer > 0f)
        {
            hintTimer -= dt;
            hintText.alpha = Mathf.Clamp01(hintTimer) * 0.6f;
        }

        UpdateJoystickVisual();
        UpdateOxygenBar();
        UpdateDetector(dt);
        UpdateCriticalPulse();
    }

    // =====================================================================================
    //  CRITICAL-ACTION PULSE
    // =====================================================================================
    //  One shared Mathf.Sin(Time.time * 6f) carrier drives a smooth, continuous breathing
    //  scale on every life-saving control, plus a soft halo and a brightening tint. Under
    //  blackout + hypoxia the trainee is panicking, so the control that saves them has to be
    //  the most alive thing on the screen. Purely visual: no layout or logic depends on it.
    void UpdateCriticalPulse()
    {
        float carrier = Mathf.Sin(Time.time * 6f);      // -1 .. 1, the requested pulse driver
        float unit = 0.5f + 0.5f * carrier;             //  0 .. 1, same phase

        if (maskButton != null)
        {
            bool showMask = phase == Phase.ToxicGas && gasArmed && !maskOn && !decisionOpen && !debriefOpen;
            if (maskButton.gameObject.activeSelf != showMask) maskButton.gameObject.SetActive(showMask);
            if (maskHalo != null && maskHalo.gameObject.activeSelf != showMask) maskHalo.gameObject.SetActive(showMask);

            if (showMask)
            {
                // Urgency rises as the breathable air runs out: 4% swing at full air, 8% at empty.
                float urgency = Mathf.Clamp01(HypoxiaLevel());
                float amp = Mathf.Lerp(0.040f, 0.080f, urgency);
                float scale = 1f + amp * carrier;
                maskButton.transform.localScale = new Vector3(scale, scale, 1f);

                if (maskBtnImg != null)
                    maskBtnImg.color = Color.Lerp(maskBtnBase, new Color(0.35f, 0.95f, 0.55f, 1f), unit * 0.55f);
                if (maskLabel != null)
                    maskLabel.color = Color.Lerp(Color.white, new Color(0.88f, 1f, 0.92f), unit);

                if (maskHalo != null)
                {
                    float hs = 1f + amp * 1.9f * carrier;
                    maskHalo.rectTransform.localScale = new Vector3(hs, hs, 1f);
                    maskHalo.color = new Color(0.22f, 0.95f, 0.48f, Mathf.Lerp(0.07f, 0.26f, unit) * (0.55f + 0.45f * urgency));
                }
            }
            else
            {
                maskButton.transform.localScale = Vector3.one;
                if (maskBtnImg != null) maskBtnImg.color = maskBtnBase;
            }
        }

        // The context action button pulses too, but only while it offers a life-saving
        // interaction (the self-rescuer cache during the gas leak). Routine inspections
        // stay perfectly still so the pulse never loses its meaning.
        if (actionButton != null && actionButton.gameObject.activeSelf)
        {
            bool lifeSaving = focused != null && stationIt != null && focused == stationIt &&
                              phase == Phase.ToxicGas && !maskOn;
            float s2 = lifeSaving ? 1f + 0.035f * carrier : 1f;
            actionButton.transform.localScale = new Vector3(s2, s2, 1f);
        }
    }

    void UpdateJoystickVisual()
    {
        bool touchDevice = Touchscreen.current != null;
        bool show = touchDevice && !debriefOpen && !decisionOpen && !cinematicLock;
        joyBaseRT.gameObject.SetActive(show);
        joyKnobRT.gameObject.SetActive(show);
        if (!show) return;

        if (joyTouchId >= 0 &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(joyLayerRT, joyOrigin, null, out Vector2 o) &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(joyLayerRT, joyCurrent, null, out Vector2 c))
        {
            Vector2 local0 = o - joyLayerRT.rect.min;
            Vector2 local1 = c - joyLayerRT.rect.min;
            joyBaseRT.anchoredPosition = local0;
            joyKnobRT.anchoredPosition = local0 + Vector2.ClampMagnitude(local1 - local0, joystickRadius);
            joyBase.color = new Color(1f, 1f, 1f, 0.45f);
            joyKnob.color = new Color(1f, 1f, 1f, 0.7f);
        }
        else
        {
            joyBaseRT.anchoredPosition = joyRestPos;
            joyKnobRT.anchoredPosition = joyRestPos;
            joyBase.color = new Color(1f, 1f, 1f, 0.18f);
            joyKnob.color = new Color(1f, 1f, 1f, 0.25f);
        }
    }

    void UpdateOxygenBar()
    {
        if (!o2Group.gameObject.activeSelf) return;
        RectTransform fill = o2Fill.rectTransform;
        if (maskOn)
        {
            fill.anchorMax = Vector2.Lerp(fill.anchorMax, Vector2.one, Time.deltaTime * 3f);
            o2Fill.color = UIGreen;
            o2Text.text = "~30 MIN";
            o2Text.color = UIGreen;
            o2Group.localScale = Vector3.one;
            return;
        }
        float frac = Mathf.Clamp01(gasTimer / OxyTotal);
        fill.anchorMax = new Vector2(Mathf.Max(0.004f, frac), 1f);
        bool low = frac < 0.35f;
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * (low ? 14f : 6f));
        o2Fill.color = Color.Lerp(new Color(0.75f, 0.12f, 0.1f), new Color(1f, 0.4f, 0.32f), pulse);
        o2Text.text = Mathf.Max(0f, gasTimer).ToString("0.0") + " s";
        o2Text.color = low ? Color.Lerp(UIRed, Color.white, pulse) : UITextCol;
        o2Group.localScale = Vector3.one * (low ? 1f + 0.025f * pulse : 1f);
    }

    void UpdateDetector(float dt)
    {
        float tCH4 = 0.12f, tCO = 4f, tO2 = 20.9f;
        if (gasStarted)
        {
            float t = elapsed - gasStartTime;
            float k = 1f - Mathf.Exp(-t / 6f);
            tCH4 = Mathf.Lerp(0.3f, 2.6f, k);
            tCO = Mathf.Lerp(15f, 480f, 1f - Mathf.Exp(-t / 8f));
            tO2 = Mathf.Lerp(20.9f, 16.2f, 1f - Mathf.Exp(-t / 10f));
            if (phase == Phase.Wayfinding || phase == Phase.Evacuation || cageEntered)
            {
                if (basePos.x > 3f) { tCH4 = 0.35f; tCO = 28f; tO2 = 20.4f; }     // intake air
                else if (basePos.x < -3f) { tCH4 = 3.8f; tCO = 940f; tO2 = 14.1f; } // return / abandoned
            }
        }
        float rate = 1f - Mathf.Exp(-dt * 1.5f);
        ch4 = Mathf.Lerp(ch4, tCH4, rate);
        co = Mathf.Lerp(co, tCO, rate);
        o2 = Mathf.Lerp(o2, tO2, rate);

        float n = Mathf.PerlinNoise(Time.time * 0.7f, 0.3f) - 0.5f;
        ch4Text.text = (ch4 + n * 0.02f).ToString("0.00") + "<size=60%> %</size>";
        coText.text = Mathf.RoundToInt(Mathf.Max(0f, co + n * 2f)) + "<size=60%> ppm</size>";
        o2DetText.text = (o2 + n * 0.05f).ToString("0.0") + "<size=60%> %</size>";
        ch4Text.color = ch4 >= 1.25f ? UIRed : ch4 >= 0.75f ? UIAmber : UITextCol;
        coText.color = co >= 50f ? UIRed : co >= 25f ? UIAmber : UITextCol;
        o2DetText.color = o2 < 19f ? UIRed : o2 < 19.5f ? UIAmber : UITextCol;

        bool alarm = ch4 >= 1.25f || co >= 50f || o2 < 19f;
        bool caution = !alarm && (ch4 >= 0.75f || co >= 25f || o2 < 19.5f);
        float blink = Mathf.PingPong(Time.time * 3f, 1f);
        detStatusText.text = alarm ? "ALARM" : caution ? "CAUTION" : "NORMAL";
        detStatusText.color = alarm ? Color.Lerp(UIRed, Color.white, blink * 0.5f) : caution ? UIAmber : UIGreen;
        detHeader.color = alarm ? new Color(0.9f, 0.2f, 0.18f, 0.12f + 0.2f * blink) : new Color(1f, 1f, 1f, 0.06f);
    }

    // =====================================================================================
    //  DEBRIEF, CLOUD SYNC, NAVIGATION
    // =====================================================================================
    int CorrectDecisions()
    {
        int n = 0;
        foreach (Competency c in comps) if (c.correct) n++;
        return n;
    }

    float SafetyPoints()
    {
        float p = 0f;
        foreach (Competency c in comps) p += c.points;
        return p;
    }

    void ShowDebrief()
    {
        if (debriefOpen) return;
        debriefOpen = true;
        phase = Phase.Debrief;
        cinematicLock = true;
        ReleaseInput();

        if (alarmSrc != null) alarmSrc.Stop();
        if (winchSrc != null) winchSrc.Stop();
        if (breathSrc != null) breathSrc.Stop();
        if (ambSrc != null) ambSrc.volume = 0.15f;
        safeRoot.gameObject.SetActive(false);
        alarmFlash.color = Color.clear;
        hypoxiaDim.color = Color.clear;

        int correct = CorrectDecisions();
        float pct = SafetyPoints() / (PointsPerCheck * TotalDecisions);
        bool passed = survived && pct >= passPercent;

        dbMeta.text = "TRAINEE  " + traineeId + "    |    SEAM II  -  INTAKE DRIFT 3    |    " + System.DateTime.Now.ToString("dd MMM yyyy  HH:mm");
        Color stampCol = passed ? UIGreen : survived ? UIAmber : UIRed;
        dbStamp.text = passed ? "QUALIFIED" : survived ? "RETRAINING\nREQUIRED" : "FATALITY\nDRILL FAILED";
        dbStamp.color = stampCol;
        dbStampFrame.color = stampCol;

        dbTime.text = FormatTime(elapsed);
        dbDecisions.text = correct + "<size=55%><color=#9AA0A6> / " + TotalDecisions + "</color></size>";
        dbScore.text = Mathf.RoundToInt(pct * 100f) + "<size=55%>%</size>";
        dbScore.color = pct >= passPercent ? UIGreen : pct >= 0.5f ? UIAmber : UIRed;
        dbO2.text = maskOn ? Mathf.RoundToInt(maskDonFraction * 100f) + "<size=55%>%</size>" : "--";

        for (int i = 0; i < TotalDecisions; i++)
        {
            Competency c = comps[i];
            string status;
            Color col;
            if (!c.done) { status = "NOT REACHED"; col = UIMuted; }
            else if (c.correct && c.points >= PointsPerCheck - 0.01f) { status = "PASS"; col = UIGreen; }
            else if (c.correct) { status = "PASS (" + Mathf.RoundToInt(c.points) + ")"; col = UIGreen; }
            else if (c.points > 0f) { status = "PARTIAL"; col = UIAmber; }
            else { status = "FAIL"; col = UIRed; }
            dbRowStatus[i].text = status;
            dbRowStatus[i].color = col;
            dbRowChip[i].color = col;
            dbRowNote[i].text = c.note;
        }

        string verdict = passed ? "<color=#3FD685><b>CERTIFIED SAFE.</b></color> "
                       : survived ? "<color=#F5AB0D><b>RETAKE MANDATORY.</b></color> "
                       : "<color=#E5484D><b>CRITICAL FAILURE.</b></color> ";
        dbOutcome.text = verdict + outcomeReason + "  Pass mark: " + Mathf.RoundToInt(passPercent * 100f) + "% safety score and a successful evacuation.";

        SyncResults();
        dbSync.text = syncStatus;

        // Verdict signature: the certificate stamp lands with its own audio + haptic.
        if (passed) { Play(clipPass, 1f); Buzz(Haptic.Success); }
        else if (survived) { Play(clipBeep, 1f); Buzz(Haptic.Warning); }
        else { Play(clipBad, 1f); Buzz(Haptic.Failure); }

        debriefRoot.SetActive(true);
        debriefRoot.transform.SetAsLastSibling();
        StartCoroutine(PopIn(debriefRoot.transform.GetChild(0)));
    }

    void SyncResults()
    {
        if (resultsSynced) return;
        resultsSynced = true;
        int correct = CorrectDecisions();
        int pct = Mathf.RoundToInt(SafetyPoints() / (PointsPerCheck * TotalDecisions) * 100f);

        // Local backup so results survive offline sessions.
        PlayerPrefs.SetInt("CMVR_LastDecisions", correct);
        PlayerPrefs.SetInt("CMVR_LastSafetyPct", pct);
        PlayerPrefs.SetFloat("CMVR_LastTime", elapsed);
        PlayerPrefs.SetInt("CMVR_LastSurvived", survived ? 1 : 0);
        PlayerPrefs.Save();

        try
        {
            if (BackendSync.Instance != null)
            {
                BackendSync.Instance.SaveAndSyncScore(correct, TotalDecisions);
                syncStatus = "Results dispatched to the control room (BackendSync): " + correct + "/" + TotalDecisions + " decisions, " + pct + "% safety score.";
            }
            else syncStatus = "Standalone mode: BackendSync not present - results saved on this device only.";
        }
        catch (System.Exception e)
        {
            syncStatus = "Cloud sync failed (" + e.Message + ") - results saved on this device.";
            Debug.LogWarning("[MineSimulationManager] BackendSync failed: " + e);
        }
    }

    void SaveAndReturn()
    {
        SyncResults();
        ReturnToMenu();
    }

    void ReturnToMenu()
    {
        if (!string.IsNullOrEmpty(mainMenuSceneName) && Application.CanStreamedLevelBeLoaded(mainMenuSceneName))
            SceneManager.LoadScene(mainMenuSceneName);
        else
            SceneManager.LoadScene(0);
    }

    // =====================================================================================
    //  EDITOR DEVELOPER PANEL
    // =====================================================================================
#if UNITY_EDITOR
    void OnGUI()
    {
        if (Event.current != null && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F12)
            showDebugPanel = !showDebugPanel;

        if (!showDebugPanel || debriefOpen) return;
        GUILayout.BeginArea(new Rect(Screen.width - 260, Screen.height * 0.35f, 250, 280), GUI.skin.box);
        GUILayout.Label("DEV  |  " + phase + "  |  z " + basePos.z.ToString("0.0") + "  x " + basePos.x.ToString("0.0"));
        GUILayout.Label("Pts " + SafetyPoints().ToString("0") + "  |  O2 " + gasTimer.ToString("0.0") + "s  |  " + FormatTime(elapsed));
        if (GUILayout.Button("1  Strata sounding (stand at barrier)")) { basePos = new Vector3(0f, EyeHeight, SoundingGateZ - 0.3f); yaw = 0f; pitch = -35f; }
        if (GUILayout.Button("2  Skip to cave-in") && !caveInStarted)
        {
            if (!soundingDone) { Record(0, true, PointsPerCheck, "[DEV] skipped"); CompleteSounding(); }
            basePos = new Vector3(0f, EyeHeight, CaveTriggerZ + 0.1f);
        }
        if (GUILayout.Button("3  Skip to gas leak") && !gasStarted && !decisionOpen)
        {
            if (!soundingDone) { Record(0, true, PointsPerCheck, "[DEV] skipped"); CompleteSounding(); }
            Record(1, true, PointsPerCheck, "[DEV] skipped");
            basePos = new Vector3(0f, EyeHeight, CaveTriggerZ + 0.5f);
            StartCoroutine(GasSequence());
        }
        if (GUILayout.Button("4  Don self-rescuer") && gasStarted && !maskOn)
        {
            basePos = new Vector3(0.8f, EyeHeight, StationZ - 1.2f);
            StartCoroutine(DonRespirator());
        }
        if (GUILayout.Button("5  Teleport to T-junction") && maskOn && phase == Phase.Wayfinding)
        { basePos = new Vector3(0f, EyeHeight, XcutZ - 1.2f); yaw = 0f; pitch = 0f; }
        if (GUILayout.Button("6  Teleport to shaft landing") && maskOn && phase == Phase.Wayfinding)
        { basePos = new Vector3(ShaftX - 2.5f, EyeHeight, XcutZ); yaw = 90f; pitch = 0f; }
        if (GUILayout.Button("Show debrief now")) { survived = maskOn; outcomeReason = "[DEV] Debrief forced."; ShowDebrief(); }
        GUILayout.EndArea();
    }
#endif
}