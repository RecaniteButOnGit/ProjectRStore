using UnityEngine;

public class PlayAudioOnSecondary : MonoBehaviour
{
    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("If true, uses PlayOneShot(clip) instead of AudioSource.Play().")]
    [SerializeField] private bool useOneShot = false;
    [SerializeField] private AudioClip oneShotClip;

    [Header("Optional")]
    [SerializeField] private bool forceInitialStop = false;

    private void Start()
    {
        if (forceInitialStop)
        {
            GetSource().Stop();
        }
    }

    // Your HandGrabber calls public void Secondary()
    public void Secondary()
    {
        var src = GetSource();

        if (useOneShot)
        {
            var clip = oneShotClip != null ? oneShotClip : src.clip;
            if (clip == null)
            {
                Debug.LogWarning("[PlayAudioOnSecondary] No clip assigned for OneShot.", this);
                return;
            }

            src.PlayOneShot(clip);
            Debug.Log($"[PlayAudioOnSecondary] PlayOneShot '{clip.name}' on '{src.gameObject.name}'", this);
        }
        else
        {
            if (src.clip == null)
            {
                Debug.LogWarning("[PlayAudioOnSecondary] AudioSource has no clip.", this);
                return;
            }

            src.Play();
            Debug.Log($"[PlayAudioOnSecondary] Play '{src.clip.name}' on '{src.gameObject.name}'", this);
        }
    }

    private AudioSource GetSource()
    {
        if (audioSource != null) return audioSource;

        // Fallback: try on this object
        audioSource = GetComponent<AudioSource>();
        if (audioSource != null) return audioSource;

        // Last resort: add one (so it “just works”)
        audioSource = gameObject.AddComponent<AudioSource>();
        return audioSource;
    }
}
