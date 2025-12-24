using UnityEngine;
using Photon.Pun;
using Photon.Realtime;

public class PhotonOwnerToggle : MonoBehaviourPunCallbacks, IPunOwnershipCallbacks
{
    [Header("Object 1 (4 refs)")]
    public GameObject Object1_A;
    public GameObject Object1_B;
    public GameObject Object1_C;
    public GameObject Object1_D;

    [Header("Object 2")]
    public GameObject Object2;

    [Header("Debug")]
    public bool DebugLogs = false;

    private void Start() => Apply();
    private void OnEnable() => Apply();

    private void OnDestroy()
    {
        // Not required, but avoids edge-case callbacks on destroyed objects
        // (Photon registers callbacks via PunCallbacks base)
    }

    // --- IPunOwnershipCallbacks ---
    public void OnOwnershipRequest(PhotonView targetView, Player requestingPlayer) { }

    public void OnOwnershipTransfered(PhotonView targetView, Player previousOwner)
    {
        if (targetView == photonView)
            Apply();
    }

    public void OnOwnershipTransferFailed(PhotonView targetView, Player senderOfFailedRequest)
    {
        if (targetView == photonView && DebugLogs)
            Debug.LogWarning("[PhotonOwnerToggle] Ownership transfer failed.", this);

        // Still re-apply to be safe
        if (targetView == photonView)
            Apply();
    }

    // -----------------------------

    private void Apply()
    {
        bool iOwnThis = photonView != null && photonView.IsMine;

        // If I own it: Object1s OFF, Object2 ON
        SetActiveSafe(Object1_A, !iOwnThis);
        SetActiveSafe(Object1_B, !iOwnThis);
        SetActiveSafe(Object1_C, !iOwnThis);
        SetActiveSafe(Object1_D, !iOwnThis);

        SetActiveSafe(Object2, iOwnThis);

        if (DebugLogs)
            Debug.Log($"[PhotonOwnerToggle] IsMine={iOwnThis}", this);
    }

    private void SetActiveSafe(GameObject go, bool active)
    {
        if (!go) return;
        if (go.activeSelf != active) go.SetActive(active);
    }
}
