using Photon.Pun;
using UnityEngine;

/// <summary>
/// DeathZone
/// - Put on a GameObject with a Collider set to IsTrigger.
/// - On trigger enter, finds PlayerStats IN THE SCENE and kills via Damage() (Health -> 0).
/// - Optional: only kill the LOCAL player (recommended for PUN).
///
/// Save as: DeathZone.cs
/// </summary>
[RequireComponent(typeof(Collider))]
public class DeathZone : MonoBehaviour
{
    [Header("Auto-find PlayerStats in scene")]
    [Tooltip("If left null, auto-finds the first PlayerStats in the scene (including inactive).")]
    public PlayerStats StatsInScene;

    [Header("Filters")]
    [Tooltip("If true, only kills the locally-owned player (recommended).")]
    public bool OnlyKillLocalPlayer = true;

    [Tooltip("Optional: require this tag on the entering collider OR its root (leave blank to ignore).")]
    public string RequiredTag = "Player";

    [Header("Kill Settings")]
    [Tooltip("Big number so Health definitely hits 0.")]
    public int KillDamage = 999999;

    [Header("Debug")]
    public bool DebugLogs = false;

    private void Reset()
    {
        var c = GetComponent<Collider>();
        if (c) c.isTrigger = true;
    }

    private void Awake()
    {
        EnsureStatsFound();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other == null) return;

        // Optional tag filter
        if (!string.IsNullOrWhiteSpace(RequiredTag))
        {
            var t = other.transform;
            if (!t.CompareTag(RequiredTag) && !t.root.CompareTag(RequiredTag))
                return;
        }

        // Multiplayer safety: only affect local player
        if (OnlyKillLocalPlayer)
        {
            PhotonView pv =
                other.GetComponentInParent<PhotonView>() ??
                other.transform.root.GetComponent<PhotonView>();

            // If we can identify ownership and it's not ours, ignore.
            if (pv != null && !pv.IsMine)
                return;
        }

        if (!EnsureStatsFound())
        {
            if (DebugLogs) Debug.LogWarning("[DeathZone] No PlayerStats found in scene.", this);
            return;
        }

        StatsInScene.Damage(KillDamage);

        if (DebugLogs) Debug.Log($"[DeathZone] Killed via PlayerStats.Damage({KillDamage}).", this);
    }

    private bool EnsureStatsFound()
    {
        if (StatsInScene != null) return true;

        // Includes inactive objects:
        var all = FindObjectsOfType<PlayerStats>(true);
        if (all != null && all.Length > 0)
        {
            StatsInScene = all[0];
            if (DebugLogs) Debug.Log($"[DeathZone] Auto-found PlayerStats: {StatsInScene.name}", this);
            return true;
        }

        return false;
    }
}
