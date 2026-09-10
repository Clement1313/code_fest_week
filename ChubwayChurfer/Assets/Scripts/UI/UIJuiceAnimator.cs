using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lightweight, reusable UI motion used by the menu, HUD and game-over screen.
/// All animation uses unscaled time so it remains fluid while gameplay is paused.
/// </summary>
[DisallowMultipleComponent]
public class UIJuiceAnimator : MonoBehaviour
{
    public enum Style
    {
        PopFloat,
        SlideFromRight,
        StaggeredPop,
        PulseOnTextChange,
        Heartbeat
    }

    RectTransform m_Rect;
    CanvasGroup m_Group;
    Text m_Text;
    Vector2 m_BasePosition;
    Vector3 m_BaseScale;
    Quaternion m_BaseRotation;
    Style m_Style;
    float m_Delay;
    float m_Elapsed;
    float m_Punch;
    float m_LastPunchTime;
    string m_PreviousText;
    bool m_Configured;

    public void Configure(Style style, float delay = 0.0f)
    {
        if (!m_Configured)
        {
            m_Rect = transform as RectTransform;
            if (m_Rect == null)
                return;

            m_BasePosition = m_Rect.anchoredPosition;
            m_BaseScale = m_Rect.localScale;
            m_BaseRotation = m_Rect.localRotation;
            m_Group = GetComponent<CanvasGroup>();
            if (m_Group == null)
                m_Group = gameObject.AddComponent<CanvasGroup>();
            m_Text = GetComponent<Text>();
            m_Configured = true;
        }
        else
        {
            RestoreBaseState();
        }

        m_Style = style;
        m_Delay = Mathf.Max(0.0f, delay);
        Restart();
    }

    public void Punch()
    {
        m_Punch = 1.0f;
        m_LastPunchTime = Time.unscaledTime;
    }

    void OnEnable()
    {
        if (m_Configured)
            Restart();
    }

    void OnDisable()
    {
        RestoreBaseState();
    }

    void Restart()
    {
        m_Elapsed = 0.0f;
        m_Punch = 0.0f;
        m_LastPunchTime = -10.0f;
        m_PreviousText = m_Text != null ? m_Text.text : string.Empty;
        if (m_Group != null)
            m_Group.alpha = 0.0f;
    }

    void Update()
    {
        if (!m_Configured || m_Rect == null)
            return;

        m_Elapsed += Time.unscaledDeltaTime;
        float localTime = m_Elapsed - m_Delay;
        if (localTime < 0.0f)
        {
            m_Group.alpha = 0.0f;
            return;
        }

        const float entranceDuration = 0.42f;
        float entranceT = Mathf.Clamp01(localTime / entranceDuration);
        float smooth = entranceT * entranceT * (3.0f - 2.0f * entranceT);
        float bounce = BackOut(entranceT);
        m_Group.alpha = smooth;

        Vector2 position = m_BasePosition;
        Vector3 scale = m_BaseScale;
        Quaternion rotation = m_BaseRotation;

        switch (m_Style)
        {
            case Style.PopFloat:
            {
                float phase = Mathf.Max(0.0f, localTime - entranceDuration) * 1.8f;
                position += Vector2.down * ((1.0f - smooth) * 30.0f);
                position += Vector2.up * (Mathf.Sin(phase) * 3.5f * smooth);
                scale *= Mathf.Lerp(0.72f, 1.0f, bounce) * (1.0f + Mathf.Sin(phase) * 0.012f * smooth);
                rotation *= Quaternion.Euler(0.0f, 0.0f, Mathf.Sin(phase * 0.7f) * 0.7f * smooth);
                break;
            }
            case Style.SlideFromRight:
            {
                float phase = Mathf.Max(0.0f, localTime - entranceDuration) * 1.4f;
                position += Vector2.right * ((1.0f - smooth) * 120.0f);
                position += Vector2.up * (Mathf.Sin(phase) * 2.0f * smooth);
                scale *= Mathf.Lerp(0.96f, 1.0f, smooth);
                break;
            }
            case Style.StaggeredPop:
                position += Vector2.right * ((1.0f - smooth) * 26.0f);
                scale *= Mathf.Lerp(0.78f, 1.0f, bounce);
                break;
            case Style.PulseOnTextChange:
                UpdateTextPunch(0.12f);
                scale *= Mathf.Lerp(0.82f, 1.0f, bounce) * (1.0f + EaseOut(m_Punch) * 0.13f);
                break;
            case Style.Heartbeat:
            {
                float phase = Mathf.Max(0.0f, localTime - entranceDuration) * 2.6f;
                float beat = Mathf.Pow(Mathf.Max(0.0f, Mathf.Sin(phase)), 8.0f);
                scale *= Mathf.Lerp(0.78f, 1.0f, bounce) * (1.0f + beat * 0.045f);
                break;
            }
        }

        m_Punch = Mathf.MoveTowards(m_Punch, 0.0f, Time.unscaledDeltaTime * 4.5f);
        m_Rect.anchoredPosition = position;
        m_Rect.localScale = scale;
        m_Rect.localRotation = rotation;
    }

    void UpdateTextPunch(float minimumInterval)
    {
        if (m_Text == null || m_Text.text == m_PreviousText)
            return;

        m_PreviousText = m_Text.text;
        if (Time.unscaledTime - m_LastPunchTime >= minimumInterval)
            Punch();
    }

    void RestoreBaseState()
    {
        if (!m_Configured || m_Rect == null)
            return;

        m_Rect.anchoredPosition = m_BasePosition;
        m_Rect.localScale = m_BaseScale;
        m_Rect.localRotation = m_BaseRotation;
        if (m_Group != null)
            m_Group.alpha = 1.0f;
    }

    static float BackOut(float t)
    {
        const float overshoot = 1.70158f;
        float shifted = t - 1.0f;
        return 1.0f + (overshoot + 1.0f) * shifted * shifted * shifted + overshoot * shifted * shifted;
    }

    static float EaseOut(float t)
    {
        float inverse = 1.0f - Mathf.Clamp01(t);
        return 1.0f - inverse * inverse;
    }
}
