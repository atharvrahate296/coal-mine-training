using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;
using UnityEngine.UI;

[RequireComponent(typeof(ARTrackedImageManager))]
public class ImageTargetHandler : MonoBehaviour
{
    [SerializeField]
    private GameObject infoPrefab;

    private ARTrackedImageManager trackedImageManager;
    private Dictionary<string, GameObject> spawnedObjects = new Dictionary<string, GameObject>();
    private Dictionary<string, bool> questionShown = new Dictionary<string, bool>();

    public static int score = 0;
    public static int totalAnswered = 0;
    public static HashSet<string> itemsSeen = new HashSet<string>();

    private class ItemData
    {
        public string info;
        public string question;
        public string optionA;
        public string optionB;
        public bool correctIsA;
    }

    private Dictionary<string, ItemData> items = new Dictionary<string, ItemData>()
    {
        { "HelmetImage", new ItemData {
            info = "SAFETY HELMET\n\nProtects against falling objects and head impact.",
            question = "When must you wear this?",
            optionA = "Before entering the mine", optionB = "Only during blasting", correctIsA = true } },

        { "MaskImage", new ItemData {
            info = "GAS MASK / RESPIRATOR\n\nProtects against toxic gases and dust inhalation.",
            question = "What must you check before use?",
            optionA = "Seal and filter", optionB = "Battery level", correctIsA = true } },

        { "DangerSignImage", new ItemData {
            info = "DANGER: TOXIC GAS\n\nIndicates hazardous gas in this zone.",
            question = "What should you do first?",
            optionA = "Enter quickly", optionB = "Check gas levels before entry", correctIsA = false } },

        { "RoofSignImage", new ItemData {
            info = "ROOF INSTABILITY WARNING\n\nRisk of rock or roof collapse.",
            question = "You see loose rock. What next?",
            optionA = "Ignore and continue work", optionB = "Report it immediately", correctIsA = false } },

        { "EscapeRouteImage", new ItemData {
            info = "ESCAPE ROUTE\n\nMarks the nearest emergency exit path.",
            question = "When should you learn this route?",
            optionA = "Before starting work", optionB = "Only during an emergency", correctIsA = true } },

        { "GasDetectorImage", new ItemData {
            info = "MULTI GAS DETECTOR\n\nMeasures methane, carbon monoxide and oxygen levels.",
            question = "The alarm sounds. What do you do?",
            optionA = "Evacuate immediately", optionB = "Wait and check again later", correctIsA = true } },
    };

    void Awake()
    {
        trackedImageManager = GetComponent<ARTrackedImageManager>();
    }

    void OnEnable()
    {
        trackedImageManager.trackablesChanged.AddListener(OnChanged);
    }

    void OnDisable()
    {
        trackedImageManager.trackablesChanged.RemoveListener(OnChanged);
    }

    void OnChanged(ARTrackablesChangedEventArgs<ARTrackedImage> eventArgs)
    {
        foreach (var trackedImage in eventArgs.added) SpawnOrUpdate(trackedImage);
        foreach (var trackedImage in eventArgs.updated) SpawnOrUpdate(trackedImage);

        foreach (var pair in eventArgs.removed)
        {
            var trackedImage = pair.Value;
            string name = trackedImage.referenceImage.name;
            if (spawnedObjects.ContainsKey(name))
            {
                Destroy(spawnedObjects[name]);
                spawnedObjects.Remove(name);
            }
        }
    }

    void SpawnOrUpdate(ARTrackedImage trackedImage)
    {
        string imageName = trackedImage.referenceImage.name;
        if (!items.ContainsKey(imageName)) return;

        if (!spawnedObjects.ContainsKey(imageName))
        {
            GameObject newObject = Instantiate(infoPrefab, trackedImage.transform.position, trackedImage.transform.rotation);
            spawnedObjects[imageName] = newObject;
            questionShown[imageName] = false;
            itemsSeen.Add(imageName);

            TextMeshProUGUI textComponent = newObject.GetComponentInChildren<TextMeshProUGUI>();
            if (textComponent != null) textComponent.text = items[imageName].info;

            StartCoroutine(RevealQuestionAfterDelay(imageName, newObject, 2.5f));
        }

        GameObject obj = spawnedObjects[imageName];
        Vector3 offsetPosition = trackedImage.transform.position + trackedImage.transform.up * 0.1f;
        obj.transform.position = offsetPosition;

        if (Camera.main != null)
            obj.transform.rotation = Quaternion.LookRotation(obj.transform.position - Camera.main.transform.position);

        obj.SetActive(trackedImage.trackingState == TrackingState.Tracking);
    }

    System.Collections.IEnumerator RevealQuestionAfterDelay(string imageName, GameObject panelObject, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (panelObject == null || questionShown[imageName]) yield break;

        ItemData data = items[imageName];
        TextMeshProUGUI textComponent = panelObject.GetComponentInChildren<TextMeshProUGUI>();
        if (textComponent != null) textComponent.text = data.question;

        Button[] buttons = panelObject.GetComponentsInChildren<Button>(true);
        foreach (Button b in buttons)
        {
            b.gameObject.SetActive(true);
            TextMeshProUGUI btnText = b.GetComponentInChildren<TextMeshProUGUI>();
            bool isA = b.name == "AnswerButtonA";
            if (btnText != null) btnText.text = isA ? data.optionA : data.optionB;

            b.onClick.RemoveAllListeners();
            bool correct = isA ? data.correctIsA : !data.correctIsA;
            b.onClick.AddListener(() => OnAnswerSelected(panelObject, correct));
        }

        questionShown[imageName] = true;
    }

    void OnAnswerSelected(GameObject panelObject, bool correct)
    {
        totalAnswered++;
        if (correct) score++;

        TextMeshProUGUI textComponent = panelObject.GetComponentInChildren<TextMeshProUGUI>();
        if (textComponent != null)
            textComponent.text = correct ? "Correct!" : "Not quite. Review this item again.";

        Button[] buttons = panelObject.GetComponentsInChildren<Button>(true);
        foreach (Button b in buttons) b.gameObject.SetActive(false);
    }
}