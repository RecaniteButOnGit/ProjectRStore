using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;

public class ItemNetworkManager : MonoBehaviourPunCallbacks
{
    public static ItemNetworkManager Instance { get; private set; }

    [Header("Local anchors (set automatically by HandGrabber, or assign)")]
    public Transform LocalLeftHandAnchor;
    public Transform LocalRightHandAnchor;

    [Header("Debug")]
    public bool DebugLogs = true;

    private readonly Dictionary<int, Rigidbody> itemsById = new();

    private class HeldState
    {
        public int actor;
        public bool isLeft;
        public Vector3 localPos;
        public Quaternion localRot;
    }

    // itemId -> held state
    private readonly Dictionary<int, HeldState> held = new();

    // actor -> mirror root cache
    private readonly Dictionary<int, Transform> mirrorRootByActor = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // =========================================================
    // NEW: BUTTON PRESS SYNC (HandGrabber calls this)
    // =========================================================

    /// <summary>
    /// Broadcast a held-item button press to other clients (sender runs locally already).
    /// buttonInt must match HandGrabber.ItemButton enum values.
    /// </summary>
    public void BroadcastItemButton(int itemId, int buttonInt, float value)
    {
        if (!PhotonNetwork.InRoom)
        {
            LogWarn($"BroadcastItemButton ignored (not in room). itemID={itemId}");
            return;
        }

        if (itemId == 0)
        {
            LogWarn("BroadcastItemButton ignored (itemId==0).");
            return;
        }

        photonView.RPC(nameof(RPC_ItemButton), RpcTarget.Others, itemId, buttonInt, value);
        Log($"BroadcastItemButton -> itemID={itemId} button={buttonInt} value={value}");
    }

    [PunRPC]
    private void RPC_ItemButton(int itemId, int buttonInt, float value)
    {
        // Run same modular scan on this client
        // NOTE: HandGrabber.ItemButton must be PUBLIC in HandGrabber for this cast to compile.
        HandGrabber.ApplyItemButtonLocal(itemId, (HandGrabber.ItemButton)buttonInt, value);
        Log($"RPC_ItemButton <- itemID={itemId} button={buttonInt} value={value}");
    }

    // Register items (call at spawn/start for preplaced)
    public void RegisterItem(NetworkItem ni, Rigidbody rb)
    {
        if (!ni || !rb) return;
        if (ni.ItemID == 0)
        {
            LogWarn($"RegisterItem: {rb.name} has ItemID=0 (won't sync).");
            return;
        }
        itemsById[ni.ItemID] = rb;
        Log($"Registered itemID={ni.ItemID} name={rb.name}");
    }

    public bool IsHeldByOther(int itemId, int myActor)
    {
        if (!held.TryGetValue(itemId, out var hs)) return false;
        return hs.actor != myActor;
    }

    public void GrabItem(int itemId, int actor, bool isLeft, Vector3 localPos, Quaternion localRot)
    {
        // deny if held by someone else
        if (held.TryGetValue(itemId, out var ex) && ex.actor != actor)
        {
            LogWarn($"Grab denied: itemID={itemId} already held by actor={ex.actor}");
            return;
        }

        ApplyGrabLocal(itemId, actor, isLeft, localPos, localRot);

        if (PhotonNetwork.InRoom)
            photonView.RPC(nameof(RPC_GrabItem), RpcTarget.Others, itemId, actor, isLeft, localPos, localRot);
    }

    public void ReleaseItem(int itemId, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel)
    {
        ApplyReleaseLocal(itemId, pos, rot, vel, angVel);

        if (PhotonNetwork.InRoom)
            photonView.RPC(nameof(RPC_ReleaseItem), RpcTarget.Others, itemId, pos, rot, vel, angVel);
    }

    [PunRPC]
    private void RPC_GrabItem(int itemId, int actor, bool isLeft, Vector3 localPos, Quaternion localRot)
    {
        // if already held by another, ignore
        if (held.TryGetValue(itemId, out var ex) && ex.actor != actor)
        {
            LogWarn($"RPC_Grab ignored: itemID={itemId} already held by actor={ex.actor}");
            return;
        }

        ApplyGrabLocal(itemId, actor, isLeft, localPos, localRot);
    }

    [PunRPC]
    private void RPC_ReleaseItem(int itemId, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel)
    {
        ApplyReleaseLocal(itemId, pos, rot, vel, angVel);
    }

    private void ApplyGrabLocal(int itemId, int actor, bool isLeft, Vector3 localPos, Quaternion localRot)
    {
        if (!itemsById.TryGetValue(itemId, out var rb) || rb == null)
        {
            LogWarn($"Grab: unknown itemID={itemId} (not registered).");
            return;
        }

        held[itemId] = new HeldState
        {
            actor = actor,
            isLeft = isLeft,
            localPos = localPos,
            localRot = localRot
        };

        // If this is NOT the local holder, parent to MIRROR rig anchor so it follows naturally.
        int myActor = PhotonNetwork.InRoom ? PhotonNetwork.LocalPlayer.ActorNumber : -999;

        if (actor != myActor)
        {
            Transform mirrorAnchor = GetMirrorHandAnchor(actor, isLeft);
            if (mirrorAnchor != null)
            {
                rb.isKinematic = true;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;

                rb.transform.SetParent(mirrorAnchor, false);
                rb.transform.localPosition = localPos;
                rb.transform.localRotation = localRot;

                Log($"Mirror-parented itemID={itemId} to actor={actor} {(isLeft ? "L" : "R")} anchor");
            }
            else
            {
                LogWarn($"No mirror anchor yet for actor={actor} (will retry in LateUpdate).");
            }
        }
        else
        {
            // Local holder: DO NOT parent or set transforms here.
            // Local joint system handles it.
            Log($"Local holder set for itemID={itemId} (joint handles local hold)");
        }
    }

    private void ApplyReleaseLocal(int itemId, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel)
    {
        if (!itemsById.TryGetValue(itemId, out var rb) || rb == null)
        {
            LogWarn($"Release: unknown itemID={itemId} (not registered).");
            return;
        }

        held.Remove(itemId);

        // Unparent from mirror if needed
        if (rb.transform.parent != null)
            rb.transform.SetParent(null, true);

        rb.isKinematic = false;
        rb.position = pos;
        rb.rotation = rot;
        rb.velocity = vel;
        rb.angularVelocity = angVel;

        Log($"Released itemID={itemId}");
    }

    // Retry mirror parenting if anchor didn't exist when RPC arrived
    private void LateUpdate()
    {
        if (!PhotonNetwork.InRoom || held.Count == 0) return;

        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;

        foreach (var kv in held)
        {
            int itemId = kv.Key;
            HeldState hs = kv.Value;

            if (!itemsById.TryGetValue(itemId, out var rb) || rb == null)
                continue;

            if (hs.actor == myActor)
                continue; // local joint handles local hold

            // If already parented correctly, no work
            Transform expected = GetMirrorHandAnchor(hs.actor, hs.isLeft);
            if (expected == null) continue;

            if (rb.transform.parent == expected)
                continue;

            // Parent now
            rb.isKinematic = true;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            rb.transform.SetParent(expected, false);
            rb.transform.localPosition = hs.localPos;
            rb.transform.localRotation = hs.localRot;

            Log($"LateUpdate mirror-parent fix: itemID={itemId} -> actor={hs.actor}");
        }
    }

    private Transform GetMirrorHandAnchor(int actor, bool isLeft)
    {
        Transform root = GetMirrorRoot(actor);
        if (root == null) return null;

        Transform hand = root.Find(isLeft ? "LeftHand" : "RightHand");
        if (hand == null) return null;

        return hand.Find("ItemAnchor");
    }

    private Transform GetMirrorRoot(int actor)
    {
        if (!mirrorRootByActor.TryGetValue(actor, out var root) || root == null)
        {
            var players = GameObject.FindObjectsOfType<Photon.VR.Player.PhotonVRPlayer>(true);
            foreach (var p in players)
            {
                if (p == null || p.photonView == null || p.photonView.Owner == null) continue;
                mirrorRootByActor[p.photonView.Owner.ActorNumber] = p.transform;
            }
            mirrorRootByActor.TryGetValue(actor, out root);
        }
        return root;
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        if (otherPlayer == null) return;

        int actor = otherPlayer.ActorNumber;
        mirrorRootByActor.Remove(actor);

        // Free any items they were holding
        List<int> toFree = null;
        foreach (var kv in held)
            if (kv.Value.actor == actor) (toFree ??= new List<int>()).Add(kv.Key);

        if (toFree != null)
        {
            foreach (int itemId in toFree)
            {
                held.Remove(itemId);
                if (itemsById.TryGetValue(itemId, out var rb) && rb != null)
                {
                    rb.transform.SetParent(null, true);
                    rb.isKinematic = false;
                }
            }
        }
    }

    private void Log(string msg) { if (DebugLogs) Debug.Log($"[ItemNet] {msg}", this); }
    private void LogWarn(string msg) { if (DebugLogs) Debug.LogWarning($"[ItemNet] {msg}", this); }
}
