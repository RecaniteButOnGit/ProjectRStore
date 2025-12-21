using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;

public class ItemNetworkManager : MonoBehaviourPunCallbacks
{
    public static ItemNetworkManager Instance { get; private set; }

    [Header("Local anchors (set automatically by HandGrabber, or assign)")]
    public Transform LocalLeftHandAnchor;
    public Transform LocalRightHandAnchor;

    [Header("Auto Item IDs (Scene Items Layer)")]
    [Tooltip("If true, scans all NetworkItem components whose root GameObject is on the 'Items' layer and ensures unique non-zero IDs.")]
    public bool AutoEnsureUniqueItemIDs = true;

    [Tooltip("If true, only fixes ItemID==0 or duplicates. If false, rewrites all IDs deterministically.")]
    public bool OnlyFixZerosAndDuplicates = true;

    [Tooltip("If true, auto-registers all scene items on 'Items' layer at Start.")]
    public bool AutoRegisterSceneItems = true;

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

    // HandGrabber reflection cache
    private static Type _handGrabberType;
    private static MethodInfo _hbApply_IntIntFloat;
    private static MethodInfo _hbApply_IntEnumFloat;
    private static Type _hbItemButtonEnumType;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        if (AutoEnsureUniqueItemIDs)
            EnsureUniqueIdsForItemsLayer();

        if (AutoRegisterSceneItems)
            AutoRegisterAllItemsLayer();
    }

    // =========================================================
    // BUTTON PRESS SYNC (HandGrabber calls this)
    // =========================================================

    /// <summary>
    /// Broadcast a held-item button press to other clients (sender runs locally already).
    /// buttonInt should match HandGrabber's internal enum values.
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
        // Avoid compile-time dependency on HandGrabber.ItemButton (fixes inconsistent accessibility issues).
        if (!InvokeHandGrabberApplyItemButtonLocal(itemId, buttonInt, value))
            LogWarn($"RPC_ItemButton: Could not call HandGrabber.ApplyItemButtonLocal for itemID={itemId} button={buttonInt}");

        Log($"RPC_ItemButton <- itemID={itemId} button={buttonInt} value={value}");
    }

    private bool InvokeHandGrabberApplyItemButtonLocal(int itemId, int buttonInt, float value)
    {
        try
        {
            CacheHandGrabberReflection();
            if (_handGrabberType == null) return false;

            // Preferred overload: ApplyItemButtonLocal(int itemId, int buttonInt, float value)
            if (_hbApply_IntIntFloat != null)
            {
                _hbApply_IntIntFloat.Invoke(null, new object[] { itemId, buttonInt, value });
                return true;
            }

            // Fallback overload: ApplyItemButtonLocal(int itemId, ItemButton enum, float value)
            if (_hbApply_IntEnumFloat != null && _hbItemButtonEnumType != null && _hbItemButtonEnumType.IsEnum)
            {
                object enumObj = Enum.ToObject(_hbItemButtonEnumType, buttonInt);
                _hbApply_IntEnumFloat.Invoke(null, new object[] { itemId, enumObj, value });
                return true;
            }
        }
        catch (Exception e)
        {
            LogWarn($"InvokeHandGrabberApplyItemButtonLocal exception: {e.GetType().Name} {e.Message}");
        }

        return false;
    }

    private void CacheHandGrabberReflection()
    {
        if (_handGrabberType != null) return;

        _handGrabberType = FindTypeByName("HandGrabber");
        if (_handGrabberType == null)
        {
            LogWarn("HandGrabber type not found (reflection). Button RPCs won't apply.");
            return;
        }

        // Try overload: (int,int,float)
        _hbApply_IntIntFloat = _handGrabberType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m =>
            {
                if (m.Name != "ApplyItemButtonLocal") return false;
                var p = m.GetParameters();
                return p.Length == 3 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(int) && p[2].ParameterType == typeof(float);
            });

        // Try overload: (int, enum, float)
        _hbApply_IntEnumFloat = _handGrabberType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m =>
            {
                if (m.Name != "ApplyItemButtonLocal") return false;
                var p = m.GetParameters();
                return p.Length == 3 && p[0].ParameterType == typeof(int) && p[2].ParameterType == typeof(float) && p[1].ParameterType.IsEnum;
            });

        if (_hbApply_IntEnumFloat != null)
            _hbItemButtonEnumType = _hbApply_IntEnumFloat.GetParameters()[1].ParameterType;
    }

    private static Type FindTypeByName(string typeName)
    {
        // Unity Type.GetType usually fails without assembly-qualified name, so scan assemblies.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = null;
            try { t = asm.GetTypes().FirstOrDefault(x => x.Name == typeName); }
            catch { /* ignore reflection type load issues */ }
            if (t != null) return t;
        }
        return null;
    }

    // =========================================================
    // ITEM REGISTRATION + ID MANAGEMENT
    // =========================================================

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

    private void AutoRegisterAllItemsLayer()
    {
        int itemsLayer = LayerMask.NameToLayer("Items");
        if (itemsLayer == -1)
        {
            LogWarn("Layer 'Items' not found. Create it in Unity (Tags & Layers).");
            return;
        }

        var all = FindObjectsOfType<NetworkItem>(true)
            .Where(ni => ni != null && ni.gameObject.layer == itemsLayer)
            .OrderBy(ni => BuildStableKey(ni.transform))
            .ToList();

        int count = 0;
        foreach (var ni in all)
        {
            // root should have it, but be forgiving
            Rigidbody rb = ni.GetComponent<Rigidbody>();
            if (rb == null) rb = ni.GetComponentInChildren<Rigidbody>(true);
            if (rb == null)
            {
                LogWarn($"AutoRegister: No Rigidbody found for item '{ni.name}' (ItemID={ni.ItemID}).");
                continue;
            }

            if (ni.ItemID != 0)
            {
                itemsById[ni.ItemID] = rb;
                count++;
            }
        }

        Log($"AutoRegisterSceneItems: registered {count}/{all.Count} items on 'Items' layer.");
    }

    private void EnsureUniqueIdsForItemsLayer()
    {
        int itemsLayer = LayerMask.NameToLayer("Items");
        if (itemsLayer == -1)
        {
            LogWarn("Layer 'Items' not found. Create it in Unity (Tags & Layers).");
            return;
        }

        var items = FindObjectsOfType<NetworkItem>(true)
            .Where(ni => ni != null && ni.gameObject.layer == itemsLayer)
            .Select(ni => new { ni, key = BuildStableKey(ni.transform) })
            .OrderBy(x => x.key, StringComparer.Ordinal)
            .ToList();

        if (items.Count == 0)
        {
            LogWarn("EnsureUniqueIds: No NetworkItem found on 'Items' layer.");
            return;
        }

        if (!OnlyFixZerosAndDuplicates)
        {
            // Rewrite ALL IDs deterministically: 1..N
            for (int i = 0; i < items.Count; i++)
                items[i].ni.ItemID = i + 1;

            Log($"EnsureUniqueIds: Rewrote all ItemIDs deterministically (1..{items.Count}).");
            return;
        }

        // Only fix zeros + duplicates, but do it deterministically.
        // Group by existing ItemID
        var groups = items.GroupBy(x => x.ni.ItemID).ToList();

        // IDs that are already unique and >0
        var used = new HashSet<int>(groups.Where(g => g.Key > 0 && g.Count() == 1).Select(g => g.Key));

        // Items that need a new ID (id==0 or duplicates beyond the “winner”)
        var needsNew = new List<(NetworkItem ni, string key)>();

        foreach (var g in groups)
        {
            int id = g.Key;

            if (id == 0)
            {
                foreach (var x in g) needsNew.Add((x.ni, x.key));
                continue;
            }

            if (g.Count() > 1)
            {
                // Keep the one with the smallest key, reassign the rest
                var ordered = g.OrderBy(x => x.key, StringComparer.Ordinal).ToList();
                var winner = ordered[0];

                // Winner keeps the ID (even if duplicate group) — but ensure it's counted as used
                used.Add(id);

                for (int i = 1; i < ordered.Count; i++)
                    needsNew.Add((ordered[i].ni, ordered[i].key));
            }
        }

        needsNew = needsNew.OrderBy(x => x.key, StringComparer.Ordinal).ToList();

        // Assign smallest available positive integers not in used
        int next = 1;
        int fixedCount = 0;

        foreach (var x in needsNew)
        {
            while (used.Contains(next)) next++;
            x.ni.ItemID = next;
            used.Add(next);
            next++;
            fixedCount++;
        }

        if (fixedCount > 0)
            Log($"EnsureUniqueIds: Fixed {fixedCount} ItemID(s) (zeros/duplicates) on 'Items' layer.");
        else
            Log("EnsureUniqueIds: All ItemIDs already unique & non-zero on 'Items' layer.");
    }

    private string BuildStableKey(Transform t)
    {
        // Deterministic key based on scene + full hierarchy path + sibling indices.
        // This is stable across clients as long as the scene hierarchy matches.
        var sceneName = t.gameObject.scene.IsValid() ? t.gameObject.scene.name : "NoScene";

        var parts = new List<string>(16);
        Transform cur = t;
        while (cur != null)
        {
            parts.Add($"{cur.GetSiblingIndex():D4}:{cur.name}");
            cur = cur.parent;
        }
        parts.Reverse();

        return sceneName + "|" + string.Join("/", parts);
    }

    // =========================================================
    // HOLD / RELEASE SYNC
    // =========================================================

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
