// UnityEventButton.cs
// Drop this on a "button" object and hook UnityEvents in the Inspector.
// Works great for physical VR buttons (trigger collider) AND quick mouse testing.

using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class UnityEventButton : MonoBehaviour
{
    [Header("Events")]
    public UnityEvent OnPressed;
    public UnityEvent OnReleased;

    [Header("Trigger Press Settings")]
    [Tooltip("If true, pressing uses OnTriggerEnter/Exit. Your collider should be isTrigger=true.")]
    public bool UseTrigger = true;

    [Tooltip("Layer filter for what can press this button.")]
    public LayerMask PressLayers = ~0;

    [Tooltip("Optional tag filter (leave empty to ignore).")]
    public string RequiredTag = "";

    [Tooltip("If true, button presses when something enters the trigger.")]
    public bool PressOnEnter = true;

    [Tooltip("If true, button releases when the presser exits the trigger.")]
    public bool ReleaseOnExit = true;

    [Header("Debounce")]
    [Tooltip("If true, it won't press again until it has been released.")]
    public bool PressOnceUntilRelease = true;

    [Tooltip("Minimum time between presses (seconds).")]
    public float Cooldown = 0.10f;

    private bool _isPressed;
    private float _nextAllowedTime;

    // Call this from anything (XR interactable events, animations, etc.)
    public void Press()
    {
        if (PressOnceUntilRelease && _isPressed) return;
        if (Time.time < _nextAllowedTime) return;

        _isPressed = true;
        _nextAllowedTime = Time.time + Mathf.Max(0f, Cooldown);

        OnPressed?.Invoke();
    }

    // Call this from anything if you want explicit release behavior.
    public void Release()
    {
        if (!_isPressed) return;
        _isPressed = false;
        OnReleased?.Invoke();
    }

    private bool PassesFilter(Collider other)
    {
        if (((1 << other.gameObject.layer) & PressLayers.value) == 0) return false;
        if (!string.IsNullOrEmpty(RequiredTag) && !other.CompareTag(RequiredTag)) return false;
        return true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!UseTrigger || !PressOnEnter) return;
        if (!PassesFilter(other)) return;
        Press();
    }

    private void OnTriggerExit(Collider other)
    {
        if (!UseTrigger || !ReleaseOnExit) return;
        if (!PassesFilter(other)) return;
        Release();
    }

    // Mouse testing in editor / desktop
    private void OnMouseDown()
    {
        // Optional: quick test presses with mouse click
        Press();
    }

    [ContextMenu("TEST: Press")]
    private void TestPress() => Press();

    [ContextMenu("TEST: Release")]
    private void TestRelease() => Release();
}
