using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

#if UNITY_ADS
using UnityEngine.Advertisements;
#endif
#if UNITY_ANALYTICS
using UnityEngine.Analytics;
#endif

/// <summary>
/// Pushed on top of the GameManager during gameplay. Takes care of initializing all the UI and start the TrackManager
/// Also will take care of cleaning when leaving that state.
/// </summary>
public class GameState : AState
{
	static int s_DeadHash = Animator.StringToHash("Dead");

    public Canvas canvas;
    public TrackManager trackManager;

	public AudioClip gameTheme;

    [Header("UI")]
    public Text coinText;
    public Text premiumText;
    public Text scoreText;
	public Text distanceText;
    public Text multiplierText;
	public Text countdownText;
    public RectTransform powerupZone;
	public RectTransform lifeRectTransform;

	public RectTransform pauseMenu;
	public RectTransform wholeUI;
	public Button pauseButton;

    public Image inventoryIcon;

    public GameObject gameOverPopup;
    public Button premiumForLifeButton;
    public GameObject adsForLifeButton;
    public Text premiumCurrencyOwned;

    [Header("Prefabs")]
    public GameObject PowerupIconPrefab;

    [Header("Tutorial")]
    public Text tutorialValidatedObstacles;
    public GameObject sideSlideTuto;
    public GameObject upSlideTuto;
    public GameObject downSlideTuto;
    public GameObject finishTuto;
    public Sprite tutorialSideLeftIcon;
    public Sprite tutorialSideRightIcon;

    [Tooltip("Distance in metres before an obstacle at which its tutorial instruction is shown.")]
    [Min(0.0f)]
    public float tutorialInstructionDistance = 14.0f;

    [Tooltip("Distance in metres after an obstacle at which it is considered cleared.")]
    [Min(0.0f)]
    public float tutorialObstacleClearDistance = 1.5f;

    [Tooltip("Seconds for which the tutorial-complete marker is displayed before the run continues.")]
    [Min(0.0f)]
    public float tutorialCompletionDelay = 3.0f;

    [Tooltip("Number of obstacles that receive an in-game tutorial indication.")]
    [Min(1)]
    public int tutorialObstacleCount = 5;

    [Tooltip("Vertical offset in pixels applied to tutorial icons.")]
    public float tutorialIconVerticalOffset = 35.0f;

    [Tooltip("Thickness in pixels of the black outline around tutorial icons.")]
    [Min(0.0f)]
    public float tutorialIconOutlineSize = 4.0f;

    [System.NonSerialized]
    public Modifier currentModifier = new Modifier();

    public string adsPlacementId = "rewardedVideo";
#if UNITY_ANALYTICS
    public AdvertisingNetwork adsNetwork = AdvertisingNetwork.UnityAds;
#endif
    public bool adsRewarded = true;

    protected bool m_Finished;
    protected float m_TimeSinceStart;
    protected List<PowerupIcon> m_PowerupIcons = new List<PowerupIcon>();
	protected Image[] m_LifeHearts;

    protected RectTransform m_CountdownRectTransform;
    protected bool m_WasMoving;

    protected bool m_AdsInitialised = false;
    protected bool m_GameoverSelectionDone = false;

    protected int k_MaxLives = 3;

    protected bool m_IsTutorial; //True while contextual hints are shown for the first obstacles.
    protected int m_TutorialClearedObstacle = 0;
    protected bool m_DisplayTutorial;
    protected int m_TutorialSideDirection = 1;
    protected bool m_TutorialCompletionCountdown;
    protected float m_TutorialCompletionTimeRemaining;
    protected float m_TutorialCompletionEndTime;
    protected Text m_TutorialCompletionMessage;
    protected string m_TutorialCompletionBaseMessage;
    protected int m_CurrentSegmentObstacleIndex = 0;

    public override void Enter(AState from)
    {
        m_CountdownRectTransform = countdownText.GetComponent<RectTransform>();

        m_LifeHearts = new Image[k_MaxLives];
        for (int i = 0; i < k_MaxLives; ++i)
        {
            m_LifeHearts[i] = lifeRectTransform.GetChild(i).GetComponent<Image>();
        }

        if (MusicPlayer.instance.GetStem(0) != gameTheme)
        {
            MusicPlayer.instance.SetStem(0, gameTheme);
            CoroutineHandler.StartStaticCoroutine(MusicPlayer.instance.RestartAllStems());
        }

        m_AdsInitialised = false;
        m_GameoverSelectionDone = false;

        StartGame();
    }

    public override void Exit(AState to)
    {
        canvas.gameObject.SetActive(false);

        ClearPowerup();
    }

    public void StartGame()
    {
        canvas.gameObject.SetActive(true);
        pauseMenu.gameObject.SetActive(false);
        wholeUI.gameObject.SetActive(true);
        pauseButton.gameObject.SetActive(!trackManager.isTutorial);
        gameOverPopup.SetActive(false);

        sideSlideTuto.SetActive(false);
        upSlideTuto.SetActive(false);
        downSlideTuto.SetActive(false);
        finishTuto.SetActive(false);
        ConfigureTutorialIconVisuals();
        m_TutorialCompletionCountdown = false;
        m_TutorialCompletionTimeRemaining = 0.0f;
        m_TutorialCompletionEndTime = 0.0f;
        if (tutorialValidatedObstacles != null) tutorialValidatedObstacles.gameObject.SetActive(false);

        if (!trackManager.isRerun)
        {
            m_TimeSinceStart = 0;
            trackManager.characterController.currentLife = trackManager.characterController.maxLife;
            trackManager.characterController.currentTutorialLevel = 0;
            m_TutorialClearedObstacle = 0;
            m_CurrentSegmentObstacleIndex = 0;
            m_IsTutorial = true;
        }

        currentModifier.OnRunStart(this);

        trackManager.isTutorial = m_IsTutorial;

        if (m_IsTutorial)
        {
            m_DisplayTutorial = true;
            trackManager.newSegmentCreated = null;

            trackManager.currentSegementChanged = segment =>
            {
                m_CurrentSegmentObstacleIndex = 0;
                m_DisplayTutorial = true;
                DisplayTutorial(false);
            };
        }

        m_Finished = false;
        m_PowerupIcons.Clear();

        StartCoroutine(trackManager.Begin());
    }

    public override string GetName()
    {
        return "Game";
    }

    public override void Tick()
    {
        if (m_Finished)
        {
            //if we are finished, we check if advertisement is ready, allow to disable the button until it is ready
#if UNITY_ADS
            if (!trackManager.isTutorial && !m_AdsInitialised && Advertisement.IsReady(adsPlacementId))
            {
                adsForLifeButton.SetActive(true);
                m_AdsInitialised = true;
#if UNITY_ANALYTICS
                AnalyticsEvent.AdOffer(adsRewarded, adsNetwork, adsPlacementId, new Dictionary<string, object>
            {
                { "level_index", PlayerData.instance.rank },
                { "distance", TrackManager.instance == null ? 0 : TrackManager.instance.worldDistance },
            });
#endif
            }
            else if(trackManager.isTutorial || !m_AdsInitialised)
                adsForLifeButton.SetActive(false);
#else
            adsForLifeButton.SetActive(false); //Ads is disabled
#endif

            return;
        }

        if (trackManager.isLoaded)
        {
            CharacterInputController chrCtrl = trackManager.characterController;

            if (m_TutorialCompletionCountdown)
            {
                UpdateTutorialCompletionCountdown();
                UpdateUI();
                return;
            }

            m_TimeSinceStart += Time.deltaTime;

            if (chrCtrl.currentLife <= 0)
            {
                pauseButton.gameObject.SetActive(false);
                chrCtrl.CleanConsumable();
                chrCtrl.character.animator.SetBool(s_DeadHash, true);
                chrCtrl.characterCollider.koParticle.gameObject.SetActive(true);
                StartCoroutine(WaitForGameOver());
            }

            // Consumable ticking & lifetime management
            List<Consumable> toRemove = new List<Consumable>();
            List<PowerupIcon> toRemoveIcon = new List<PowerupIcon>();

            for (int i = 0; i < chrCtrl.consumables.Count; ++i)
            {
                PowerupIcon icon = null;
                for (int j = 0; j < m_PowerupIcons.Count; ++j)
                {
                    if (m_PowerupIcons[j].linkedConsumable == chrCtrl.consumables[i])
                    {
                        icon = m_PowerupIcons[j];
                        break;
                    }
                }

                chrCtrl.consumables[i].Tick(chrCtrl);
                if (!chrCtrl.consumables[i].active)
                {
                    toRemove.Add(chrCtrl.consumables[i]);
                    toRemoveIcon.Add(icon);
                }
                else if (icon == null)
                {
                    // If there's no icon for the active consumable, create it!
                    GameObject o = Instantiate(PowerupIconPrefab);

                    icon = o.GetComponent<PowerupIcon>();

                    icon.linkedConsumable = chrCtrl.consumables[i];
                    icon.transform.SetParent(powerupZone, false);

                    m_PowerupIcons.Add(icon);
                }
            }

            for (int i = 0; i < toRemove.Count; ++i)
            {
                toRemove[i].Ended(trackManager.characterController);

                Addressables.ReleaseInstance(toRemove[i].gameObject);
                if (toRemoveIcon[i] != null)
                   Destroy(toRemoveIcon[i].gameObject);

                chrCtrl.consumables.Remove(toRemove[i]);
                m_PowerupIcons.Remove(toRemoveIcon[i]);
            }

            if (m_IsTutorial)
                TutorialCheckObstacleClear();

            UpdateUI();

            currentModifier.OnRunTick(this);
        }
    }

	void OnApplicationPause(bool pauseStatus)
	{
		if (pauseStatus) Pause();
	}

    void OnApplicationFocus(bool focusStatus)
    {
        if (!focusStatus) Pause();
    }

    public void Pause(bool displayMenu = true)
	{
		//check if we aren't finished OR if we aren't already in pause (as that would mess states)
		if (m_Finished || AudioListener.pause == true)
			return;

		AudioListener.pause = true;
		Time.timeScale = 0;

		pauseButton.gameObject.SetActive(false);
        pauseMenu.gameObject.SetActive (displayMenu);
		wholeUI.gameObject.SetActive(false);
		m_WasMoving = trackManager.isMoving;
		trackManager.StopMove();
	}

	public void Resume()
	{
		Time.timeScale = 1.0f;
		pauseButton.gameObject.SetActive(true);
		pauseMenu.gameObject.SetActive (false);
		wholeUI.gameObject.SetActive(true);
		if (m_WasMoving)
		{
			trackManager.StartMove(false);
		}

		AudioListener.pause = false;
	}

	public void QuitToLoadout()
	{
		// Used by the pause menu to return immediately to loadout, canceling everything.
		Time.timeScale = 1.0f;
		AudioListener.pause = false;
		trackManager.End();
		trackManager.isRerun = false;
        PlayerData.instance.Save();
		manager.SwitchState ("Loadout");
	}

    protected void UpdateUI()
    {
        coinText.text = trackManager.characterController.coins.ToString();
        premiumText.text = trackManager.characterController.premium.ToString();

		for (int i = 0; i < 3; ++i)
		{

			if(trackManager.characterController.currentLife > i)
			{
				m_LifeHearts[i].color = Color.white;
			}
			else
			{
				m_LifeHearts[i].color = Color.black;
			}
		}

        scoreText.text = trackManager.score.ToString();
        multiplierText.text = "x " + trackManager.multiplier;

		distanceText.text = Mathf.FloorToInt(trackManager.worldDistance).ToString() + "m";

		if (m_TutorialCompletionCountdown)
		{
			// The gameplay countdown is rendered behind the tutorial overlay.
			// Append the number to the completion message so they share one layer.
			m_CountdownRectTransform.localScale = Vector3.zero;
			if (m_TutorialCompletionMessage != null)
			{
				int seconds = Mathf.Max(1, Mathf.CeilToInt(m_TutorialCompletionTimeRemaining));
				m_TutorialCompletionMessage.text = m_TutorialCompletionBaseMessage + "\n\n" + seconds;
			}
		}
		else if (trackManager.timeToStart >= 0)
		{
			countdownText.gameObject.SetActive(true);
			countdownText.text = Mathf.Ceil(trackManager.timeToStart).ToString();
			m_CountdownRectTransform.localScale = Vector3.one * (1.0f - (trackManager.timeToStart - Mathf.Floor(trackManager.timeToStart)));
		}
		else
		{
			m_CountdownRectTransform.localScale = Vector3.zero;
		}

        // Consumable
        if (trackManager.characterController.inventory != null)
        {
            inventoryIcon.transform.parent.gameObject.SetActive(true);
            inventoryIcon.sprite = trackManager.characterController.inventory.icon;
        }
        else
            inventoryIcon.transform.parent.gameObject.SetActive(false);
    }

	IEnumerator WaitForGameOver()
	{
		m_Finished = true;
		trackManager.StopMove();

        // Reseting the global blinking value. Can happen if game unexpectly exited while still blinking
        Shader.SetGlobalFloat("_BlinkingValue", 0.0f);

        yield return new WaitForSeconds(2.0f);
        if (currentModifier.OnRunEnd(this))
        {
            if (trackManager.isRerun)
                manager.SwitchState("GameOver");
            else
                OpenGameOverPopup();
        }
	}

    protected void ClearPowerup()
    {
        for (int i = 0; i < m_PowerupIcons.Count; ++i)
        {
            if (m_PowerupIcons[i] != null)
                Destroy(m_PowerupIcons[i].gameObject);
        }

        trackManager.characterController.powerupSource.Stop();

        m_PowerupIcons.Clear();
    }

    public void OpenGameOverPopup()
    {
        premiumForLifeButton.interactable = PlayerData.instance.premium >= 3;

        premiumCurrencyOwned.text = PlayerData.instance.premium.ToString();

        ClearPowerup();

        gameOverPopup.SetActive(true);
    }

    public void GameOver()
    {
        manager.SwitchState("GameOver");
    }

    public void PremiumForLife()
    {
        //This check avoid a bug where the video AND premium button are released on the same frame.
        //It lead to the ads playing and then crashing the game as it try to start the second wind again.
        //Whichever of those function run first will take precedence
        if (m_GameoverSelectionDone)
            return;

        m_GameoverSelectionDone = true;

        PlayerData.instance.premium -= 3;
        //since premium are directly added to the PlayerData premium count, we also need to remove them from the current run premium count
        // (as if you had 0, grabbed 3 during that run, you can directly buy a new chance). But for the case where you add one in the playerdata
        // and grabbed 2 during that run, we don't want to remove 3, otherwise will have -1 premium for that run!
        trackManager.characterController.premium -= Mathf.Min(trackManager.characterController.premium, 3);

        SecondWind();
    }

    public void SecondWind()
    {
        trackManager.characterController.currentLife = 1;
        trackManager.isRerun = true;
        StartGame();
    }

    public void ShowRewardedAd()
    {
        if (m_GameoverSelectionDone)
            return;

        m_GameoverSelectionDone = true;

#if UNITY_ADS
        if (Advertisement.IsReady(adsPlacementId))
        {
#if UNITY_ANALYTICS
            AnalyticsEvent.AdStart(adsRewarded, adsNetwork, adsPlacementId, new Dictionary<string, object>
            {
                { "level_index", PlayerData.instance.rank },
                { "distance", TrackManager.instance == null ? 0 : TrackManager.instance.worldDistance },
            });
#endif
            var options = new ShowOptions { resultCallback = HandleShowResult };
            Advertisement.Show(adsPlacementId, options);
        }
        else
        {
#if UNITY_ANALYTICS
            AnalyticsEvent.AdSkip(adsRewarded, adsNetwork, adsPlacementId, new Dictionary<string, object> {
                { "error", Advertisement.GetPlacementState(adsPlacementId).ToString() }
            });
#endif
        }
#else
		GameOver();
#endif
    }

    //=== AD
#if UNITY_ADS

    private void HandleShowResult(ShowResult result)
    {
        switch (result)
        {
            case ShowResult.Finished:
#if UNITY_ANALYTICS
                AnalyticsEvent.AdComplete(adsRewarded, adsNetwork, adsPlacementId);
#endif
                SecondWind();
                break;
            case ShowResult.Skipped:
                Debug.Log("The ad was skipped before reaching the end.");
#if UNITY_ANALYTICS
                AnalyticsEvent.AdSkip(adsRewarded, adsNetwork, adsPlacementId);
#endif
                break;
            case ShowResult.Failed:
                Debug.LogError("The ad failed to be shown.");
#if UNITY_ANALYTICS
                AnalyticsEvent.AdSkip(adsRewarded, adsNetwork, adsPlacementId, new Dictionary<string, object> {
                    { "error", "failed" }
                });
#endif
                break;
        }
    }
#endif


    void TutorialCheckObstacleClear()
    {
        if (trackManager.segments.Count == 0)
            return;

        TrackSegment currentSeg = trackManager.currentSegment;
        if (currentSeg == null)
            return;

        int obstacleCount = currentSeg.obstaclePositions != null ? currentSeg.obstaclePositions.Length : 0;
        if (m_CurrentSegmentObstacleIndex >= obstacleCount)
            return;

        float nextObstaclePosition = currentSeg.obstaclePositions[m_CurrentSegmentObstacleIndex];

        // Obstacles and player movement both use the segment's normalized path
        // parameter, so this is the matching travelled distance on the segment.
        float obstacleWorldDist = nextObstaclePosition * currentSeg.worldLength;
        float distToObstacle = obstacleWorldDist - trackManager.currentSegmentDistance;

        // Keep a small tolerance after the obstacle so a long frame cannot skip
        // the prompt by jumping directly from before the threshold to just past it.
        if (m_DisplayTutorial && distToObstacle <= tutorialInstructionDistance && distToObstacle > -tutorialObstacleClearDistance)
        {
            int tutorialLevel = GetTutorialLevelForObstacle(currentSeg, nextObstaclePosition);
            if (tutorialLevel < 0)
            {
                DisplayTutorial(false);
                return;
            }

            trackManager.characterController.currentTutorialLevel = tutorialLevel;

            if (tutorialLevel == 0)
            {
                int currentLane = trackManager.characterController.currentLane;
                bool currentLaneBlocked = IsTutorialLaneBlocked(currentSeg, nextObstaclePosition, currentLane);

                // A lateral hint is only useful when the obstacle is actually
                // in the player's lane. Hide it as soon as the target lane is safe.
                if (!currentLaneBlocked ||
                    !TryFindSafeTutorialSideDirection(currentSeg, nextObstaclePosition,
                        currentLane, out m_TutorialSideDirection))
                {
                    DisplayTutorial(false);
                    return;
                }
            }

            DisplayTutorial(true);
        }
        else if (distToObstacle <= -tutorialObstacleClearDistance)
        {
            DisplayTutorial(false);
            m_CurrentSegmentObstacleIndex += 1;
            m_TutorialClearedObstacle += 1;

            if (m_TutorialClearedObstacle >= Mathf.Max(1, tutorialObstacleCount))
            {
                CompleteIntegratedTutorial();
            }
            else
            {
                m_DisplayTutorial = true;
                trackManager.ChangeZone();
            }

            trackManager.characterController.characterCollider.tutorialHitObstacle = false;
        }
    }

    int GetTutorialLevelForObstacle(TrackSegment segment, float obstaclePosition)
    {
        if (segment.objectRoot == null)
            return -1;

        Vector3 pathPosition;
        Quaternion pathRotation;
        segment.GetPointAt(obstaclePosition, out pathPosition, out pathRotation);

        Obstacle[] obstacles = segment.objectRoot.GetComponentsInChildren<Obstacle>(true);
        Obstacle closestObstacle = null;
        float closestSqrDistance = float.MaxValue;
        float searchRadius = trackManager.laneOffset * 2.5f;

        for (int i = 0; i < obstacles.Length; ++i)
        {
            float sqrDistance = (obstacles[i].transform.position - pathPosition).sqrMagnitude;
            if (sqrDistance < closestSqrDistance && sqrDistance <= searchRadius * searchRadius)
            {
                closestObstacle = obstacles[i];
                closestSqrDistance = sqrDistance;
            }
        }

        if (closestObstacle == null)
            return -1;

        if (closestObstacle is SimpleBarricade || closestObstacle is PatrollingObstacle ||
            closestObstacle is Missile)
            return 0;

        if (closestObstacle is AllLaneObstacle)
        {
            string obstacleName = closestObstacle.gameObject.name.ToLowerInvariant();
            return obstacleName.Contains("high") ? 2 : 1;
        }

        return 0;
    }

    void CompleteIntegratedTutorial()
    {
        m_DisplayTutorial = false;
        m_IsTutorial = false;
        trackManager.isTutorial = false;
        trackManager.characterController.currentTutorialLevel = 3;
        trackManager.characterController.tutorialWaitingForValidation = false;
        trackManager.currentSegementChanged = null;
        trackManager.newSegmentCreated = null;

        DisplayTutorial(false);
        pauseButton.gameObject.SetActive(true);
        trackManager.SwitchToRegularTheme();
    }

    void BeginTutorialCompletionCountdown()
    {
        trackManager.characterController.currentTutorialLevel = 3;
        m_DisplayTutorial = false;
        m_TutorialCompletionTimeRemaining = Mathf.Max(0.0f, tutorialCompletionDelay);
        m_TutorialCompletionEndTime = Time.realtimeSinceStartup + m_TutorialCompletionTimeRemaining;
        m_TutorialCompletionCountdown = true;

        // This panel is the visible end marker. Continuation is automatic, so
        // hide its old "Go To Loadout" button during the countdown.
        Button finishButton = finishTuto.GetComponentInChildren<Button>(true);
        if (finishButton != null)
            finishButton.gameObject.SetActive(false);

        Text[] finishTexts = finishTuto.GetComponentsInChildren<Text>(true);
        for (int i = 0; i < finishTexts.Length; ++i)
        {
            if (finishTexts[i].GetComponentInParent<Button>() == null)
            {
                m_TutorialCompletionMessage = finishTexts[i];
                m_TutorialCompletionBaseMessage = finishTexts[i].text;
                m_TutorialCompletionMessage.verticalOverflow = VerticalWrapMode.Overflow;
                break;
            }
        }

        DisplayTutorial(true);
    }

    void UpdateTutorialCompletionCountdown()
    {
        // realtimeSinceStartup is independent from Time.timeScale, so this is
        // always a real three-second countdown while gameplay is paused.
        m_TutorialCompletionTimeRemaining = Mathf.Max(0.0f,
            m_TutorialCompletionEndTime - Time.realtimeSinceStartup);
        if (m_TutorialCompletionTimeRemaining > 0.0f)
            return;

        m_TutorialCompletionTimeRemaining = 0.0f;
        m_TutorialCompletionCountdown = false;
        m_IsTutorial = false;
        trackManager.isTutorial = false;

        if (m_TutorialCompletionMessage != null)
            m_TutorialCompletionMessage.text = m_TutorialCompletionBaseMessage;

        DisplayTutorial(false);
        pauseButton.gameObject.SetActive(true);
        trackManager.SwitchToRegularTheme();
    }

    void DisplayTutorial(bool value)
    {
        // Integrated tutorial: hints are overlays only and never pause gameplay.
        int tutorialLevel = trackManager.characterController.currentTutorialLevel;
        bool showSide = value && tutorialLevel == 0;
        bool showJump = value && tutorialLevel == 1;
        bool showCrouch = value && tutorialLevel == 2;

        sideSlideTuto.SetActive(showSide);
        upSlideTuto.SetActive(showJump);
        downSlideTuto.SetActive(showCrouch);
        finishTuto.SetActive(false);
        trackManager.characterController.tutorialWaitingForValidation = false;

        if (showSide)
            ConfigureSideTutorialDirection();
    }

    void ConfigureTutorialIconVisuals()
    {
        ConfigureTutorialIconVisual(sideSlideTuto);
        ConfigureTutorialIconVisual(upSlideTuto);
        ConfigureTutorialIconVisual(downSlideTuto);
    }

    void ConfigureTutorialIconVisual(GameObject tutorialPanel)
    {
        if (tutorialPanel == null)
            return;

        Transform iconTransform = tutorialPanel.transform.Find("Animation");
        Image iconImage = iconTransform != null ? iconTransform.GetComponent<Image>() : null;
        if (iconImage == null)
            return;

        RectTransform iconRect = iconImage.rectTransform;
        iconRect.anchoredPosition = new Vector2(iconRect.anchoredPosition.x, tutorialIconVerticalOffset);

        Outline outline = iconImage.GetComponent<Outline>();
        if (outline == null)
            outline = iconImage.gameObject.AddComponent<Outline>();

        outline.effectColor = Color.black;
        float outlineSize = Mathf.Max(0.0f, tutorialIconOutlineSize);
        outline.effectDistance = new Vector2(outlineSize, -outlineSize);
        outline.useGraphicAlpha = true;

        TutorialHintAnimator hintAnimator = tutorialPanel.GetComponent<TutorialHintAnimator>();
        if (hintAnimator == null)
            hintAnimator = tutorialPanel.AddComponent<TutorialHintAnimator>();

        hintAnimator.Configure(iconRect);
    }

    void ConfigureSideTutorialDirection()
    {
        Transform figureTransform = sideSlideTuto.transform.Find("Animation");
        Image sideImage = figureTransform != null ? figureTransform.GetComponent<Image>() : null;
        if (sideImage == null)
            return;

        bool moveLeft = m_TutorialSideDirection < 0;
        Sprite directionIcon = moveLeft ? tutorialSideLeftIcon : tutorialSideRightIcon;
        if (directionIcon != null)
            sideImage.sprite = directionIcon;

        Vector3 figureScale = sideImage.transform.localScale;
        figureScale.x = Mathf.Abs(figureScale.x);
        sideImage.transform.localScale = figureScale;
    }

    bool TryFindSafeTutorialSideDirection(TrackSegment segment, float obstaclePosition,
        int currentLane, out int direction)
    {
        bool canMoveLeft = currentLane > 0 && !IsTutorialLaneBlocked(segment, obstaclePosition, currentLane - 1);
        bool canMoveRight = currentLane < 2 && !IsTutorialLaneBlocked(segment, obstaclePosition, currentLane + 1);

        if (canMoveLeft && !canMoveRight)
        {
            direction = -1;
            return true;
        }

        if (canMoveRight && !canMoveLeft)
        {
            direction = 1;
            return true;
        }

        // If both adjacent lanes are free, keep the usual rightward hint from
        // the left/centre and point inward when already on the right edge.
        if (canMoveLeft && canMoveRight)
        {
            direction = currentLane >= 2 ? -1 : 1;
            return true;
        }

        // Do not display an arbitrary direction when no adjacent lane is safe.
        direction = 0;
        return false;
    }

    bool IsTutorialLaneBlocked(TrackSegment segment, float obstaclePosition, int lane)
    {
        Vector3 pathPosition;
        Quaternion pathRotation;
        segment.GetPointAt(obstaclePosition, out pathPosition, out pathRotation);

        Vector3 lanePosition = pathPosition
            + (lane - 1) * trackManager.laneOffset * (pathRotation * Vector3.right)
            + pathRotation * Vector3.up;
        Vector3 halfExtents = new Vector3(trackManager.laneOffset * 0.35f, 1.25f, 1.0f);
        int obstacleLayer = LayerMask.NameToLayer("Obstacle");

        return obstacleLayer >= 0 && Physics.CheckBox(lanePosition, halfExtents,
            pathRotation, 1 << obstacleLayer, QueryTriggerInteraction.Collide);
    }


    public void FinishTutorial()
    {
        PlayerData.instance.tutorialDone = false;
        PlayerData.instance.Save();

        QuitToLoadout();
    }
}
