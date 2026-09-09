using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

#if UNITY_ANALYTICS
using UnityEngine.Analytics;
#endif

/// <summary>
/// State pushed on the GameManager during the Loadout, when player select player, theme and accessories
/// Take care of init the UI, load all the data used for it etc.
/// </summary>
public class LoadoutState : AState
{
    public Canvas inventoryCanvas;

    [Header("Char UI")]
    public Text charNameDisplay;
	public RectTransform charSelect;
	public Transform charPosition;

	[Header("Theme UI")]
	public Text themeNameDisplay;
	public RectTransform themeSelect;
	public Image themeIcon;

	[Header("PowerUp UI")]
	public RectTransform powerupSelect;
	public Image powerupIcon;
	public Text powerupCount;
    public Sprite noItemIcon;

	[Header("Accessory UI")]
    public RectTransform accessoriesSelector;
    public Text accesoryNameDisplay;
	public Image accessoryIconDisplay;

	[Header("Other Data")]
	public Leaderboard leaderboard;
    public MissionUI missionPopup;
	public Button runButton;

    public GameObject tutorialBlocker;
    public GameObject tutorialPrompt;

	public MeshFilter skyMeshFilter;
    public MeshFilter UIGroundFilter;

	public AudioClip menuTheme;


    [Header("Prefabs")]
    public ConsumableIcon consumableIcon;

    Consumable.ConsumableType m_PowerupToUse = Consumable.ConsumableType.NONE;

    protected GameObject m_Character;
    protected List<int> m_OwnedAccesories = new List<int>();
    protected int m_UsedAccessory = -1;
	protected int m_UsedPowerupIndex;
    protected bool m_IsLoadingCharacter;

	protected Modifier m_CurrentModifier = new Modifier();

    const float k_HoldToStartDuration = 3.0f;
    protected Image m_HoldProgress;
    protected Text m_HoldStatus;
    protected RectTransform m_HoldVisual;
    protected Sprite m_HoldCircleSprite;
    protected float m_HoldStartTimer;
    protected bool m_CanStart;
    protected bool m_StartTriggered;

    protected const float k_CharacterRotationSpeed = 45f;
    protected const string k_ShopSceneName = "shop";
    protected const float k_OwnedAccessoriesCharacterOffset = -0.1f;
    protected int k_UILayer;
    protected readonly Quaternion k_FlippedYAxisRotation = Quaternion.Euler (0f, 180f, 0f);

    public override void Enter(AState from)
    {
        if (tutorialBlocker != null) tutorialBlocker.SetActive(false);
        if (tutorialPrompt != null) tutorialPrompt.SetActive(false);

        inventoryCanvas.gameObject.SetActive(true);
        if (missionPopup != null) missionPopup.gameObject.SetActive(false);

        SetupGameTitle();
        SetupKinectStartInstruction();
        SetupHoldToStartUI();
        SetupDefaultLeaderboard();
        if (themeNameDisplay != null) themeNameDisplay.text = "";

        k_UILayer = LayerMask.NameToLayer("UI");

        skyMeshFilter.gameObject.SetActive(true);
        UIGroundFilter.gameObject.SetActive(true);

        // Reseting the global blinking value. Can happen if the game unexpectedly exited while still blinking
        Shader.SetGlobalFloat("_BlinkingValue", 0.0f);

        if (MusicPlayer.instance.GetStem(0) != menuTheme)
		{
            MusicPlayer.instance.SetStem(0, menuTheme);
            StartCoroutine(MusicPlayer.instance.RestartAllStems());
        }

        m_CanStart = false;
        m_StartTriggered = false;
        ResetHoldToStart("CHARGEMENT...");

        if(m_PowerupToUse != Consumable.ConsumableType.NONE)
        {
            //if we come back from a run and we don't have any more of the powerup we wanted to use, we reset the powerup to use to NONE
            if (!PlayerData.instance.consumables.ContainsKey(m_PowerupToUse) || PlayerData.instance.consumables[m_PowerupToUse] == 0)
                m_PowerupToUse = Consumable.ConsumableType.NONE;
        }

        Refresh();
    }

    void SetupGameTitle()
    {
        if (charNameDisplay == null)
            return;

        charNameDisplay.enabled = true;
        charNameDisplay.color = Color.white;
        charNameDisplay.text = "CHABWAY\nCHURFER";

        Transform oldLogo = charNameDisplay.transform.Find("GameLogo");
        if (oldLogo != null)
            oldLogo.gameObject.SetActive(false);
    }

    void SetupKinectStartInstruction()
    {
        if (runButton == null)
            return;

        Transform existingInstruction = runButton.transform.Find("KinectStartInstruction");
        if (existingInstruction != null)
            existingInstruction.gameObject.SetActive(false);
    }

    void SetupHoldToStartUI()
    {
        if (runButton == null)
            return;

        // Keep the old button only as a layout container so scene references stay valid.
        runButton.interactable = false;
        runButton.transition = Selectable.Transition.None;

        Transform existing = runButton.transform.Find("HoldToStartUI");

        Image[] oldImages = runButton.GetComponentsInChildren<Image>(true);
        for (int i = 0; i < oldImages.Length; ++i)
        {
            if (existing != null && oldImages[i].transform.IsChildOf(existing))
                continue;

            oldImages[i].enabled = false;
            oldImages[i].raycastTarget = false;
        }

        Text[] oldLabels = runButton.GetComponentsInChildren<Text>(true);
        Font menuFont = charNameDisplay != null ? charNameDisplay.font : null;
        for (int i = 0; i < oldLabels.Length; ++i)
        {
            if (oldLabels[i].gameObject.name == "KinectStartInstruction" ||
                (existing != null && oldLabels[i].transform.IsChildOf(existing)))
                continue;

            if (menuFont == null)
                menuFont = oldLabels[i].font;
            oldLabels[i].gameObject.SetActive(false);
        }

        GameObject root;
        if (existing != null)
        {
            root = existing.gameObject;
            root.SetActive(true);
            m_HoldVisual = root.GetComponent<RectTransform>();
            m_HoldProgress = root.transform.Find("Progress").GetComponent<Image>();
            m_HoldStatus = root.transform.Find("Status").GetComponent<Text>();
            Image[] holdImages = root.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < holdImages.Length; ++i)
                holdImages[i].enabled = true;
            Text[] holdLabels = root.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < holdLabels.Length; ++i)
                holdLabels[i].gameObject.SetActive(true);
            return;
        }

        if (m_HoldCircleSprite == null)
            m_HoldCircleSprite = CreateCircleSprite();

        root = new GameObject("HoldToStartUI", typeof(RectTransform));
        root.transform.SetParent(runButton.transform, false);
        m_HoldVisual = root.GetComponent<RectTransform>();
        m_HoldVisual.anchorMin = new Vector2(0.5f, 0.5f);
        m_HoldVisual.anchorMax = new Vector2(0.5f, 0.5f);
        m_HoldVisual.pivot = new Vector2(0.5f, 0.5f);
        m_HoldVisual.anchoredPosition = new Vector2(0.0f, 4.0f);
        m_HoldVisual.sizeDelta = new Vector2(220.0f, 220.0f);

        Image border = CreateCircleImage(root.transform, "Border", new Vector2(220.0f, 220.0f), Color.white);
        border.raycastTarget = false;

        m_HoldProgress = CreateCircleImage(root.transform, "Progress", new Vector2(202.0f, 202.0f), new Color(1.0f, 0.53f, 0.0f, 1.0f));
        m_HoldProgress.type = Image.Type.Filled;
        m_HoldProgress.fillMethod = Image.FillMethod.Radial360;
        m_HoldProgress.fillOrigin = (int)Image.Origin360.Top;
        m_HoldProgress.fillClockwise = true;
        m_HoldProgress.fillAmount = 0.0f;
        m_HoldProgress.raycastTarget = false;

        Image center = CreateCircleImage(root.transform, "Center", new Vector2(162.0f, 162.0f), new Color(0.23f, 0.25f, 0.34f, 1.0f));
        center.raycastTarget = false;

        Text keyText = CreateHoldText(root.transform, "Key", menuFont, 82, TextAnchor.MiddleCenter);
        keyText.text = "";
        keyText.color = Color.white;
        keyText.rectTransform.anchorMin = Vector2.zero;
        keyText.rectTransform.anchorMax = Vector2.one;
        keyText.rectTransform.offsetMin = Vector2.zero;
        keyText.rectTransform.offsetMax = Vector2.zero;
        Outline keyOutline = keyText.gameObject.AddComponent<Outline>();
        keyOutline.effectColor = new Color(0.08f, 0.08f, 0.12f, 0.9f);
        keyOutline.effectDistance = new Vector2(3.0f, -3.0f);

        m_HoldStatus = CreateHoldText(root.transform, "Status", menuFont, 36, TextAnchor.MiddleCenter);
        RectTransform statusRect = m_HoldStatus.rectTransform;
        statusRect.anchorMin = new Vector2(0.5f, 0.0f);
        statusRect.anchorMax = new Vector2(0.5f, 0.0f);
        statusRect.pivot = new Vector2(0.5f, 1.0f);
        statusRect.anchoredPosition = new Vector2(0.0f, -22.0f);
        statusRect.sizeDelta = new Vector2(540.0f, 66.0f);
        m_HoldStatus.color = Color.white;
        Outline statusOutline = m_HoldStatus.gameObject.AddComponent<Outline>();
        statusOutline.effectColor = new Color(0.08f, 0.08f, 0.12f, 0.9f);
        statusOutline.effectDistance = new Vector2(2.0f, -2.0f);
    }

    Image CreateCircleImage(Transform parent, string objectName, Vector2 size, Color color)
    {
        GameObject circleObject = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        circleObject.transform.SetParent(parent, false);
        RectTransform rect = circleObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = size;

        Image image = circleObject.GetComponent<Image>();
        image.sprite = m_HoldCircleSprite;
        image.color = color;
        return image;
    }

    Text CreateHoldText(Transform parent, string objectName, Font font, int fontSize, TextAnchor alignment)
    {
        GameObject textObject = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(parent, false);
        Text text = textObject.GetComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = Mathf.Max(14, fontSize / 2);
        text.resizeTextMaxSize = fontSize;
        text.alignment = alignment;
        text.raycastTarget = false;
        return text;
    }

    Sprite CreateCircleSprite()
    {
        const int size = 128;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = "HoldToStartCircle";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        Color32[] pixels = new Color32[size * size];
        float center = (size - 1) * 0.5f;
        float radius = center - 1.0f;
        Vector2 circleCenter = new Vector2(center, center);
        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                float distance = Vector2.Distance(new Vector2(x, y), circleCenter);
                byte alpha = (byte)(Mathf.Clamp01(radius + 0.75f - distance) * 255.0f);
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100.0f);
    }

    void ResetHoldToStart(string status)
    {
        m_HoldStartTimer = 0.0f;
        if (m_HoldProgress != null)
            m_HoldProgress.fillAmount = 0.0f;
        if (m_HoldStatus != null)
            m_HoldStatus.text = status;
        if (m_HoldVisual != null)
            m_HoldVisual.localScale = Vector3.one;
    }

    void UpdateHoldToStart()
    {
        if (!m_CanStart || m_StartTriggered)
            return;

        if (!Input.GetKey(KeyCode.S))
        {
            ResetHoldToStart("AU CENTRE, MAINTENEZ S 3 SEC");
            return;
        }

        m_HoldStartTimer += Time.unscaledDeltaTime;
        float progress = Mathf.Clamp01(m_HoldStartTimer / k_HoldToStartDuration);
        if (m_HoldProgress != null)
            m_HoldProgress.fillAmount = progress;

        float remaining = Mathf.Max(0.0f, k_HoldToStartDuration - m_HoldStartTimer);
        if (m_HoldStatus != null)
            m_HoldStatus.text = remaining > 0.05f ? remaining.ToString("0.0") + " SEC" : "LANCEMENT !";

        if (m_HoldVisual != null)
        {
            float pulse = 1.0f + Mathf.Sin(Time.unscaledTime * 9.0f) * 0.025f;
            m_HoldVisual.localScale = Vector3.one * pulse;
        }

        if (m_HoldStartTimer >= k_HoldToStartDuration)
        {
            m_StartTriggered = true;
            if (m_HoldStatus != null)
                m_HoldStatus.text = "LANCEMENT !";
            StartGame();
        }
    }

    void SetupDefaultLeaderboard()
    {
        if (leaderboard == null)
            return;

        // The leaderboard is permanently visible in the loadout, so its old opener is unnecessary.
        Transform openButton = inventoryCanvas.transform.Find("OpenLeaderboard");
        if (openButton != null)
            openButton.gameObject.SetActive(false);

        leaderboard.gameObject.SetActive(true);
        leaderboard.transform.localScale = Vector3.one;
        leaderboard.displayPlayer = false;
        leaderboard.forcePlayerDisplay = false;

        if (CultCatNameGenerator.ReplaceLegacyNames(PlayerData.instance))
            PlayerData.instance.Save();

        // Remove the old full-screen dimmer; only the side panel remains visible.
        Image fullScreenDimmer = leaderboard.GetComponent<Image>();
        if (fullScreenDimmer != null)
        {
            fullScreenDimmer.enabled = false;
            fullScreenDimmer.raycastTarget = false;
        }

        if (leaderboard.transform.childCount > 0)
        {
            RectTransform panel = leaderboard.transform.GetChild(0) as RectTransform;
            if (panel != null)
            {
                panel.anchorMin = new Vector2(1.0f, 0.5f);
                panel.anchorMax = new Vector2(1.0f, 0.5f);
                panel.pivot = new Vector2(1.0f, 0.5f);
                panel.anchoredPosition = new Vector2(-18.0f, 0.0f);

                // Keep the leaderboard's native 580x780 layout so its fixed-size rows
                // remain inside the frame, then scale the complete panel uniformly.
                panel.sizeDelta = new Vector2(580.0f, 780.0f);
                panel.localScale = Vector3.one * 0.62f;

                // The panel stays open by default, therefore its close button is also removed.
                Button[] panelButtons = panel.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < panelButtons.Length; ++i)
                    panelButtons[i].gameObject.SetActive(false);
            }
        }

        leaderboard.Populate();
    }

    public override void Exit(AState to)
    {
        if (missionPopup != null) missionPopup.gameObject.SetActive(false);
        if (leaderboard != null) leaderboard.Close();
        inventoryCanvas.gameObject.SetActive(false);

        if (m_Character != null) Addressables.ReleaseInstance(m_Character);

        GameState gs = to as GameState;

        skyMeshFilter.gameObject.SetActive(false);
        UIGroundFilter.gameObject.SetActive(false);

        if (gs != null)
        {
			gs.currentModifier = m_CurrentModifier;
			
            // We reset the modifier to a default one, for next run (if a new modifier is applied, it will replace this default one before the run starts)
			m_CurrentModifier = new Modifier();

			if (m_PowerupToUse != Consumable.ConsumableType.NONE)
			{
				PlayerData.instance.Consume(m_PowerupToUse);
                Consumable inv = Instantiate(ConsumableDatabase.GetConsumbale(m_PowerupToUse));
                inv.gameObject.SetActive(false);
                gs.trackManager.characterController.inventory = inv;
            }
        }
    }

    public void Refresh()
    {
		PopulatePowerup();

        StartCoroutine(PopulateCharacters());
        StartCoroutine(PopulateTheme());
    }

    public override string GetName()
    {
        return "Loadout";
    }

    public override void Tick()
    {
        if (!m_CanStart)
        {
            bool interactable = ThemeDatabase.loaded && CharacterDatabase.loaded;
            if(interactable)
            {
                m_CanStart = true;
                ResetHoldToStart("AU CENTRE, MAINTENEZ S 3 SEC");

                //we can always enabled, as the parent will be disabled if tutorial is already done
                tutorialPrompt.SetActive(true);
            }
        }

        UpdateHoldToStart();

        if(m_Character != null)
        {
            m_Character.transform.Rotate(0, k_CharacterRotationSpeed * Time.deltaTime, 0, Space.Self);
        }

		if (charSelect != null) charSelect.gameObject.SetActive(false);
		if (themeSelect != null) themeSelect.gameObject.SetActive(false);
    }

	public void GoToStore()
	{
        // Disabled for simple demo
	}

    public void ChangeCharacter(int dir)
    {
        // Disabled for simple demo
    }

    public void ChangeAccessory(int dir)
    {
        // Disabled for simple demo
    }

    public void ChangeTheme(int dir)
    {
        // Disabled for simple demo
    }

    public IEnumerator PopulateTheme()
    {
        ThemeData t = null;

        while (t == null)
        {
            t = ThemeDatabase.GetThemeData(PlayerData.instance.themes[PlayerData.instance.usedTheme]);
            yield return null;
        }

        if (themeNameDisplay != null) themeNameDisplay.text = t.themeName;
		if (themeIcon != null) themeIcon.sprite = t.themeIcon;

		if (skyMeshFilter != null) skyMeshFilter.sharedMesh = t.skyMesh;
        if (UIGroundFilter != null) UIGroundFilter.sharedMesh = t.UIGroundMesh;
	}

    public IEnumerator PopulateCharacters()
    {
		if (accessoriesSelector != null) accessoriesSelector.gameObject.SetActive(false);
        PlayerData.instance.usedAccessory = -1;
        m_UsedAccessory = -1;

        if (!m_IsLoadingCharacter)
        {
            m_IsLoadingCharacter = true;
            GameObject newChar = null;
            while (newChar == null)
            {
                Character c = CharacterDatabase.GetCharacter(PlayerData.instance.characters[PlayerData.instance.usedCharacter]);

                if (c != null)
                {
                    m_OwnedAccesories.Clear();

                    Vector3 pos = charPosition.transform.position;
                    pos.x = 0.0f;
                    charPosition.transform.position = pos;

                    if (accessoriesSelector != null) accessoriesSelector.gameObject.SetActive(false);

                    AsyncOperationHandle op = Addressables.InstantiateAsync(c.characterName);
                    yield return op;
                    if (op.Result == null || !(op.Result is GameObject))
                    {
                        Debug.LogWarning(string.Format("Unable to load character {0}.", c.characterName));
                        yield break;
                    }
                    newChar = op.Result as GameObject;
                    Helpers.SetRendererLayerRecursive(newChar, k_UILayer);
					newChar.transform.SetParent(charPosition, false);
                    newChar.transform.rotation = k_FlippedYAxisRotation;

                    if (m_Character != null)
                        Addressables.ReleaseInstance(m_Character);

                    m_Character = newChar;
                    // The menu header is now the game logo, not the selected character name.

                    m_Character.transform.localPosition = Vector3.right * 1000;
                    //animator will take a frame to initialize, during which the character will be in a T-pose.
                    //So we move the character off screen, wait that initialised frame, then move the character back in place.
                    //That avoid an ugly "T-pose" flash time
                    yield return new WaitForEndOfFrame();
                    m_Character.transform.localPosition = Vector3.zero;

                    SetupAccessory();
                }
                else
                    yield return new WaitForSeconds(1.0f);
            }
            m_IsLoadingCharacter = false;
        }
	}

    void SetupAccessory()
    {
        Character c = m_Character != null ? m_Character.GetComponent<Character>() : null;
        if (c != null)
        {
            c.SetupAccesory(PlayerData.instance.usedAccessory);
        }

        if (accesoryNameDisplay != null)
        {
            accesoryNameDisplay.text = "None";
        }
        if (accessoryIconDisplay != null)
        {
            accessoryIconDisplay.enabled = false;
        }
    }

	void PopulatePowerup()
	{
		powerupIcon.gameObject.SetActive(true);

        if (PlayerData.instance.consumables.Count > 0)
        {
            Consumable c = ConsumableDatabase.GetConsumbale(m_PowerupToUse);

            powerupSelect.gameObject.SetActive(true);
            if (c != null)
            {
                powerupIcon.sprite = c.icon;
                powerupCount.text = PlayerData.instance.consumables[m_PowerupToUse].ToString();
            }
            else
            {
                powerupIcon.sprite = noItemIcon;
                powerupCount.text = "";
            }
        }
        else
        {
            powerupSelect.gameObject.SetActive(false);
        }
	}

	public void ChangeConsumable(int dir)
	{
		bool found = false;
		do
		{
			m_UsedPowerupIndex += dir;
			if(m_UsedPowerupIndex >= (int)Consumable.ConsumableType.MAX_COUNT)
			{
				m_UsedPowerupIndex = 0; 
			}
			else if(m_UsedPowerupIndex < 0)
			{
				m_UsedPowerupIndex = (int)Consumable.ConsumableType.MAX_COUNT - 1;
			}

			int count = 0;
			if(PlayerData.instance.consumables.TryGetValue((Consumable.ConsumableType)m_UsedPowerupIndex, out count) && count > 0)
			{
				found = true;
			}

		} while (m_UsedPowerupIndex != 0 && !found);

		m_PowerupToUse = (Consumable.ConsumableType)m_UsedPowerupIndex;
		PopulatePowerup();
	}

	public void UnequipPowerup()
	{
		m_PowerupToUse = Consumable.ConsumableType.NONE;
	}
	

	public void SetModifier(Modifier modifier)
	{
		m_CurrentModifier = modifier;
	}

    public void StartGame()
    {
        PlayerData.instance.tutorialDone = false;
        manager.SwitchState("Game");
    }

	public void Openleaderboard()
	{
		leaderboard.displayPlayer = false;
		leaderboard.forcePlayerDisplay = false;
		leaderboard.Open();
    }
}
