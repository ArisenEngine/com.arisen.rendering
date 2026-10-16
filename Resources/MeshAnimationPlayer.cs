using System.Numerics;

namespace ArisenEngine.Rendering.Resources;

/// <summary>
/// Plays one clip of a cooked mesh's animation table at a time, cross-fading between two clips when
/// it switches - the runtime half of the clip contract, so a driver or a game picks names and timing
/// policy (loop or advance, fade length) instead of re-implementing blend state. Both clips keep
/// advancing through a fade, so a transition never freezes the pose it leaves, and once the fade
/// elapses the incoming clip is sampled exactly.
///
/// The player owns no pose storage: <see cref="SamplePose"/> fills caller spans, which is the same
/// shape the frame's pose arrays and the skin palette consume.
/// </summary>
public sealed class MeshAnimationPlayer
{
    private readonly MeshAnimationTable m_Table;
    private int m_CurrentIndex = -1;
    private float m_CurrentTime;
    private bool m_Loop = true;
    private bool m_Completed;
    private int m_PreviousIndex = -1;
    private float m_PreviousTime;
    private float m_FadeSeconds;
    private float m_FadeRemaining;

    public MeshAnimationPlayer(MeshAnimationTable table)
    {
        m_Table = table ?? throw new ArgumentNullException(nameof(table));
    }

    public MeshAnimationTable Table => m_Table;

    public bool IsPlaying => m_CurrentIndex >= 0;

    public int CurrentClipIndex => m_CurrentIndex;

    public float CurrentTime => m_CurrentTime;

    public bool IsLooping => m_Loop;

    public bool IsFading => m_PreviousIndex >= 0 && m_FadeRemaining > 0.0f;

    /// <summary>
    /// True on the frame a non-looping clip reached its end; the owner decides what plays next. The
    /// pose holds the clip's last frame until then.
    /// </summary>
    public bool CompletedThisFrame { get; private set; }

    public MeshAnimationClip CurrentClip => m_Table.GetClip(m_CurrentIndex);

    /// <summary>
    /// Switches to a clip, fading from whatever is playing. A zero fade cuts hard, and a fade from
    /// the clip already playing (a sequence wrapping onto itself) restarts it while blending from
    /// the pose it was holding.
    /// </summary>
    public void Play(int clipIndex, float fadeSeconds, bool loop)
    {
        if ((uint)clipIndex >= (uint)m_Table.ClipCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clipIndex),
                $"[MeshAnimationPlayer] Clip index '{clipIndex}' is outside the table's {m_Table.ClipCount} clips.");
        }

        float fade = MathF.Max(0.0f, fadeSeconds);
        if (IsPlaying && fade > 0.0f)
        {
            m_PreviousIndex = m_CurrentIndex;
            m_PreviousTime = m_CurrentTime;
            m_FadeSeconds = fade;
            m_FadeRemaining = fade;
        }
        else
        {
            m_PreviousIndex = -1;
            m_PreviousTime = 0.0f;
            m_FadeSeconds = 0.0f;
            m_FadeRemaining = 0.0f;
        }

        m_CurrentIndex = clipIndex;
        m_CurrentTime = 0.0f;
        m_Loop = loop;
        m_Completed = false;
        CompletedThisFrame = false;
    }

    /// <summary>Finds a clip by name - the cook's addressing rule - and plays it when it exists.</summary>
    public bool TryPlay(string clipName, float fadeSeconds, bool loop)
    {
        if (!m_Table.TryFindClip(clipName, out int clipIndex))
        {
            return false;
        }

        Play(clipIndex, fadeSeconds, loop);
        return true;
    }

    /// <summary>
    /// Advances the playing clip (and the outgoing one through a fade). A looping clip wraps; a
    /// non-looping clip clamps on its last frame and reports <see cref="CompletedThisFrame"/> once.
    /// </summary>
    public void Advance(float deltaTime)
    {
        CompletedThisFrame = false;
        if (!IsPlaying || deltaTime <= 0.0f)
        {
            return;
        }

        if (IsFading)
        {
            m_PreviousTime += deltaTime;
            float previousDuration = m_Table.GetClip(m_PreviousIndex).Duration;
            if (previousDuration > 0.0f && m_PreviousTime >= previousDuration)
            {
                m_PreviousTime %= previousDuration;
            }

            m_FadeRemaining = MathF.Max(0.0f, m_FadeRemaining - deltaTime);
            if (m_FadeRemaining <= 0.0f)
            {
                m_PreviousIndex = -1;
                m_PreviousTime = 0.0f;
                m_FadeSeconds = 0.0f;
            }
        }

        if (m_Completed)
        {
            return;
        }

        float duration = m_Table.GetClip(m_CurrentIndex).Duration;
        m_CurrentTime += deltaTime;
        if (duration <= 0.0f)
        {
            m_CurrentTime = 0.0f;
            return;
        }

        if (m_Loop)
        {
            m_CurrentTime %= duration;
            return;
        }

        if (m_CurrentTime >= duration)
        {
            m_CurrentTime = duration;
            m_Completed = true;
            CompletedThisFrame = true;
        }
    }

    /// <summary>
    /// Fills the caller's spans with the local pose the frame draws: the current clip alone, or the
    /// cross-fade of the outgoing and incoming clips while a fade is in flight.
    /// </summary>
    public void SamplePose(MeshSkin skin, Span<Quaternion> rotations, Span<Vector3> translations)
    {
        ArgumentNullException.ThrowIfNull(skin);
        if (!IsPlaying)
        {
            throw new InvalidOperationException(
                "[MeshAnimationPlayer] SamplePose needs a playing clip; call Play first.");
        }

        if (!IsFading)
        {
            MeshAnimationSampler.SamplePose(skin, m_Table, m_CurrentIndex, m_CurrentTime, rotations, translations);
            return;
        }

        float blend = 1.0f - m_FadeRemaining / m_FadeSeconds;
        MeshAnimationSampler.SampleBlendedPose(
            skin,
            m_Table,
            m_PreviousIndex,
            m_PreviousTime,
            m_CurrentIndex,
            m_CurrentTime,
            blend,
            rotations,
            translations);
    }

    public void Stop()
    {
        m_CurrentIndex = -1;
        m_CurrentTime = 0.0f;
        m_PreviousIndex = -1;
        m_PreviousTime = 0.0f;
        m_FadeSeconds = 0.0f;
        m_FadeRemaining = 0.0f;
        m_Completed = false;
        CompletedThisFrame = false;
    }
}
