using UnityEngine;
using UnityEngine.UI;
using System.Collections;
#if UNITY_ANALYTICS
using UnityEngine.Analytics;
#endif
using System.Collections.Generic;
 
/// <summary>
/// state pushed on top of the GameManager when the player dies.
/// </summary>
public class GameOverState : AState
{
    public TrackManager trackManager;
    public Canvas canvas;
    public MissionUI missionPopup;

	public AudioClip gameOverTheme;

	public Leaderboard miniLeaderboard;
	public Leaderboard fullLeaderboard;

    public GameObject addButton;

    protected Text m_ReturnToMenuText;
    protected Coroutine m_ReturnToMenuCoroutine;
    protected const float k_ReturnToMenuDelay = 5.0f;

    public override void Enter(AState from)
    {
        canvas.gameObject.SetActive(true);

		string assignedName = CultCatNameGenerator.GetRandomName(PlayerData.instance.previousName);
		miniLeaderboard.playerEntry.inputName.text = assignedName;
		miniLeaderboard.playerEntry.inputName.interactable = false;
		PlayerData.instance.previousName = assignedName;
		
        miniLeaderboard.playerEntry.score.text = trackManager.score.ToString();
		miniLeaderboard.Populate();

		SetupAutomaticReturnToMenu();

        if (missionPopup != null)
        {
            if (PlayerData.instance.AnyMissionComplete())
                StartCoroutine(missionPopup.Open());
            else
                missionPopup.gameObject.SetActive(false);
        }

		CreditCoins();

		if (MusicPlayer.instance.GetStem(0) != gameOverTheme)
		{
            MusicPlayer.instance.SetStem(0, gameOverTheme);
			StartCoroutine(MusicPlayer.instance.RestartAllStems());
        }
    }

	public override void Exit(AState to)
    {
        if (m_ReturnToMenuCoroutine != null)
        {
            StopCoroutine(m_ReturnToMenuCoroutine);
            m_ReturnToMenuCoroutine = null;
        }

        canvas.gameObject.SetActive(false);
        FinishRun();
    }

    protected void SetupAutomaticReturnToMenu()
    {
        // Game Over no longer requires input: remove Run, Main Menu and Leaderboard.
        Button[] buttons = canvas.GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; ++i)
            buttons[i].gameObject.SetActive(false);

        const string countdownObjectName = "ReturnToMenuCountdown";
        Transform existingCountdown = canvas.transform.Find(countdownObjectName);

        if (existingCountdown != null)
        {
            m_ReturnToMenuText = existingCountdown.GetComponent<Text>();
            existingCountdown.gameObject.SetActive(true);
        }
        else
        {
            GameObject countdownObject = new GameObject(
                countdownObjectName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text),
                typeof(Outline));

            countdownObject.transform.SetParent(canvas.transform, false);
            m_ReturnToMenuText = countdownObject.GetComponent<Text>();

            RectTransform countdownRect = countdownObject.GetComponent<RectTransform>();
            countdownRect.anchorMin = new Vector2(0.08f, 0.12f);
            countdownRect.anchorMax = new Vector2(0.92f, 0.25f);
            countdownRect.offsetMin = Vector2.zero;
            countdownRect.offsetMax = Vector2.zero;

            Outline outline = countdownObject.GetComponent<Outline>();
            outline.effectColor = new Color(0.08f, 0.08f, 0.12f, 0.95f);
            outline.effectDistance = new Vector2(4.0f, -4.0f);
            outline.useGraphicAlpha = true;
        }

        m_ReturnToMenuText.font = miniLeaderboard.playerEntry.score.font;
        m_ReturnToMenuText.fontSize = 52;
        m_ReturnToMenuText.resizeTextForBestFit = true;
        m_ReturnToMenuText.resizeTextMinSize = 28;
        m_ReturnToMenuText.resizeTextMaxSize = 52;
        m_ReturnToMenuText.alignment = TextAnchor.MiddleCenter;
        m_ReturnToMenuText.color = Color.white;
        m_ReturnToMenuText.raycastTarget = false;

        if (m_ReturnToMenuCoroutine != null)
            StopCoroutine(m_ReturnToMenuCoroutine);
        m_ReturnToMenuCoroutine = StartCoroutine(ReturnToMenuAfterDelay());
    }

    protected IEnumerator ReturnToMenuAfterDelay()
    {
        float remainingTime = k_ReturnToMenuDelay;

        while (remainingTime > 0.0f)
        {
            int displayedSeconds = Mathf.Max(1, Mathf.CeilToInt(remainingTime));
            string unit = displayedSeconds > 1 ? "SECONDES" : "SECONDE";
            m_ReturnToMenuText.text = "RETOUR AU MENU DANS " + displayedSeconds + " " + unit;

            yield return null;
            remainingTime -= Time.unscaledDeltaTime;
        }

        trackManager.isRerun = false;
        manager.SwitchState("Loadout");
    }

    public override string GetName()
    {
        return "GameOver";
    }

    public override void Tick()
    {
        
    }

	public void OpenLeaderboard()
	{
		fullLeaderboard.forcePlayerDisplay = false;
		fullLeaderboard.displayPlayer = true;
		fullLeaderboard.playerEntry.playerName.text = miniLeaderboard.playerEntry.inputName.text;
		fullLeaderboard.playerEntry.score.text = trackManager.score.ToString();

		fullLeaderboard.Open();
    }

	public void GoToStore()
    {
        // Disabled for simple demo
    }


    public void GoToLoadout()
    {
        trackManager.isRerun = false;
		manager.SwitchState("Loadout");
    }

    public void RunAgain()
    {
        trackManager.isRerun = false;
        manager.SwitchState("Game");
    }

	protected void CreditCoins()
	{
		// Fish are score pickups only; there is no currency to credit at the end of a run.
		PlayerData.instance.Save();
	}

	protected void FinishRun()
    {
		if (string.IsNullOrWhiteSpace(miniLeaderboard.playerEntry.inputName.text))
			miniLeaderboard.playerEntry.inputName.text = CultCatNameGenerator.GetRandomName(PlayerData.instance.previousName);

		PlayerData.instance.previousName = miniLeaderboard.playerEntry.inputName.text;

        PlayerData.instance.InsertScore(trackManager.score, miniLeaderboard.playerEntry.inputName.text );

        CharacterCollider.DeathEvent de = trackManager.characterController.characterCollider.deathData;
        //register data to analytics
#if UNITY_ANALYTICS
        AnalyticsEvent.GameOver(null, new Dictionary<string, object> {
            { "coins", de.coins },
            { "premium", de.premium },
            { "score", de.score },
            { "distance", de.worldDistance },
            { "obstacle",  de.obstacleType },
            { "theme", de.themeUsed },
            { "character", de.character },
        });
#endif

        PlayerData.instance.Save();

        trackManager.End();
    }

    //----------------
}
