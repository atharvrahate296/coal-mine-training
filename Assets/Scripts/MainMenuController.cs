// =====================================================================
//  MainMenuController.cs  -  CoalMineVR Mine Dispatch Console (v3)
//  VIT Pune | CSE (AI & ML) | Guide: Prof. Kalyani Ghuge
//
//  Engine : Unity 6 (6000.5.x) + URP 17.5 | Android 10+ (API 29+), IL2CPP, ARM64
//
//  Drop-in replacement. Put it on the "MenuController" object in the
//  MainMenu scene. The whole UI is built in code, so no scene wiring is
//  needed. Public entry points are kept: OnStartARMode(),
//  OnVRModeClicked(), OnCloseVRPopup().
//
//  v3 INDUSTRIAL OVERHAUL
//   - No personal trainee name anywhere. The header is now a real
//     dispatch console: "MINE DISPATCH CONSOLE" + a neutral operator
//     badge ("SYSTEM STATUS: ONLINE", "OPR  EMP-1042") and a live clock.
//   - Dark brushed-metal plate styling generated procedurally (no image
//     assets): milled steel panels, hairline machined edges, rivet heads,
//     safety-yellow (#FCC721) accents and hazard stripes.
//   - One coherent type scale (FsDisplay .. FsMicro) so every label is
//     comfortably readable on a portrait phone, with tight auto-size
//     ranges so nothing ever collapses to unreadable or wraps awkwardly.
//   - Module cards stretch to fill the available height (LayoutElement
//     flexibleHeight), so tall phones get breathing room instead of a
//     dead gap at the bottom.
//
//  UNCHANGED ARCHITECTURE
//   - Anchor/layout based (no hard-coded pixel positions); follows
//     Screen.safeArea (punch-hole, gesture bar)
//   - Background photo (assign in Inspector, or it reuses the sprite
//     already in the scene), with a dark readability shade
//   - Module cards: AR Equipment Inspection, VR Disaster Drills
//   - VR scenario picker modal; scenes missing from Build Profiles are
//     shown as unavailable instead of silently loading the wrong one
//   - Deployment mode dialog for the VR drill: "START WITHOUT MARKER"
//     (classic spawn in front of the player) or "START WITH AR MARKER"
//     (ARCore image-tracking anchors the mine to a printed marker so the
//     trainee physically walks the tunnel). The choice is handed to the
//     VRMode scene with PlayerPrefs "CMVR_UseMarker" (0 / 1), which is
//     read by MineSimulationManager.
//   - Fade + async scene loading, double-tap protection
//   - Android back key: closes the modal, otherwise "press again to exit"
//   - Toast messages, creates an EventSystem if the scene has none
//   - Works with the new Input System and the legacy one
//
//  All on-screen text is plain ASCII so it renders in the default TMP font.
// =====================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

public class MainMenuController : MonoBehaviour
{
    [Header("Scenes (must be in Build Profiles)")]
    public string arSceneName = "ARMode";
    public string vrSceneName = "VRMode";
    public string blastingSceneName = "BlastingMode";

    [Header("Dispatch console identity (no personal names)")]
    public string consoleTitle = "MINE DISPATCH CONSOLE";
    public string consoleSubtitle = "AR / VR SAFETY TRAINING & ASSESSMENT";
    public string institution = "VIT PUNE  |  CSE (AI & ML)";
    [Tooltip("Neutral operator / licence ID printed on the console badge.")]
    public string operatorId = "EMP-1042";
    [Tooltip("Shown on the status badge: ONLINE / STANDBY / LOCAL.")]
    public string systemStatus = "ONLINE";
    public string stationName = "SURFACE CONTROL ROOM";

    [Header("Look")]
    [Tooltip("Optional background photo (e.g. MainMenuMineBackground). If empty, the script reuses a sprite named *Background* found in the scene.")]
    public Texture2D backgroundTexture;
    [Range(0f, 0.95f)] public float backgroundShade = 0.74f;
    [Tooltip("Strength of the procedural brushed-metal grain on every plate.")]
    [Range(0f, 0.35f)] public float metalStrength = 0.15f;
    [Tooltip("Faint blueprint grid behind the console (monitoring-dashboard feel).")]
    public bool showBlueprintGrid = true;
    public bool hideOtherCanvases = true;
    public bool forcePortrait = true;

    // =================================================================
    //  TYPE SCALE  (canvas reference 1080 x 1920 portrait)
    //  One scale for the whole console: nothing is tiny, nothing shouts.
    // =================================================================
    const float FsDisplay = 48f;   // console title
    const float FsH1      = 34f;   // modal titles / section heads
    const float FsTitle   = 32f;   // module card titles
    const float FsStat    = 34f;   // statistic chip values
    const float FsH2      = 26f;   // scenario / mode row titles
    const float FsButton  = 27f;   // button labels
    const float FsBody    = 24f;   // body copy, bullets
    const float FsLabel   = 21f;   // supporting labels
    const float FsMicro    = 18f;  // tags, footer, chip captions

    // ---------- palette: graphite + brushed steel + safety yellow #FCC721 ----------
    static readonly Color ColBg        = new Color(0.035f, 0.040f, 0.047f, 1f);
    static readonly Color ColPlate     = new Color(0.086f, 0.098f, 0.113f, 0.96f);
    static readonly Color ColPlateDeep = new Color(0.051f, 0.059f, 0.070f, 0.985f);
    static readonly Color ColPlate2    = new Color(0.125f, 0.141f, 0.161f, 1f);
    static readonly Color ColEdge      = new Color(1f, 1f, 1f, 0.11f);
    static readonly Color ColEdgeHi    = new Color(1f, 1f, 1f, 0.20f);
    static readonly Color ColYellow    = new Color(0.988f, 0.780f, 0.129f, 1f); // #FCC721
    static readonly Color ColYellowDim = new Color(0.62f, 0.47f, 0.07f, 1f);
    static readonly Color ColBlue      = new Color(0.30f, 0.72f, 1f, 1f);
    static readonly Color ColBlueDim   = new Color(0.11f, 0.38f, 0.70f, 1f);
    static readonly Color ColText      = new Color(0.90f, 0.93f, 0.96f, 1f);
    static readonly Color ColMuted     = new Color(0.58f, 0.63f, 0.70f, 1f);
    static readonly Color ColGreen     = new Color(0.25f, 0.82f, 0.45f, 1f);
    static readonly Color ColRed       = new Color(0.92f, 0.33f, 0.28f, 1f);

    // ---------- runtime refs ----------
    Canvas canvas;
    RectTransform rootRT, safeRT;
    RawImage backgroundPhoto, gridOverlay;
    Image statusDot, introDot;
    TextMeshProUGUI clockText;
    GameObject modal;
    GameObject modeModal;
    CanvasGroup modalGroup, modeGroup, toastGroup, fadeGroup;
    RectTransform modalPanel, modePanel;
    TextMeshProUGUI toastText, fadeLabel;
    Button s1Button, s2Button;
    TextMeshProUGUI s1Badge, s2Badge;
    readonly List<CanvasGroup> introGroups = new List<CanvasGroup>();

    Sprite roundSprite, circleSprite, outlineSprite, ringSprite;
    Texture2D stripeTex, brushedTex, gridTex;
    Coroutine toastCo, modalCo;

    Rect lastSafe;
    int lastW, lastH;
    bool loading;
    float lastBackTime = -10f;
    float lastClockTick = -10f;

    // PlayerPrefs key shared with MineSimulationManager (1 = with marker, 0 = without).
    const string UseMarkerPrefKey = "CMVR_UseMarker";

    // =================================================================
    //  LIFECYCLE
    // =================================================================
    void Start()
    {
        Time.timeScale = 1f; // in case we returned from a paused scene
        if (forcePortrait) Screen.orientation = ScreenOrientation.Portrait;

        EnsureEventSystem();
        CaptureOldBackground();
        if (hideOtherCanvases) HideOtherCanvases();

        roundSprite   = MakeRoundSprite(64, 20, 20);
        circleSprite  = MakeRoundSprite(64, 32, 0);
        outlineSprite = MakeOutlineSprite(64, 20, 2.0f, 20);
        ringSprite    = MakeOutlineSprite(64, 32, 3.0f, 0);
        stripeTex     = MakeStripeTexture();
        brushedTex    = MakeBrushedTexture(128, 128);
        gridTex       = MakeGridTexture(64);

        BuildConsole();
        StartCoroutine(IntroRoutine());
    }

    void Update()
    {
        if (canvas == null) return;

        if (Screen.safeArea != lastSafe || Screen.width != lastW || Screen.height != lastH)
        {
            ApplySafeArea();
            FitBackground();
        }

        // Breathing status LEDs on the console badge + module header.
        float led = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 3f);
        if (statusDot != null)
        {
            Color c = ColGreen; c.a = led;
            statusDot.color = c;
        }
        if (introDot != null)
        {
            Color c = ColGreen; c.a = 0.45f + 0.4f * Mathf.Sin(Time.unscaledTime * 3f + 1.1f);
            introDot.color = c;
        }

        // Dispatch clock: one refresh per second, never per frame.
        if (clockText != null && Time.unscaledTime - lastClockTick >= 1f)
        {
            lastClockTick = Time.unscaledTime;
            clockText.text = System.DateTime.Now.ToString("HH:mm:ss");
        }

        HandleBackKey();
    }

    void OnDestroy()
    {
        if (roundSprite != null)   Destroy(roundSprite.texture);
        if (circleSprite != null)  Destroy(circleSprite.texture);
        if (outlineSprite != null) Destroy(outlineSprite.texture);
        if (ringSprite != null)    Destroy(ringSprite.texture);
        if (stripeTex != null)  Destroy(stripeTex);
        if (brushedTex != null) Destroy(brushedTex);
        if (gridTex != null)    Destroy(gridTex);
    }

    // =================================================================
    //  SCENE PREP
    // =================================================================
    void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        GameObject es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        es.AddComponent<StandaloneInputModule>();
#endif
    }

    // Reuse the background your teammate put into the old menu, if any.
    void CaptureOldBackground()
    {
        if (backgroundTexture != null) return;
        foreach (GameObject go in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            foreach (Image im in go.GetComponentsInChildren<Image>(true))
            {
                if (im.sprite != null && im.sprite.name.ToLowerInvariant().Contains("background"))
                {
                    backgroundTexture = im.sprite.texture;
                    return;
                }
            }
        }
    }

    // Disable the old menu canvas(es) so they don't sit behind the new UI.
    void HideOtherCanvases()
    {
        foreach (GameObject go in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            foreach (Canvas c in go.GetComponentsInChildren<Canvas>(true))
            {
                if (!c.isRootCanvas) continue;
                if (transform.IsChildOf(c.transform)) continue;
                c.gameObject.SetActive(false);
            }
        }
    }

    // =================================================================
    //  UI BUILD
    // =================================================================
    void BuildConsole()
    {
        GameObject rootGO = new GameObject("DispatchConsoleHUD");
        canvas = rootGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;

        CanvasScaler scaler = rootGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        rootGO.AddComponent<GraphicRaycaster>();
        rootRT = (RectTransform)rootGO.transform;

        BuildBackground();

        safeRT = NewRect("SafeArea", rootRT);
        ApplySafeArea();

        BuildHeader(safeRT);
        BuildContent(safeRT);
        BuildFooter(safeRT);

        BuildScenarioModal(rootRT);
        BuildModeModal(rootRT);
        BuildToast(rootRT);
        BuildFadeOverlay(rootRT);
    }

    void BuildBackground()
    {
        Image solid = Plain("BackdropColor", rootRT, ColBg);
        Fill(solid.rectTransform, 0, 0, 0, 0);

        if (backgroundTexture != null)
        {
            RectTransform rt = NewRect("BackdropPhoto", rootRT);
            Fill(rt, 0, 0, 0, 0);
            backgroundPhoto = rt.gameObject.AddComponent<RawImage>();
            backgroundPhoto.texture = backgroundTexture;
            backgroundPhoto.raycastTarget = false;
        }

        Image shade = Plain("BackdropShade", rootRT, new Color(0.012f, 0.016f, 0.020f, backgroundShade));
        Fill(shade.rectTransform, 0, 0, 0, 0);

        if (showBlueprintGrid)
        {
            RectTransform gr = NewRect("BlueprintGrid", rootRT);
            Fill(gr, 0, 0, 0, 0);
            gridOverlay = gr.gameObject.AddComponent<RawImage>();
            gridOverlay.texture = gridTex;
            gridOverlay.color = new Color(0.45f, 0.62f, 0.78f, 0.075f);
            gridOverlay.raycastTarget = false;
        }

        FitBackground();
    }

    // -----------------------------------------------------------------
    //  HEADER: console identity (left) + operator / status badge (right)
    // -----------------------------------------------------------------
    void BuildHeader(Transform parent)
    {
        RectTransform header = Plate("Header", parent, ColPlate, false, true);
        TopStrip(header, 20, 20, 12, 208);

        // Safety-yellow command bar down the left edge.
        Image bar = Plain("CommandBar", header, ColYellow);
        LeftStrip(bar.rectTransform, 0, 8, 22, 22);

        // Hazard stripe along the bottom edge of the header.
        RawImage stripe = NewStripe("HeaderStripe", header, 12);
        BottomStrip(stripe.rectTransform, 20, 20, 12, 12);

        TextMeshProUGUI title = Txt(header, consoleTitle, FsDisplay, ColYellow,
                                    TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true, true);
        TopStrip(title.rectTransform, 34, 352, 20, 58);

        TextMeshProUGUI sub = Txt(header, consoleSubtitle, FsBody, ColBlue,
                                  TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true, true);
        TopStrip(sub.rectTransform, 34, 352, 84, 34);

        TextMeshProUGUI inst = Txt(header, institution, FsLabel, ColMuted,
                                   TextAlignmentOptions.MidlineLeft, FontStyles.Normal, true, true);
        TopStrip(inst.rectTransform, 34, 352, 122, 30);

        BuildOperatorBadge(header);
    }

    // Neutral, name-free console badge: system state, operator ID, clock.
    void BuildOperatorBadge(Transform parent)
    {
        RectTransform badge = Plate("OperatorBadge", parent, ColPlate2, false, false);
        Fixed(badge, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-22, -20), new Vector2(312, 140));
        Rivets(badge, 12f);

        TextMeshProUGUI lbl = Txt(badge, "SYSTEM STATUS", FsMicro, ColMuted,
                                  TextAlignmentOptions.MidlineLeft, FontStyles.Bold, false, true);
        TopStrip(lbl.rectTransform, 20, 16, 10, 24);

        Image bezel = NewImage("StatusBezel", badge, ringSprite, new Color(1f, 1f, 1f, 0.22f));
        Fixed(bezel.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(15, -35), new Vector2(26, 26));

        statusDot = NewImage("StatusLED", badge, circleSprite, ColGreen);
        Fixed(statusDot.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -40), new Vector2(16, 16));

        TextMeshProUGUI state = Txt(badge, systemStatus, FsH2, ColGreen,
                                    TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true, true);
        TopStrip(state.rectTransform, 44, 16, 34, 32);

        TextMeshProUGUI clk = Txt(badge, "00:00:00", FsLabel, ColText,
                                  TextAlignmentOptions.MidlineRight, FontStyles.Bold, false, true);
        TopStrip(clk.rectTransform, 160, 18, 36, 30);
        clockText = clk;

        Image rule = Plain("BadgeRule", badge, ColEdge);
        TopStrip(rule.rectTransform, 20, 18, 74, 2);

        TextMeshProUGUI opr = Txt(badge, "OPR  " + operatorId, FsLabel, ColYellow,
                                  TextAlignmentOptions.MidlineLeft, FontStyles.Bold, false, true);
        TopStrip(opr.rectTransform, 20, 16, 82, 28);

        TextMeshProUGUI lic = Txt(badge, "LICENCE  DGMS / TRG", FsMicro, ColMuted,
                                  TextAlignmentOptions.MidlineLeft, FontStyles.Normal, false, true);
        TopStrip(lic.rectTransform, 20, 16, 110, 24);
    }

    // -----------------------------------------------------------------
    //  CONTENT: module selector + statistic chips
    // -----------------------------------------------------------------
    void BuildContent(Transform parent)
    {
        RectTransform content = NewRect("Content", parent);
        Fill(content, 20, 112, 20, 232);

        VerticalLayoutGroup vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 20f;
        vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        // ---- selector header ----
        RectTransform intro = NewRect("Intro", content);
        intro.gameObject.AddComponent<LayoutElement>().preferredHeight = 104f;

        TextMeshProUGUI head = Txt(intro, "SELECT A TRAINING MODULE", FsH1, Color.white,
                                   TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true, true);
        TopStrip(head.rectTransform, 6, 272, 0, 46);

        RectTransform pill = Plate("OfflinePill", intro, ColPlate2, false, false);
        Fixed(pill, new Vector2(1, 1), new Vector2(1, 1), new Vector2(0, 0), new Vector2(252, 44));
        introDot = NewImage("OfflineLED", pill, circleSprite, ColGreen);
        Fixed(introDot.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(14, 14));
        TextMeshProUGUI st = Txt(pill, "OFFLINE-READY", FsMicro, ColGreen,
                                 TextAlignmentOptions.MidlineLeft, FontStyles.Bold, false, true);
        Fill(st.rectTransform, 38, 2, 14, 2);

        TextMeshProUGUI hint = Txt(intro, "Every answer is scored on this device. No network needed.",
                                   FsBody, ColMuted, TextAlignmentOptions.TopLeft, FontStyles.Normal, false, false);
        TopStrip(hint.rectTransform, 6, 6, 54, 46);

        // ---- module cards ----
        Button arBtn, vrBtn;
        CanvasGroup arCard = BuildCard(content, "Card_AR", "MODULE 01  |  AR", "AR EQUIPMENT INSPECTION",
            new[]
            {
                "3D scanner reticle with live target lock",
                "6 items: helmet, mask, signs, gas detector",
                "Scored quiz with instant feedback"
            },
            "LAUNCH AR INSPECTION", ColYellow, ColYellowDim, OnStartARMode, out arBtn);

        CanvasGroup vrCard = BuildCard(content, "Card_VR", "MODULE 02  |  VR", "VR DISASTER DRILLS",
            new[]
            {
                "Roof fall and toxic gas emergency drill",
                "Blackout escape with oxygen countdown",
                "Decision-based scoring and results"
            },
            "ENTER VR EMERGENCY DRILLS", ColBlue, ColBlueDim, OnVRModeClicked, out vrBtn);

        // ---- statistic chips (bottom strip) ----
        RectTransform strip = NewRect("Stats", content);
        LayoutElement sle = strip.gameObject.AddComponent<LayoutElement>();
        sle.preferredHeight = 134f;
        sle.flexibleHeight = 0f;
        HorizontalLayoutGroup hlg = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 14f;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = true;
        CanvasGroup c1 = BuildChip(strip, "06", "AR INSPECTION ITEMS", ColYellow);
        CanvasGroup c2 = BuildChip(strip, "05", "DGMS DRILL PHASES", ColBlue);
        CanvasGroup c3 = BuildChip(strip, "ARCORE", "ANDROID 10+ DEVICE", ColGreen);

        introGroups.Add(arCard);
        introGroups.Add(vrCard);
        introGroups.Add(c1);
        introGroups.Add(c2);
        introGroups.Add(c3);
        foreach (CanvasGroup g in introGroups) g.alpha = 0f;
    }

    CanvasGroup BuildCard(Transform parent, string name, string tag, string title, string[] bullets,
                          string btnLabel, Color accent, Color btnColor,
                          UnityEngine.Events.UnityAction onClick, out Button btn)
    {
        RectTransform card = Plate(name, parent, ColPlate, false, true);
        LayoutElement le = card.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = 470f;
        le.flexibleHeight = 1f;          // share any spare height on tall phones
        CanvasGroup cg = card.gameObject.AddComponent<CanvasGroup>();
        Rivets(card, 14f);

        Image bar = Plain("Accent", card, accent);
        LeftStrip(bar.rectTransform, 0, 9, 22, 22);

        RectTransform chip = Plate("Tag", card, new Color(accent.r * 0.28f, accent.g * 0.28f, accent.b * 0.28f, 0.55f), false, false);
        Fixed(chip, new Vector2(0, 1), new Vector2(0, 1), new Vector2(36, -24), new Vector2(286, 40));
        TextMeshProUGUI tagT = Txt(chip, tag, FsMicro, accent, TextAlignmentOptions.Center, FontStyles.Bold, true, true);
        Fill(tagT.rectTransform, 10, 2, 10, 2);

        TextMeshProUGUI t = Txt(card, title, FsTitle, Color.white,
                                TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true, true);
        TopStrip(t.rectTransform, 36, 28, 76, 52);

        // Bulleted feature list: ASCII bullet, generous line spacing, wraps cleanly.
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < bullets.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append("<color=#FCC721>-</color>  ").Append(bullets[i]);
        }
        TextMeshProUGUI b = Txt(card, sb.ToString(), FsBody, ColText,
                                TextAlignmentOptions.TopLeft, FontStyles.Normal, false, false);
        b.lineSpacing = 16f;
        Fill(b.rectTransform, 36, 150, 28, 142);

        Image rule = Plain("CardRule", card, ColEdge);
        BottomStrip(rule.rectTransform, 36, 36, 140, 2);

        btn = MakeButton(card, "Btn_" + name, btnLabel, btnColor, onClick, FsButton);
        BottomStrip((RectTransform)btn.transform, 36, 36, 30, 94);
        return cg;
    }

    CanvasGroup BuildChip(Transform parent, string value, string label, Color accent)
    {
        RectTransform chip = Plate("Chip_" + label, parent, ColPlateDeep, false, false);
        CanvasGroup cg = chip.gameObject.AddComponent<CanvasGroup>();

        Image bar = Plain("ChipAccent", chip, accent);
        TopStrip(bar.rectTransform, 14, 14, 0, 4);

        TextMeshProUGUI v = Txt(chip, value, FsStat, accent, TextAlignmentOptions.Center, FontStyles.Bold, true, true);
        TopStrip(v.rectTransform, 8, 8, 24, 46);

        TextMeshProUGUI l = Txt(chip, label, FsMicro, ColMuted, TextAlignmentOptions.Top, FontStyles.Bold, false, false);
        TopStrip(l.rectTransform, 10, 10, 74, 48);
        return cg;
    }

    void BuildFooter(Transform parent)
    {
        RectTransform bar = Plate("Footer", parent, ColPlate, false, false);
        BottomStrip(bar, 20, 20, 12, 88);

        RawImage stripe = NewStripe("FooterStripe", bar, 10);
        TopStrip(stripe.rectTransform, 20, 20, 6, 10);

        TextMeshProUGUI left = Txt(bar, "STATION  " + stationName + "   |   OFFLINE-READY  |  ON-DEVICE RECOGNITION",
                                   FsMicro, ColMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Normal, true, true);
        Fill(left.rectTransform, 32, 6, 190, 20);

        TextMeshProUGUI right = Txt(bar, "BUILD v" + Application.version, FsLabel, ColBlue,
                                    TextAlignmentOptions.MidlineRight, FontStyles.Bold, false, true);
        Fixed(right.rectTransform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-32, -5), new Vector2(180, 36));
    }

    // =================================================================
    //  VR SCENARIO MODAL
    // =================================================================
    void BuildScenarioModal(Transform parent)
    {
        RectTransform root = NewRect("VRScenarioModal", parent);
        Fill(root, 0, 0, 0, 0);
        modal = root.gameObject;
        modalGroup = modal.AddComponent<CanvasGroup>();

        // dim layer, tap to close
        Image dim = Plain("Dim", root, new Color(0f, 0f, 0f, 0.80f));
        Fill(dim.rectTransform, 0, 0, 0, 0);
        dim.raycastTarget = true;
        Button dimBtn = dim.gameObject.AddComponent<Button>();
        dimBtn.transition = Selectable.Transition.None;
        dimBtn.targetGraphic = dim;
        dimBtn.onClick.AddListener(CloseModal);

        RectTransform panel = Plate("ModalPanel", root, ColPlateDeep, true, true);
        modalPanel = panel;
        modalPanel.anchorMin = new Vector2(0f, 0.5f);
        modalPanel.anchorMax = new Vector2(1f, 0.5f);
        modalPanel.offsetMin = new Vector2(32f, -520f);
        modalPanel.offsetMax = new Vector2(-32f, 520f);
        Rivets(panel, 16f);

        RawImage stripe = NewStripe("ModalStripe", panel, 12);
        TopStrip(stripe.rectTransform, 20, 20, 12, 12);

        TextMeshProUGUI title = Txt(panel, "SELECT VR HAZARD SCENARIO", FsH1, Color.white,
                                    TextAlignmentOptions.Center, FontStyles.Bold, true, true);
        TopStrip(title.rectTransform, 28, 28, 42, 52);
        TextMeshProUGUI sub = Txt(panel, "Choose a drill. Every decision is scored on this device.",
                                  FsLabel, ColMuted, TextAlignmentOptions.Center, FontStyles.Normal, false, true);
        TopStrip(sub.rectTransform, 28, 28, 100, 32);

        BuildScenarioRow(panel, 150, "SCENARIO 01: ROOF FALL & TOXIC GAS",
            "Roof sounding test, tremor, boulder cave-in, gas alarm, self-rescuer mask and blackout escape maze.",
            ColYellow, new Color(0.17f, 0.55f, 0.32f, 1f), "SELECT DEPLOYMENT", OpenModeModal, out s1Button, out s1Badge);

        BuildScenarioRow(panel, 470, "SCENARIO 02: CONTROLLED BLASTING",
            "Exploder circuit check, siren and clearance radius, key ignition and the misfire lockdown protocol.",
            ColBlue, ColBlueDim, "START SCENARIO 02", () => LoadScene(blastingSceneName), out s2Button, out s2Badge);

        Button cancel = MakeButton(panel, "BtnCloseModal", "CANCEL", new Color(0.38f, 0.13f, 0.13f, 1f), CloseModal, FsButton);
        BottomStrip((RectTransform)cancel.transform, 250, 250, 30, 86);

        modal.SetActive(false);
    }

    void BuildScenarioRow(Transform parent, float top, string title, string desc, Color accent, Color btnColor,
                          string btnLabel, UnityEngine.Events.UnityAction onClick, out Button btn, out TextMeshProUGUI badge)
    {
        RectTransform box = Plate("Scenario_" + top, parent, ColPlate2, false, false);
        TopStrip(box, 26, 26, top, 300);

        Image bar = Plain("Accent", box, accent);
        LeftStrip(bar.rectTransform, 0, 7, 18, 18);

        TextMeshProUGUI t = Txt(box, title, FsH2, accent, TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true, true);
        TopStrip(t.rectTransform, 28, 196, 16, 40);

        badge = Txt(box, "", FsMicro, ColGreen, TextAlignmentOptions.MidlineRight, FontStyles.Bold, true, true);
        Fixed(badge.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-22, -18), new Vector2(176, 34));

        TextMeshProUGUI d = Txt(box, desc, FsLabel, ColText, TextAlignmentOptions.TopLeft, FontStyles.Normal, false, false);
        d.lineSpacing = 8f;
        TopStrip(d.rectTransform, 28, 28, 64, 126);

        btn = MakeButton(box, "Btn_Start", btnLabel, btnColor, onClick, FsButton);
        BottomStrip((RectTransform)btn.transform, 26, 26, 22, 82);
    }

    // =================================================================
    //  DEPLOYMENT MODE MODAL (with / without AR marker)
    // =================================================================
    void BuildModeModal(Transform parent)
    {
        RectTransform root = NewRect("VRModeModal", parent);
        Fill(root, 0, 0, 0, 0);
        modeModal = root.gameObject;
        modeGroup = modeModal.AddComponent<CanvasGroup>();

        // dim layer, tap to close
        Image dim = Plain("Dim", root, new Color(0f, 0f, 0f, 0.80f));
        Fill(dim.rectTransform, 0, 0, 0, 0);
        dim.raycastTarget = true;
        Button dimBtn = dim.gameObject.AddComponent<Button>();
        dimBtn.transition = Selectable.Transition.None;
        dimBtn.targetGraphic = dim;
        dimBtn.onClick.AddListener(CloseModeModal);

        RectTransform panel = Plate("ModePanel", root, ColPlateDeep, true, true);
        modePanel = panel;
        modePanel.anchorMin = new Vector2(0f, 0.5f);
        modePanel.anchorMax = new Vector2(1f, 0.5f);
        modePanel.offsetMin = new Vector2(32f, -470f);
        modePanel.offsetMax = new Vector2(-32f, 470f);
        Rivets(panel, 16f);

        RawImage stripe = NewStripe("ModeStripe", panel, 12);
        TopStrip(stripe.rectTransform, 20, 20, 12, 12);

        TextMeshProUGUI title = Txt(panel, "CHOOSE DEPLOYMENT MODE", FsH1, Color.white,
                                    TextAlignmentOptions.Center, FontStyles.Bold, true, true);
        TopStrip(title.rectTransform, 28, 28, 42, 52);
        TextMeshProUGUI sub = Txt(panel, "Roof-fall drill: spawn in front of you, or anchor the mine to a printed floor marker.",
                                  FsLabel, ColMuted, TextAlignmentOptions.Center, FontStyles.Normal, false, false);
        TopStrip(sub.rectTransform, 28, 28, 100, 56);

        // Classic: no camera permission, mine spawns in front of the player.
        BuildModeRow(panel, 168, "START WITHOUT MARKER",
            "Classic mode. The mine loads in front of you. Use the joystick or gyroscope to look and move. No camera access needed.",
            ColBlue, ColBlueDim, StartWithoutMarker);

        // AR: point the phone camera at the printed marker; the trainee
        // physically walks the tunnel.
        BuildModeRow(panel, 468, "START WITH AR MARKER",
            "Point the camera at the printed MineAnchorMarker on the floor. The mine is anchored to the marker - walk around to explore the tunnel in your room.",
            ColYellow, new Color(0.52f, 0.40f, 0.09f, 1f), StartWithMarker);

        Button cancel = MakeButton(panel, "BtnCloseModeModal", "CANCEL", new Color(0.38f, 0.13f, 0.13f, 1f), CloseModeModal, FsButton);
        BottomStrip((RectTransform)cancel.transform, 250, 250, 30, 86);

        modeModal.SetActive(false);
    }

    void BuildModeRow(Transform parent, float top, string title, string desc, Color accent, Color btnColor,
                      UnityEngine.Events.UnityAction onClick)
    {
        RectTransform box = Plate("Mode_" + top, parent, ColPlate2, false, false);
        TopStrip(box, 26, 26, top, 284);

        Image bar = Plain("Accent", box, accent);
        LeftStrip(bar.rectTransform, 0, 7, 18, 18);

        TextMeshProUGUI t = Txt(box, title, FsH2, accent, TextAlignmentOptions.MidlineLeft, FontStyles.Bold, true, true);
        TopStrip(t.rectTransform, 28, 150, 16, 40);

        TextMeshProUGUI d = Txt(box, desc, FsLabel, ColText, TextAlignmentOptions.TopLeft, FontStyles.Normal, false, false);
        d.lineSpacing = 8f;
        TopStrip(d.rectTransform, 28, 28, 62, 112);

        Button btn = MakeButton(box, "Btn_ModeStart", "START", btnColor, onClick, FsButton);
        BottomStrip((RectTransform)btn.transform, 26, 26, 20, 80);
    }

    // Re-evaluate which scenes can actually be loaded each time the modal opens.
    void RefreshScenarioButtons()
    {
        bool vrOk = SceneAvailable(vrSceneName);
        bool blastOk = SceneAvailable(blastingSceneName);

        // Scenario 01 opens the mode dialog; its label only changes if the scene is missing.
        SetButton(s1Button, vrOk ? "SELECT DEPLOYMENT" : "SCENE NOT IN BUILD", vrOk);
        s1Badge.text = vrOk ? "AVAILABLE" : "UNAVAILABLE";
        s1Badge.color = vrOk ? ColGreen : ColRed;

        SetButton(s2Button, blastOk ? "START SCENARIO 02" : "IN DEVELOPMENT", blastOk);
        s2Badge.text = blastOk ? "AVAILABLE" : "PLANNED";
        s2Badge.color = blastOk ? ColGreen : ColMuted;
    }

    void SetButton(Button b, string label, bool enabled)
    {
        if (b == null) return;
        b.interactable = enabled;
        TextMeshProUGUI t = b.GetComponentInChildren<TextMeshProUGUI>();
        if (t != null) t.text = label;
    }

    // =================================================================
    //  TOAST + FADE OVERLAY
    // =================================================================
    void BuildToast(Transform parent)
    {
        RectTransform toast = Plate("Toast", parent, new Color(0.08f, 0.10f, 0.12f, 0.97f), false, false);
        BottomStrip(toast, 60, 60, 150, 88);
        toastGroup = toast.gameObject.AddComponent<CanvasGroup>();
        toastGroup.alpha = 0f;
        toastGroup.blocksRaycasts = false;
        toastGroup.interactable = false;

        Image edge = Plain("ToastEdge", toast, ColYellow);
        LeftStrip(edge.rectTransform, 0, 7, 12, 12);

        toastText = Txt(toast, "", FsBody, Color.white, TextAlignmentOptions.Center, FontStyles.Bold, true, false);
        Fill(toastText.rectTransform, 26, 6, 22, 6);
    }

    void BuildFadeOverlay(Transform parent)
    {
        Image fade = Plain("FadeOverlay", parent, new Color(0.012f, 0.016f, 0.020f, 1f));
        Fill(fade.rectTransform, 0, 0, 0, 0);
        fade.raycastTarget = true;
        fadeGroup = fade.gameObject.AddComponent<CanvasGroup>();
        fadeGroup.alpha = 1f;
        fadeGroup.blocksRaycasts = true;

        fadeLabel = Txt(fade.transform, "", FsH1, ColYellow, TextAlignmentOptions.Center, FontStyles.Bold, false, true);
        Fill(fadeLabel.rectTransform, 40, 0, 40, 0);
    }

    public void ShowToast(string message, float seconds = 2.2f)
    {
        if (toastGroup == null) return;
        if (toastCo != null) StopCoroutine(toastCo);
        toastCo = StartCoroutine(ToastRoutine(message, seconds));
    }

    IEnumerator ToastRoutine(string message, float seconds)
    {
        toastText.text = message;
        yield return FadeGroup(toastGroup, 1f, 0.15f);
        float t = 0f;
        while (t < seconds) { t += Time.unscaledDeltaTime; yield return null; }
        yield return FadeGroup(toastGroup, 0f, 0.3f);
        toastCo = null;
    }

    // =================================================================
    //  FLOW
    // =================================================================
    IEnumerator IntroRoutine()
    {
        yield return null; // let layout settle one frame
        StartCoroutine(FadeGroup(fadeGroup, 0f, 0.45f));
        fadeGroup.blocksRaycasts = false;
        for (int i = 0; i < introGroups.Count; i++)
            StartCoroutine(PopIn(introGroups[i], 0.12f + i * 0.09f));
    }

    public void OnStartARMode()
    {
        LoadScene(arSceneName);
    }

    public void OnVRModeClicked()
    {
        OpenModal();
    }

    public void OnCloseVRPopup()
    {
        CloseModeModal();
        CloseModal();
    }

    void OpenModal()
    {
        if (modal == null || loading) return;
        RefreshScenarioButtons();
        modal.SetActive(true);
        if (modalCo != null) StopCoroutine(modalCo);
        modalCo = StartCoroutine(ModalAnim(modalGroup, modalPanel, true));
    }

    void CloseModal()
    {
        if (modal == null || !modal.activeSelf) return;
        if (modalCo != null) StopCoroutine(modalCo);
        modalCo = StartCoroutine(ModalAnim(modalGroup, modalPanel, false));
    }

    void OpenModeModal()
    {
        if (modeModal == null || loading) return;
        modeModal.SetActive(true);
        if (modalCo != null) StopCoroutine(modalCo);
        modalCo = StartCoroutine(ModalAnim(modeGroup, modePanel, true));
    }

    void CloseModeModal()
    {
        if (modeModal == null || !modeModal.activeSelf) return;
        if (modalCo != null) StopCoroutine(modalCo);
        modalCo = StartCoroutine(ModalAnim(modeGroup, modePanel, false));
    }

    void StartWithoutMarker()
    {
        PlayerPrefs.SetInt(UseMarkerPrefKey, 0);
        PlayerPrefs.Save();
        LoadScene(vrSceneName);
    }

    void StartWithMarker()
    {
        PlayerPrefs.SetInt(UseMarkerPrefKey, 1);
        PlayerPrefs.Save();
        LoadScene(vrSceneName);
    }

    IEnumerator ModalAnim(CanvasGroup group, RectTransform panel, bool open)
    {
        float dur = 0.18f;
        float from = open ? 0f : group.alpha;
        float to = open ? 1f : 0f;
        group.interactable = open;
        for (float t = 0f; t < dur; t += Time.unscaledDeltaTime)
        {
            float k = Mathf.Clamp01(t / dur);
            float e = 1f - (1f - k) * (1f - k);
            group.alpha = Mathf.Lerp(from, to, e);
            float s = open ? Mathf.Lerp(0.92f, 1f, e) : Mathf.Lerp(1f, 0.96f, e);
            panel.localScale = new Vector3(s, s, 1f);
            yield return null;
        }
        group.alpha = to;
        panel.localScale = Vector3.one;
        if (!open)
        {
            // panel's parent is the modal root GameObject (VRScenarioModal or VRModeModal).
            if (panel != null && panel.parent != null) panel.parent.gameObject.SetActive(false);
        }
        modalCo = null;
    }

    bool SceneAvailable(string sceneName)
    {
        return !string.IsNullOrEmpty(sceneName) && Application.CanStreamedLevelBeLoaded(sceneName);
    }

    void LoadScene(string sceneName)
    {
        if (loading) return;
        if (!SceneAvailable(sceneName))
        {
            Debug.LogError("[MainMenu] Scene '" + sceneName + "' is not in Build Profiles.");
            ShowToast("Scene '" + sceneName + "' is not in Build Profiles.", 3f);
            return;
        }
        StartCoroutine(LoadRoutine(sceneName));
    }

    IEnumerator LoadRoutine(string sceneName)
    {
        loading = true;
        fadeGroup.blocksRaycasts = true;
        fadeLabel.text = "LOADING...";
        yield return FadeGroup(fadeGroup, 1f, 0.25f);

        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName);
        if (op == null)
        {
            loading = false;
            fadeLabel.text = "";
            yield return FadeGroup(fadeGroup, 0f, 0.2f);
            fadeGroup.blocksRaycasts = false;
            ShowToast("Could not load '" + sceneName + "'.", 3f);
            yield break;
        }
        while (!op.isDone) yield return null;
    }

    // =================================================================
    //  INPUT
    // =================================================================
    bool BackPressed()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        return kb != null && kb.escapeKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.Escape);
#else
        return false;
#endif
    }

    void HandleBackKey()
    {
        if (!BackPressed() || loading) return;

        // Deployment-mode dialog takes priority, then the scenario picker.
        if (modeModal != null && modeModal.activeSelf) { CloseModeModal(); return; }
        if (modal != null && modal.activeSelf) { CloseModal(); return; }

        if (Time.unscaledTime - lastBackTime < 2f)
        {
            Application.Quit();
            return;
        }
        lastBackTime = Time.unscaledTime;
        ShowToast("Press back again to exit");
    }

    // =================================================================
    //  LAYOUT HELPERS
    // =================================================================
    void ApplySafeArea()
    {
        lastSafe = Screen.safeArea;
        lastW = Screen.width;
        lastH = Screen.height;
        if (safeRT == null || lastW <= 0 || lastH <= 0) return;

        Vector2 min = lastSafe.position;
        Vector2 max = lastSafe.position + lastSafe.size;
        min.x /= lastW; min.y /= lastH;
        max.x /= lastW; max.y /= lastH;
        safeRT.anchorMin = min;
        safeRT.anchorMax = max;
        safeRT.offsetMin = Vector2.zero;
        safeRT.offsetMax = Vector2.zero;
    }

    // Cover-fit the background photo without stretching it, and keep the
    // blueprint grid at a constant real-world cell size.
    void FitBackground()
    {
        if (Screen.height <= 0 || Screen.width <= 0) return;

        if (gridOverlay != null)
            gridOverlay.uvRect = new Rect(0f, 0f, Screen.width / 128f, Screen.height / 128f);

        if (backgroundPhoto == null || backgroundTexture == null) return;
        float screenAspect = Screen.width / (float)Screen.height;
        float texAspect = backgroundTexture.width / (float)backgroundTexture.height;
        Rect uv = new Rect(0f, 0f, 1f, 1f);
        if (screenAspect < texAspect)
        {
            uv.width = screenAspect / texAspect;
            uv.x = (1f - uv.width) * 0.5f;
        }
        else
        {
            uv.height = texAspect / screenAspect;
            uv.y = (1f - uv.height) * 0.5f;
        }
        backgroundPhoto.uvRect = uv;
    }

    RectTransform NewRect(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    // stretch to parent with inward insets
    static void Fill(RectTransform rt, float l, float b, float r, float t)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(l, b);
        rt.offsetMax = new Vector2(-r, -t);
    }

    // full width strip hanging from the top
    static void TopStrip(RectTransform rt, float l, float r, float top, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.offsetMin = new Vector2(l, -top - h);
        rt.offsetMax = new Vector2(-r, -top);
    }

    // full width strip standing on the bottom
    static void BottomStrip(RectTransform rt, float l, float r, float bottom, float h)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.offsetMin = new Vector2(l, bottom);
        rt.offsetMax = new Vector2(-r, bottom + h);
    }

    // full height strip on the left edge
    static void LeftStrip(RectTransform rt, float l, float w, float top, float bottom)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.offsetMin = new Vector2(l, bottom);
        rt.offsetMax = new Vector2(l + w, -top);
    }

    // fixed size element pinned to an anchor point
    static void Fixed(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    // =================================================================
    //  WIDGET FACTORIES
    // =================================================================
    Image Plain(string name, Transform parent, Color color)
    {
        RectTransform rt = NewRect(name, parent);
        Image img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    Image NewImage(string name, Transform parent, Sprite sprite, Color color)
    {
        Image img = Plain(name, parent, color);
        img.sprite = sprite;
        return img;
    }

    Image Panel(string name, Transform parent, Color color, bool raycast)
    {
        Image img = Plain(name, parent, color);
        img.sprite = roundSprite;
        img.type = Image.Type.Sliced;
        img.raycastTarget = raycast;
        return img;
    }

    // Dark brushed-metal plate: rounded base + milled grain + machined
    // hairline edge (+ optional brighter top light-catch). This is the
    // single building block that gives the console its industrial look.
    RectTransform Plate(string name, Transform parent, Color baseColor, bool raycast, bool topHighlight)
    {
        Image img = Panel(name, parent, baseColor, raycast);
        RectTransform rt = img.rectTransform;

        RectTransform grain = NewRect("Grain", rt);
        Fill(grain, 5, 5, 5, 5);
        RawImage metal = grain.gameObject.AddComponent<RawImage>();
        metal.texture = brushedTex;
        metal.uvRect = new Rect(0f, 0f, 3f, 1f); // streaks run horizontally, tile across
        metal.color = new Color(1f, 1f, 1f, metalStrength);
        metal.raycastTarget = false;

        Image edge = NewImage("Edge", rt, outlineSprite, ColEdge);
        edge.type = Image.Type.Sliced;
        Fill(edge.rectTransform, 0, 0, 0, 0);

        if (topHighlight)
        {
            Image hi = Plain("TopLight", rt, ColEdgeHi);
            TopStrip(hi.rectTransform, 16, 16, 2, 2);
        }
        return rt;
    }

    // Four machined rivet heads, one per corner. Pure decoration, no cost.
    void Rivets(RectTransform plate, float inset)
    {
        Vector2[] anchors =
        {
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f)
        };
        for (int i = 0; i < anchors.Length; i++)
        {
            float sx = anchors[i].x < 0.5f ? inset : -inset;
            float sy = anchors[i].y < 0.5f ? inset : -inset;
            Image r = NewImage("Rivet", plate, circleSprite, new Color(1f, 1f, 1f, 0.16f));
            Fixed(r.rectTransform, anchors[i], new Vector2(0.5f, 0.5f), new Vector2(sx, sy), new Vector2(7f, 7f));
        }
    }

    RawImage NewStripe(string name, Transform parent, float height)
    {
        RectTransform rt = NewRect(name, parent);
        RawImage ri = rt.gameObject.AddComponent<RawImage>();
        ri.texture = stripeTex;
        ri.uvRect = new Rect(0f, 0f, 34f, height / 32f);
        ri.color = new Color(1f, 1f, 1f, 0.92f);
        ri.raycastTarget = false;
        return ri;
    }

    TextMeshProUGUI Txt(Transform parent, string text, float size, Color color,
                        TextAlignmentOptions align, FontStyles style, bool autoFit, bool noWrap = false)
    {
        RectTransform rt = NewRect("Text", parent);
        TextMeshProUGUI t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.fontStyle = style;
        t.raycastTarget = false;
        t.richText = true;
        t.textWrappingMode = noWrap ? TextWrappingModes.NoWrap : TextWrappingModes.Normal;
        t.overflowMode = TextOverflowModes.Ellipsis;
        if (autoFit)
        {
            // Tight range: shrink to fit a narrow plate, never to unreadable.
            t.enableAutoSizing = true;
            t.fontSizeMax = size;
            t.fontSizeMin = Mathf.Max(14f, size * 0.78f);
        }
        return t;
    }

    Button MakeButton(Transform parent, string name, string label, Color bg,
                      UnityEngine.Events.UnityAction onClick, float fontSize)
    {
        RectTransform rt = Plate(name, parent, bg, true, true);
        Image img = rt.GetComponent<Image>();
        Button btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.transition = Selectable.Transition.ColorTint;

        ColorBlock cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.0f, 1.0f, 1.0f, 1f);
        cb.pressedColor = new Color(0.68f, 0.68f, 0.68f, 1f);
        cb.selectedColor = Color.white;
        cb.disabledColor = new Color(0.42f, 0.42f, 0.42f, 0.70f);
        cb.colorMultiplier = 1f;
        cb.fadeDuration = 0.07f;
        btn.colors = cb;

        Navigation nav = btn.navigation;
        nav.mode = Navigation.Mode.None;
        btn.navigation = nav;

        if (onClick != null) btn.onClick.AddListener(onClick);

        TextMeshProUGUI t = Txt(rt, label, fontSize, Color.white, TextAlignmentOptions.Center, FontStyles.Bold, true, true);
        Fill(t.rectTransform, 16, 6, 16, 6);
        return btn;
    }

    // =================================================================
    //  ANIMATION
    // =================================================================
    IEnumerator FadeGroup(CanvasGroup g, float to, float dur)
    {
        float from = g.alpha;
        for (float t = 0f; t < dur; t += Time.unscaledDeltaTime)
        {
            g.alpha = Mathf.Lerp(from, to, t / dur);
            yield return null;
        }
        g.alpha = to;
    }

    IEnumerator PopIn(CanvasGroup g, float delay)
    {
        g.alpha = 0f;
        g.transform.localScale = Vector3.one * 0.94f;
        for (float w = 0f; w < delay; w += Time.unscaledDeltaTime) yield return null;

        const float dur = 0.35f;
        for (float t = 0f; t < dur; t += Time.unscaledDeltaTime)
        {
            float k = Mathf.Clamp01(t / dur);
            float e = 1f - (1f - k) * (1f - k) * (1f - k);
            g.alpha = e;
            float s = Mathf.Lerp(0.94f, 1f, e);
            g.transform.localScale = new Vector3(s, s, 1f);
            yield return null;
        }
        g.alpha = 1f;
        g.transform.localScale = Vector3.one;
    }

    // =================================================================
    //  PROCEDURAL ART (no image assets needed)
    // =================================================================
    // Signed distance to a rounded rectangle centred in a size x size box.
    static float RoundRectSdf(float x, float y, float size, float radius)
    {
        float half = size * 0.5f;
        float dx = Mathf.Max(Mathf.Abs(x - half) - (half - radius), 0f);
        float dy = Mathf.Max(Mathf.Abs(y - half) - (half - radius), 0f);
        float inside = Mathf.Min(Mathf.Max(Mathf.Abs(x - half) - (half - radius),
                                           Mathf.Abs(y - half) - (half - radius)), 0f);
        return Mathf.Sqrt(dx * dx + dy * dy) + inside - radius;
    }

    // Anti-aliased rounded rectangle. border > 0 makes it 9-sliceable.
    static Sprite MakeRoundSprite(int size, int radius, int border)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        Color32[] px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = RoundRectSdf(x + 0.5f, y + 0.5f, size, radius);
                byte a = (byte)(Mathf.Clamp01(0.5f - d) * 255f);
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        Vector4 b = new Vector4(border, border, border, border);
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, b);
    }

    // Crisp machined edge: a rounded-rectangle outline of the given thickness.
    static Sprite MakeOutlineSprite(int size, int radius, float thickness, int border)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        Color32[] px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = RoundRectSdf(x + 0.5f, y + 0.5f, size, radius);
                float a = Mathf.Clamp01(Mathf.Min(0.5f - d, d + thickness + 0.5f));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        Vector4 b = new Vector4(border, border, border, border);
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                             SpriteMeshType.FullRect, b);
    }

    // Deterministic 2D hash, 0..1 (same generator style as the drill scene).
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

    // Brushed stainless steel: horizontal mill marks, a few long scratches
    // and a soft top-to-bottom light fall-off. Tiles across X, so panels of
    // any width get a continuous grain and the gradient spans their height.
    static Texture2D MakeBrushedTexture(int w, int h)
    {
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;

        // One brush value per scan line keeps the marks perfectly horizontal.
        float[] line = new float[h];
        for (int y = 0; y < h; y++)
        {
            float a = Hash(0, y, 17);
            float b = Hash(0, y / 3, 29);
            float c = Hash(0, y / 11, 41);
            line[y] = (a * 0.5f + b * 0.3f + c * 0.2f) - 0.5f;   // -0.5 .. 0.5
        }

        Color32[] px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            // Light catches the top of a vertical plate.
            float grad = Mathf.Lerp(-0.07f, 0.09f, y / (float)(h - 1));
            bool scratch = Hash(1, y, 97) > 0.978f;
            for (int x = 0; x < w; x++)
            {
                float v = 0.34f + line[y] * 0.22f + grad;
                v += (Hash(x, y, 7) - 0.5f) * 0.045f;                      // fine grain
                if (scratch) v += 0.16f * (0.6f + 0.4f * Hash(x / 7, y, 3)); // mill scratch
                byte g = (byte)(Mathf.Clamp01(v) * 255f);
                px[y * w + x] = new Color32(g, g, (byte)Mathf.Min(255, g + 6), 255);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    // Faint monitoring-dashboard grid: 1 px lines on a 32 px cell, with a
    // brighter line every 4th cell. Tiles seamlessly.
    static Texture2D MakeGridTexture(int n)
    {
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        Color32[] px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                bool major = (x == 0 || y == 0);
                bool minor = (x % (n / 2) == 0 || y % (n / 2) == 0);
                byte a = major ? (byte)170 : minor ? (byte)70 : (byte)0;
                px[y * n + x] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    // Safety-yellow / black diagonal hazard stripes, tiles seamlessly.
    static Texture2D MakeStripeTexture()
    {
        const int n = 32;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        Color32 yellow = new Color32(252, 199, 33, 255);   // #FCC721
        Color32 dark = new Color32(20, 23, 28, 255);
        Color32[] px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                px[y * n + x] = (((x + y) / 8) % 2 == 0) ? yellow : dark;
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }
}
