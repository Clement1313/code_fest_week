using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Adds a short entrance animation and a subtle idle motion to an in-game
/// tutorial hint. It uses unscaled time so its presentation stays independent
/// from gameplay speed.
/// </summary>
[DisallowMultipleComponent]
public class TutorialHintAnimator : MonoBehaviour
{
    [SerializeField] float entranceDuration = 0.35f;
    [SerializeField] float entranceStartScale = 0.68f;
    [SerializeField] float textEntranceOffset = 18.0f;
    [SerializeField] float floatingAmplitude = 5.0f;
    [SerializeField] float pulseAmplitude = 0.035f;
    [SerializeField] float tiltAmplitude = 2.0f;
    [SerializeField] float idleSpeed = 3.2f;

    RectTransform m_Icon;
    CanvasGroup m_CanvasGroup;
    RectTransform[] m_TextRects;
    Vector2[] m_BaseTextPositions;
    Vector2 m_BaseIconPosition;
    Vector3 m_BaseIconScale;
    Quaternion m_BaseIconRotation;
    float m_Elapsed;
    bool m_Configured;

    public void Configure(RectTransform icon)
    {
        m_Icon = icon;
        m_CanvasGroup = GetComponent<CanvasGroup>();
        if (m_CanvasGroup == null)
            m_CanvasGroup = gameObject.AddComponent<CanvasGroup>();

        Text[] texts = GetComponentsInChildren<Text>(true);
        m_TextRects = new RectTransform[texts.Length];
        m_BaseTextPositions = new Vector2[texts.Length];

        for (int i = 0; i < texts.Length; ++i)
        {
            m_TextRects[i] = texts[i].rectTransform;
            m_BaseTextPositions[i] = m_TextRects[i].anchoredPosition;
        }

        CacheIconTransform();
        m_Configured = m_Icon != null;
    }

    void OnEnable()
    {
        if (!m_Configured)
        {
            Transform iconTransform = transform.Find("Animation");
            Configure(iconTransform as RectTransform);
        }

        RestoreBaseState();
        m_Elapsed = 0.0f;

        if (m_CanvasGroup != null)
            m_CanvasGroup.alpha = 0.0f;
    }

    void OnDisable()
    {
        RestoreBaseState();
    }

    void Update()
    {
        if (!m_Configured || m_Icon == null)
            return;

        m_Elapsed += Time.unscaledDeltaTime;

        float entranceTime = Mathf.Max(0.01f, entranceDuration);
        float entranceT = Mathf.Clamp01(m_Elapsed / entranceTime);
        float smoothEntrance = entranceT * entranceT * (3.0f - 2.0f * entranceT);
        float bounceEntrance = BackOut(entranceT);

        if (m_CanvasGroup != null)
            m_CanvasGroup.alpha = smoothEntrance;

        float idleTime = Mathf.Max(0.0f, m_Elapsed - entranceTime);
        float idlePhase = idleTime * idleSpeed;
        float idleBlend = smoothEntrance;
        float floating = Mathf.Sin(idlePhase) * floatingAmplitude * idleBlend;
        float pulse = 1.0f + Mathf.Sin(idlePhase) * pulseAmplitude * idleBlend;
        float entranceScale = Mathf.Lerp(entranceStartScale, 1.0f, bounceEntrance);

        m_Icon.anchoredPosition = m_BaseIconPosition + Vector2.up * floating;
        m_Icon.localScale = m_BaseIconScale * (entranceScale * pulse);
        m_Icon.localRotation = m_BaseIconRotation * Quaternion.Euler(
            0.0f, 0.0f, Mathf.Sin(idlePhase * 0.7f) * tiltAmplitude * idleBlend);

        for (int i = 0; i < m_TextRects.Length; ++i)
        {
            if (m_TextRects[i] != null)
            {
                m_TextRects[i].anchoredPosition = m_BaseTextPositions[i] +
                    Vector2.down * ((1.0f - smoothEntrance) * textEntranceOffset);
            }
        }
    }

    void CacheIconTransform()
    {
        if (m_Icon == null)
            return;

        m_BaseIconPosition = m_Icon.anchoredPosition;
        m_BaseIconScale = m_Icon.localScale;
        m_BaseIconRotation = m_Icon.localRotation;
    }

    void RestoreBaseState()
    {
        if (m_Icon != null)
        {
            m_Icon.anchoredPosition = m_BaseIconPosition;
            m_Icon.localScale = m_BaseIconScale;
            m_Icon.localRotation = m_BaseIconRotation;
        }

        if (m_TextRects != null)
        {
            for (int i = 0; i < m_TextRects.Length; ++i)
            {
                if (m_TextRects[i] != null)
                    m_TextRects[i].anchoredPosition = m_BaseTextPositions[i];
            }
        }

        if (m_CanvasGroup != null)
            m_CanvasGroup.alpha = 1.0f;
    }

    static float BackOut(float t)
    {
        const float overshoot = 1.70158f;
        float shifted = t - 1.0f;
        return 1.0f + (overshoot + 1.0f) * shifted * shifted * shifted +
            overshoot * shifted * shifted;
    }
}
