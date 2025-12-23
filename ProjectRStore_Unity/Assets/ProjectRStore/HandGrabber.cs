using System;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

public class HandGrabber : MonoBehaviourPun
{
    public enum GrabberMode
    {
        LocalHand,       // Put on local rig hand trigger (input + detect + send RPCs)
        MirrorRigRoot    // Put on networked mirror rig root (anchors + receives RPCs)
    }

    [Header("Mode")]
    public GrabberMode Mode = GrabberMode.LocalHand;

    // ---------------------------
    // LOCAL HAND (Local rig)
    // ---------------------------
    [Header("Local Hand (LocalHand mode)")]
    public bool IsLeftHand = true;

    [Tooltip("Local rig anchor for THIS hand (usually your local ItemAnchor).")]
    public Transform LocalHandAnchor;

    public string ItemsLayerName = "Items";

    [Header("Release / Throw")]
    public bool SendReleaseVelocity = true;

    [Header("Debug")]
    public bool DebugLogs = true;

    private int itemsLayer = -1;
    private readonly List<Rigidbody> nearby = new();
    private int heldItemId = 0;

    // throw estimate from local anchor motion
    private Vector3 lastAnchorPos;
    private Quaternion lastAnchorRot;
    private Vector3 anchorVel;
    private Vector3 anchorAngVel;

    // ---------------------------
    // MIRROR RIG ROOT (Networked rig)
    // ---------------------------
    [Header("Mirror Rig (MirrorRigRoot mode)")]
    [Tooltip("Mirror rig left ItemAnchor (exists on the multiplayer rig).")]
    public Transform LeftItemAnchor;

    [Tooltip("Mirror rig right ItemAnchor (exists on the multiplayer rig).")]
    public Transform RightItemAnchor;

    [Tooltip("Only the local player's mirror rig drives held items each frame on THIS client.")]
    public bool DriveHeldItems = true;

    // actor -> mirror rig root
    private static readonly Dictionary<int, HandGrabber> mirrorRigByActor = new();

    // itemId -> held state (shared per-client)
    private static readonly Dictionary<int, HeldState> heldByItemId = new();

    // cache: itemId -> NetworkItem
    private static readonly Dictionary<int, NetworkItem> itemCache = new();
    private static float lastCacheTime = -999f;

    [Serializable]
    private class HeldState
    {
        public int actorNr;
        public bool isLeftHand;

        public Vector3 offsetLocalPos;
        public Quaternion offsetLocalRot;

        public NetworkItem item;
        public Rigidbody rb;

        public bool prevKinematic;
        public bool prevUseGravity;
    }

    // ---------------------------
    // UNITY
    // ---------------------------

    private void Awake()
    {
        if (Mode == GrabberMode.LocalHand)
        {
            itemsLayer = LayerMask.NameToLayer(ItemsLayerName);
            if (itemsLayer == -1) Log($"❌ Layer '{ItemsLayerName}' not found.");

            var col = GetComponent<Collider>();
            if (col && !col.isTrigger) Log("⚠️ Your hand collider should be IsTrigger.");

            if (LocalHandAnchor)
            {
                lastAnchorPos = LocalHandAnchor.position;
                lastAnchorRot = LocalHandAnchor.rotation;
            }
            else
            {
                Log("⚠️ Assign LocalHandAnchor (your local rig ItemAnchor for this hand).");
            }
        }
        else // MirrorRigRoot
        {
            if (!photonView)
            {
                Debug.LogError("[HandGrabber] MirrorRigRoot mode requires a PhotonView on the SAME object.", this);
                enabled = false;
                return;
            }

            mirrorRigByActor[photonView.OwnerActorNr] = this;

            if (DebugLogs)
                Debug.Log($"[HandGrabber] Registered mirror rig for actor {photonView.OwnerActorNr} | mine={photonView.IsMine}", this);

            RebuildItemCache(force: true);
        }
    }

    private void OnDestroy()
    {
        if (Mode == GrabberMode.MirrorRigRoot && photonView)
        {
            if (mirrorRigByActor.TryGetValue(photonView.OwnerActorNr, out var cur) && cur == this)
                mirrorRigByActor.Remove(photonView.OwnerActorNr);
        }
    }

    private void Update()
    {
        if (Mode != GrabberMode.LocalHand) return;
        if (!LocalHandAnchor) return;

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        anchorVel = (LocalHandAnchor.position - lastAnchorPos) / dt;
        anchorAngVel = AngularVelocity(lastAnchorRot, LocalHandAnchor.rotation, dt);

        lastAnchorPos = LocalHandAnchor.position;
        lastAnchorRot = LocalHandAnchor.rotation;
    }

    private void LateUpdate()
    {
        // Only the local client's OWN mirror rig drives items (one driver per client)
        if (Mode != GrabberMode.MirrorRigRoot) return;
        if (!DriveHeldItems) return;
        if (!photonView || !photonView.IsMine) return;

        DriveAllHeldItems();
    }

    // ---------------------------
    // PUBLIC INPUT (call from buttons / XR / whatever)
    // ---------------------------

    // Grab toggle (one button)
    public void PrimaryButton()
    {
        if (Mode != GrabberMode.LocalHand) return;

        if (heldItemId == 0) TryGrab();
        else Release();
    }

    public void ForceRelease()
    {
        if (Mode != GrabberMode.LocalHand) return;
        if (heldItemId != 0) Release();
    }

    // ---------------------------
    // GRAB / RELEASE (LOCAL HAND)
    // ---------------------------

    private void TryGrab()
    {
        if (!LocalHandAnchor) { Log("TryGrab: no LocalHandAnchor."); return; }

        var myMirrorRig = GetLocalMirrorRigMine();
        if (!myMirrorRig)
        {
            Log("TryGrab: no local mirror rig found (are you in a lobby / did the mirror rig spawn?).");
            return;
        }

        CleanupNulls();
        if (nearby.Count == 0) { Log("TryGrab: no nearby items."); return; }

        Rigidbody rb = GetClosest();
        if (!rb) return;

        var ni = rb.GetComponentInParent<NetworkItem>();
        if (!ni || ni.ItemID == 0) { Log($"TryGrab: '{rb.name}' missing NetworkItem/ItemID."); return; }

        // Offset stored in LOCAL HAND ANCHOR SPACE (so mirror rig can recreate exact hold pose)
        Vector3 offsetLocalPos = Quaternion.Inverse(LocalHandAnchor.rotation) * (rb.position - LocalHandAnchor.position);
        Quaternion offsetLocalRot = Quaternion.Inverse(LocalHandAnchor.rotation) * rb.rotation;

        heldItemId = ni.ItemID;

        // RPC #1: Grab
        myMirrorRig.photonView.RPC(nameof(RPC_Grab), RpcTarget.All,
            heldItemId,
            PhotonNetwork.LocalPlayer.ActorNumber,
            IsLeftHand,
            offsetLocalPos,
            offsetLocalRot
        );

        Log($"Grab -> item={heldItemId}");
    }

    private void Release()
    {
        var myMirrorRig = GetLocalMirrorRigMine();
        if (!myMirrorRig)
        {
            Log("Release: no local mirror rig found. Clearing local held state anyway.");
            heldItemId = 0;
            return;
        }

        Vector3 vel = SendReleaseVelocity ? anchorVel : Vector3.zero;
        Vector3 ang = SendReleaseVelocity ? anchorAngVel : Vector3.zero;

        // RPC #2: Release
        myMirrorRig.photonView.RPC(nameof(RPC_Release), RpcTarget.All,
            heldItemId,
            vel,
            ang
        );

        Log($"Release -> item={heldItemId}");
        heldItemId = 0;
    }

    // ---------------------------
    // RPCs (on MirrorRigRoot instances across clients)
    // ---------------------------

    [PunRPC]
    private void RPC_Grab(int itemId, int actorNr, bool isLeftHand, Vector3 offsetLocalPos, Quaternion offsetLocalRot)
    {
        if (itemId == 0) return;

        var item = FindItem(itemId);
        var rb = item ? item.GetComponent<Rigidbody>() : null;

        var hs = new HeldState
        {
            actorNr = actorNr,
            isLeftHand = isLeftHand,
            offsetLocalPos = offsetLocalPos,
            offsetLocalRot = offsetLocalRot,
            item = item,
            rb = rb,
            prevKinematic = rb ? rb.isKinematic : true,
            prevUseGravity = rb ? rb.useGravity : false
        };

        heldByItemId[itemId] = hs;

        if (rb)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        if (DebugLogs)
            Debug.Log($"[HandGrabber] RPC_Grab item={itemId} actor={actorNr} hand={(isLeftHand ? "L" : "R")}", this);
    }

    [PunRPC]
    private void RPC_Release(int itemId, Vector3 velocity, Vector3 angularVelocity)
    {
        if (itemId == 0) return;
        if (!heldByItemId.TryGetValue(itemId, out var hs) || hs == null) return;

        heldByItemId.Remove(itemId);

        // reacquire if needed
        if (hs.item == null)
        {
            hs.item = FindItem(itemId);
            hs.rb = hs.item ? hs.item.GetComponent<Rigidbody>() : null;
        }

        if (hs.rb)
        {
            hs.rb.isKinematic = hs.prevKinematic;
            hs.rb.useGravity = hs.prevUseGravity;

            // optional throw sync
            hs.rb.velocity = velocity;
            hs.rb.angularVelocity = angularVelocity;
        }

        if (DebugLogs)
            Debug.Log($"[HandGrabber] RPC_Release item={itemId}", this);
    }

    // ---------------------------
    // DRIVE HELD ITEMS (done by local client's mirror rig that is mine)
    // ---------------------------

    private static void DriveAllHeldItems()
    {
        if (heldByItemId.Count == 0) return;

        List<int> remove = null;

        foreach (var kv in heldByItemId)
        {
            int itemId = kv.Key;
            var hs = kv.Value;
            if (hs == null)
            {
                (remove ??= new List<int>()).Add(itemId);
                continue;
            }

            // Find the holder's mirror rig anchors
            if (!mirrorRigByActor.TryGetValue(hs.actorNr, out var rig) || !rig) continue;

            Transform anchor = hs.isLeftHand ? rig.LeftItemAnchor : rig.RightItemAnchor;
            if (!anchor) continue;

            // reacquire item if needed (spawned later / cache missed)
            if (hs.item == null)
            {
                hs.item = FindItem(itemId);
                hs.rb = hs.item ? hs.item.GetComponent<Rigidbody>() : null;

                if (hs.rb)
                {
                    hs.rb.isKinematic = true;
                    hs.rb.useGravity = false;
                    hs.rb.velocity = Vector3.zero;
                    hs.rb.angularVelocity = Vector3.zero;
                }
            }

            if (!hs.item) continue;

            // Position = anchor + rotated offset
            var t = hs.item.transform;
            t.position = anchor.position + (anchor.rotation * hs.offsetLocalPos);
            t.rotation = anchor.rotation * hs.offsetLocalRot;
        }

        if (remove != null)
            foreach (var id in remove)
                heldByItemId.Remove(id);
    }

    // ---------------------------
    // ITEM FINDING (no PhotonView required on items)
    // ---------------------------

    private static NetworkItem FindItem(int itemId)
    {
        if (itemCache.TryGetValue(itemId, out var ni) && ni) return ni;

        RebuildItemCache(force: false);
        if (itemCache.TryGetValue(itemId, out ni) && ni) return ni;

        RebuildItemCache(force: true);
        itemCache.TryGetValue(itemId, out ni);
        return ni;
    }

    private static void RebuildItemCache(bool force)
    {
        if (!force && Time.time - lastCacheTime < 0.5f) return;

        itemCache.Clear();
        foreach (var ni in FindObjectsOfType<NetworkItem>(true))
        {
            if (!ni || ni.ItemID == 0) continue;
            itemCache[ni.ItemID] = ni;
        }

        lastCacheTime = Time.time;
    }

    private static HandGrabber GetLocalMirrorRigMine()
    {
        foreach (var rig in mirrorRigByActor.Values)
        {
            if (rig && rig.photonView && rig.photonView.IsMine)
                return rig;
        }
        return null;
    }

    // ---------------------------
    // TRIGGER DETECTION (LOCAL HAND)
    // ---------------------------

    private void OnTriggerEnter(Collider other)
    {
        if (Mode != GrabberMode.LocalHand) return;
        if (itemsLayer == -1) return;
        if (other.gameObject.layer != itemsLayer) return;

        var rb = other.attachedRigidbody;
        if (!rb) return;

        if (!nearby.Contains(rb))
            nearby.Add(rb);
    }

    private void OnTriggerExit(Collider other)
    {
        if (Mode != GrabberMode.LocalHand) return;

        var rb = other.attachedRigidbody;
        if (rb) nearby.Remove(rb);
    }

    private void CleanupNulls()
    {
        for (int i = nearby.Count - 1; i >= 0; i--)
            if (nearby[i] == null) nearby.RemoveAt(i);
    }

    private Rigidbody GetClosest()
    {
        Rigidbody best = null;
        float bestDist = float.MaxValue;
        Vector3 p = transform.position;

        foreach (var rb in nearby)
        {
            if (!rb) continue;
            float d = Vector3.Distance(p, rb.worldCenterOfMass);
            if (d < bestDist) { bestDist = d; best = rb; }
        }
        return best;
    }

    // ---------------------------
    // MATH + LOG
    // ---------------------------

    private static Vector3 AngularVelocity(Quaternion from, Quaternion to, float dt)
    {
        Quaternion dq = to * Quaternion.Inverse(from);
        dq.ToAngleAxis(out float angle, out Vector3 axis);
        if (float.IsNaN(axis.x)) return Vector3.zero;
        if (angle > 180f) angle -= 360f;
        return axis * (angle * Mathf.Deg2Rad / dt);
    }

    private void Log(string msg)
    {
        if (DebugLogs) Debug.Log($"[HandGrabber {(IsLeftHand ? "L" : "R")}] {msg}", this);
    }

#if UNITY_EDITOR
    [UnityEditor.CustomEditor(typeof(HandGrabber))]
    public class HandGrabberEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var hg = (HandGrabber)target;

            if (hg.Mode == GrabberMode.LocalHand)
            {
                UnityEditor.EditorGUILayout.Space(8);
                UnityEditor.EditorGUILayout.LabelField("Test", UnityEditor.EditorStyles.boldLabel);
                if (GUILayout.Button("PrimaryButton() (Grab/Release Toggle)"))
                    hg.PrimaryButton();
            }
        }
    }
#endif
}
