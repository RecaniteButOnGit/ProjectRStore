using System.Collections;
using Photon.Pun;
using UnityEngine;

/// <summary>
/// DeathCam (LOCAL RIG)
/// - Death(): toggles local objects (death cam/UI)
/// - Finds your OWNED mirror Photon rig at runtime (RagdollDeath with photonView.IsMine)
/// - Calls TriggerRagdollDeath() on that rig (which RPCs to everyone via your RagdollDeath script)
/// - After MoveDelay: moves MoveTarget to MoveWorldPosition (default 0,5,0)
/// - After RevertDelay: reverts the toggles (and optionally restores moved object)
///
/// Save as: DeathCam.cs
/// </summary>
public class DeathCam : MonoBehaviour
{
    [Header("Local toggle on death")]
    public GameObject ObjectToDisable;
    public GameObject ObjectToEnable;

    [Header("Move something mid-death")]
    public Transform MoveTarget;
    public float MoveDelay = 2.5f;
    public Vector3 MoveWorldPosition = new Vector3(0f, 5f, 0f);

    [Tooltip("If true, restore MoveTarget position when reverting.")]
    public bool RestoreMovedObjectOnRevert = false;

    [Header("Revert timing")]
    public float RevertDelay = 5f;

    [Header("Finding your owned mirror rig")]
    [Tooltip("How long to keep searching for your spawned mirror rig after Death() is called.")]
    public float FindOwnedRigTimeout = 2.0f;

    [Tooltip("How often to retry searching while waiting for your mirror rig to spawn.")]
    public float FindRetryInterval = 0.1f;

    [Header("Debug")]
    public bool DebugLogs = false;

    private Coroutine _routine;

    private bool _prevDisableActive;
    private bool _prevEnableActive;
    private Vector3 _prevMovePos;
    private bool _hasPrevMovePos;

    private RagdollDeath _cachedOwnedRagdoll;

    /// <summary>
    /// Public void to call when the player dies.
    /// </summary>
    public void Death()
    {
        if (_routine != null) StopCoroutine(_routine);
        _routine = StartCoroutine(DeathRoutine());
    }

    private IEnumerator DeathRoutine()
    {
        // Cache current states so we can truly revert
        if (ObjectToDisable != null) _prevDisableActive = ObjectToDisable.activeSelf;
        if (ObjectToEnable != null) _prevEnableActive = ObjectToEnable.activeSelf;

        // Toggle immediately (LOCAL ONLY)
        if (ObjectToDisable != null) ObjectToDisable.SetActive(false);
        if (ObjectToEnable != null) ObjectToEnable.SetActive(true);

        // Find & trigger your owned mirror rig ragdoll
        yield return StartCoroutine(FindAndTriggerOwnedMirrorRagdoll());

        // After MoveDelay, move target
        if (MoveTarget != null && MoveDelay > 0f)
        {
            yield return new WaitForSeconds(MoveDelay);

            if (MoveTarget != null)
            {
                _prevMovePos = MoveTarget.position;
                _hasPrevMovePos = true;

                MoveTarget.position = MoveWorldPosition;

                if (DebugLogs) Debug.Log($"[DeathCam] Moved {MoveTarget.name} to {MoveWorldPosition}", this);
            }
        }

        // Wait the rest of the time until revert (relative to start)
        float remaining = RevertDelay - Mathf.Max(0f, MoveDelay);
        if (remaining > 0f)
            yield return new WaitForSeconds(remaining);

        // Revert toggles
        if (ObjectToDisable != null) ObjectToDisable.SetActive(_prevDisableActive);
        if (ObjectToEnable != null) ObjectToEnable.SetActive(_prevEnableActive);

        // Optional: restore moved object
        if (RestoreMovedObjectOnRevert && MoveTarget != null && _hasPrevMovePos)
            MoveTarget.position = _prevMovePos;

        if (DebugLogs) Debug.Log("[DeathCam] Reverted.", this);

        _routine = null;
    }

    private IEnumerator FindAndTriggerOwnedMirrorRagdoll()
    {
        // If we already cached one, try it first
        if (IsValidOwned(_cachedOwnedRagdoll))
        {
            _cachedOwnedRagdoll.TriggerRagdollDeath();
            if (DebugLogs) Debug.Log($"[DeathCam] Used cached owned mirror rig: {_cachedOwnedRagdoll.name}", this);
            yield break;
        }

        float t = 0f;
        while (t < FindOwnedRigTimeout)
        {
            var found = FindOwnedMirrorRagdollDeath();
            if (IsValidOwned(found))
            {
                _cachedOwnedRagdoll = found;
                _cachedOwnedRagdoll.TriggerRagdollDeath();

                if (DebugLogs) Debug.Log($"[DeathCam] Found owned mirror rig: {_cachedOwnedRagdoll.name}", this);
                yield break;
            }

            yield return new WaitForSeconds(FindRetryInterval);
            t += FindRetryInterval;
        }

        if (DebugLogs) Debug.LogWarning("[DeathCam] Could not find owned mirror rig RagdollDeath (timed out).", this);
    }

    private RagdollDeath FindOwnedMirrorRagdollDeath()
    {
        // Scan all spawned RagdollDeath scripts (on every player's mirror rig)
        var all = FindObjectsOfType<RagdollDeath>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var rd = all[i];
            if (IsValidOwned(rd))
                return rd;
        }

        return null;
    }

    private bool IsValidOwned(RagdollDeath rd)
    {
        if (rd == null) return false;
        if (rd.photonView == null) return false;

        // Best check: IsMine
        if (rd.photonView.IsMine) return true;

        // Extra safety: OwnerActorNr match (covers some weird setups)
        if (PhotonNetwork.LocalPlayer != null && rd.photonView.OwnerActorNr == PhotonNetwork.LocalPlayer.ActorNumber)
            return true;

        return false;
    }
}
