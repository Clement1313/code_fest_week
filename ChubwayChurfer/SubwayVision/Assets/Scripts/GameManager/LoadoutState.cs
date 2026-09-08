using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;

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
	public Sprite gameLogoSprite;
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

    protected const float k_CharacterRotationSpeed = 45f;
    protected const float k_OwnedAccessoriesCharacterOffset = -0.1f;
    protected int k_UILayer;
    protected readonly Quaternion k_FlippedYAxisRotation = Quaternion.Euler (0f, 180f, 0f);

    [Header("Ready Screen (Hold to start)")]
    [Tooltip("Keyboard key used to simulate the 'crouch' gesture while debugging without the Kinect.")]
    public KeyCode debugStartKey = KeyCode.S;
    [Tooltip("Total hold time before the run actually starts.")]
    public float holdDurationToStart = 4f;
    [Tooltip("Number of dots shown filling up. The last dot fills before holdDurationToStart is reached, then holds for the remaining time as a short pause before the run starts.")]
    public int dotsToShow = 3;

    protected const string k_GameTitle = "ChubwayChurfer";
    protected const string k_FallbackStartSceneName = "start";

    protected bool m_ReadyScreenBuilt;
    protected bool m_ReadyToStart;
    protected float m_HoldTimer;
    protected Text m_HoldInstructionText;
    protected Button m_DebugFallbackButton;

    public override void Enter(AState from)
    {
        if (tutorialBlocker != null) tutorialBlocker.SetActive(false);
        if (tutorialPrompt != null) tutorialPrompt.SetActive(false);

        inventoryCanvas.gameObject.SetActive(true);
        if (missionPopup != null) missionPopup.gameObject.SetActive(false);

        SetupGameLogo();
        if (themeNameDisplay != null) themeNameDisplay.text = "";

        SetupReadyScreenUI();
        m_ReadyToStart = false;
        m_HoldTimer = 0f;

        if (leaderboard != null)
        {
            leaderboard.displayPlayer = false;
            leaderboard.forcePlayerDisplay = false;
            leaderboard.Open();

            // The leaderboard now stays open permanently as a plain side panel (no mouse to
            // click anything with anyway once Kinect drives the game): hide its close button...
            Transform closeButtons = leaderboard.transform.Find("Background/Buttons");
            if (closeButtons != null) closeButtons.gameObject.SetActive(false);

            // ...and the separate "open leaderboard" button elsewhere on the screen, now
            // redundant since the leaderboard is always visible.
            GameObject openLeaderboardButton = GameObject.Find("OpenLeaderboard");
            if (openLeaderboardButton != null) openLeaderboardButton.SetActive(false);

            StyleLeaderboardText();
        }

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

        runButton.gameObject.SetActive(false);
        if (m_HoldInstructionText != null) m_HoldInstructionText.gameObject.SetActive(false);

        if(m_PowerupToUse != Consumable.ConsumableType.NONE)
        {
            //if we come back from a run and we don't have any more of the powerup we wanted to use, we reset the powerup to use to NONE
            if (!PlayerData.instance.consumables.ContainsKey(m_PowerupToUse) || PlayerData.instance.consumables[m_PowerupToUse] == 0)
                m_PowerupToUse = Consumable.ConsumableType.NONE;
        }

        Refresh();
    }

    void SetupGameLogo()
    {
        if (charNameDisplay == null)
            return;

        // The character name used to be written here (for example "Trash Cat").
        // We now show the game title instead.
        charNameDisplay.text = k_GameTitle;
        charNameDisplay.enabled = true;

        // Pin the title to a full-width band at the very top of the screen, so it never
        // overlaps the leaderboard side panel regardless of its original scene position.
        RectTransform titleRect = charNameDisplay.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 0.88f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 0.5f);
        titleRect.anchoredPosition = Vector2.zero;
        titleRect.sizeDelta = Vector2.zero;
    }

    /// <summary>
    /// Builds (once) the elements of the new "ready" screen: the lane legend, the
    /// hold-to-start instruction label, and a near-invisible fallback button (bottom-right
    /// corner) that escapes to the old mouse-driven Start scene if the gesture/key never
    /// registers (Kinect issue, etc.).
    /// </summary>
    void SetupReadyScreenUI()
    {
        if (m_ReadyScreenBuilt)
            return;
        m_ReadyScreenBuilt = true;

        if (charNameDisplay != null)
        {
            // Hold-to-start instruction/progress label, shown once loading is done.
            m_HoldInstructionText = Instantiate(charNameDisplay, inventoryCanvas.transform, false);
            m_HoldInstructionText.name = "HoldToStartLabel";
            m_HoldInstructionText.enabled = true;
            m_HoldInstructionText.fontSize = Mathf.Max(48, charNameDisplay.fontSize);
            m_HoldInstructionText.alignment = TextAnchor.MiddleCenter;
            m_HoldInstructionText.supportRichText = true;
            m_HoldInstructionText.color = Color.white;
            m_HoldInstructionText.horizontalOverflow = HorizontalWrapMode.Overflow;
            m_HoldInstructionText.verticalOverflow = VerticalWrapMode.Overflow;
            m_HoldInstructionText.text = "";
            m_HoldInstructionText.gameObject.SetActive(false);

            // Centered on screen, a bit below the middle.
            RectTransform holdRect = m_HoldInstructionText.rectTransform;
            holdRect.anchorMin = new Vector2(0.5f, 0.5f);
            holdRect.anchorMax = new Vector2(0.5f, 0.5f);
            holdRect.pivot = new Vector2(0.5f, 0.5f);
            holdRect.anchoredPosition = new Vector2(0f, -120f);
            holdRect.sizeDelta = new Vector2(1600f, 260f);
        }

        // Near-invisible fallback button, cloned from the run button so it inherits valid
        // Button/Image/Text references. Kept tiny and almost transparent in a corner.
        if (runButton != null)
        {
            m_DebugFallbackButton = Instantiate(runButton, inventoryCanvas.transform, false);
            m_DebugFallbackButton.name = "DebugFallbackButton";
            m_DebugFallbackButton.gameObject.SetActive(true);
            m_DebugFallbackButton.interactable = true;

            m_DebugFallbackButton.onClick.RemoveAllListeners();
            m_DebugFallbackButton.onClick.AddListener(GoToFallbackStartScene);

            Text fallbackLabel = m_DebugFallbackButton.GetComponentInChildren<Text>();
            if (fallbackLabel != null) fallbackLabel.text = "";

            Image fallbackImage = m_DebugFallbackButton.GetComponent<Image>();
            if (fallbackImage != null) fallbackImage.color = new Color(1f, 1f, 1f, 0.02f);

            RectTransform fallbackRect = m_DebugFallbackButton.GetComponent<RectTransform>();
            fallbackRect.anchorMin = new Vector2(1f, 0f);
            fallbackRect.anchorMax = new Vector2(1f, 0f);
            fallbackRect.pivot = new Vector2(1f, 0f);
            fallbackRect.anchoredPosition = new Vector2(-10f, 10f);
            fallbackRect.sizeDelta = new Vector2(70f, 70f);
        }
    }

    /// <summary>
    /// Escape hatch to the old mouse-driven Start scene, reachable only through the
    /// hidden corner button, in case the hold-to-start gesture/key never registers.
    /// </summary>
    void GoToFallbackStartScene()
    {
        SceneManager.LoadScene(k_FallbackStartSceneName);
    }

    /// <summary>
    /// Now that the leaderboard has no background panel behind it, its text needs a strong
    /// color + outline to stay readable over the 3D scene, and the score column needs to
    /// stand out more than the rank/name columns.
    /// </summary>
    void StyleLeaderboardText()
    {
        if (leaderboard == null || leaderboard.entriesRoot == null)
            return;

        Color textColor = Color.white;
        Color scoreColor = new Color(1f, 0.82f, 0.15f); // gold

        Text title = leaderboard.transform.Find("Background/Text")?.GetComponent<Text>();
        StyleText(title, textColor, false);

        for (int i = 0; i < leaderboard.entriesRoot.childCount; i++)
        {
            HighscoreUI hs = leaderboard.entriesRoot.GetChild(i).GetComponent<HighscoreUI>();
            if (hs == null)
                continue;

            StyleText(hs.number, textColor, false);
            StyleText(hs.playerName, textColor, false);
            StyleText(hs.inputName != null ? hs.inputName.textComponent : null, textColor, false);
            StyleText(hs.score, scoreColor, true);
        }
    }

    void StyleText(Text text, Color color, bool bold)
    {
        if (text == null)
            return;

        text.color = color;
        text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;

        Outline outline = text.GetComponent<Outline>();
        if (outline == null) outline = text.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);
    }

    /// <summary>
    /// Tracks how long debugStartKey (S) has been held. Reaching holdDurationToStart
    /// seconds of continuous hold starts the run. This key is a keyboard stand-in for the
    /// real gesture (crouching in front of the Kinect) used once body tracking is wired in.
    /// </summary>
    void UpdateHoldToStart()
    {
        if (Input.GetKey(debugStartKey))
        {
            m_HoldTimer += Time.deltaTime;
        }
        else
        {
            m_HoldTimer = 0f;
        }

        if (m_HoldInstructionText != null)
        {
            // Progress shown as filling dots (one per second held), separate from the actual
            // hold duration: all dots fill by dotsToShow seconds, then hold full/green for the
            // remaining time as a short pause before the run actually starts.
            int totalDots = Mathf.Max(1, dotsToShow);
            float ratio = Mathf.Clamp01(m_HoldTimer / totalDots);
            string timerColor = ColorUtility.ToHtmlStringRGB(Color.Lerp(Color.white, Color.green, ratio));

            int filledDots = Mathf.Clamp(Mathf.FloorToInt(m_HoldTimer + 0.001f), 0, totalDots);
            System.Text.StringBuilder dots = new System.Text.StringBuilder();
            for (int i = 0; i < totalDots; i++)
            {
                dots.Append(i < filledDots ? "*" : "-"); // ASCII only: the game font may not have ●/○ glyphs
                if (i < totalDots - 1) dots.Append(' ');
            }

            m_HoldInstructionText.text = string.Format(
                "PLACE-TOI SUR LA CROIX POUR JOUER\n<color=#{0}>{1}</color>",
                timerColor, dots.ToString());
        }

        if (m_HoldTimer >= holdDurationToStart)
        {
            m_HoldTimer = 0f;
            StartGame();
        }
    }

    public override void Exit(AState to)
    {
        if (missionPopup != null) missionPopup.gameObject.SetActive(false);
        inventoryCanvas.gameObject.SetActive(false);

        // The leaderboard has its own independent Canvas (not under inventoryCanvas), so it
        // must be closed explicitly or it would stay visible on top of the run itself.
        if (leaderboard != null) leaderboard.Close();

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
        if (!m_ReadyToStart)
        {
            m_ReadyToStart = ThemeDatabase.loaded && CharacterDatabase.loaded;
            if (m_ReadyToStart)
            {
                //we can always enabled, as the parent will be disabled if tutorial is already done
                tutorialPrompt.SetActive(true);

                if (m_HoldInstructionText != null)
                    m_HoldInstructionText.gameObject.SetActive(true);
            }
        }

        if (m_ReadyToStart)
        {
            UpdateHoldToStart();
        }

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
