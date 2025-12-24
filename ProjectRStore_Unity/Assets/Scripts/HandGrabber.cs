using System;
using System.Collections.Generic;
using System.Reflection;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.XR;

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

    [Header("Item Actions (Secondary/Trigger)")]
    [Tooltip("If true, calls ALL matching Secondary()/Trigger() methods found on the held item. If false, stops after the first one.")]
    public bool CallAllActionReceivers = false;

    [Tooltip("If true, searches inactive components too.")]
    public bool IncludeInactiveActionReceivers = true;

    [Header("Network Sync (Item Actions)")]
    [Tooltip("If true, Secondary()/Trigger() calls are RPC'd to other players so item actions stay in sync.")]
    public bool SyncItemActions = true;

    [Tooltip("If CallMode = WhileHeld, this limits how often we send action RPCs (seconds).")]
    [Range(0.02f, 0.5f)]
    public float WhileHeldActionNetInterval = 0.10f;

    // ---------------------------
    // XR INPUT (LocalHand mode)
    // ---------------------------
    public enum XRButton
    {
        Grip,
        Primary,
        Secondary,
        Trigger
    }

    public enum ActionCallMode
    {
        OnPress,
        WhileHeld
    }

    [Header("XR Input (LocalHand mode)")]
    [Tooltip("If ON, this script reads XR controller inputs directly (no other input script needed).")]
    public bool UseXRInput = true;

    [Tooltip("Button that toggles grab/release.")]
    public XRButton GrabToggleButton = XRButton.Grip;

    [Tooltip("Button that calls Secondary() on the held item.")]
    public XRButton SecondaryActionButton = XRButton.Secondary;

    [Tooltip("Button/trigger that calls Trigger() on the held item.")]
    public XRButton TriggerActionButton = XRButton.Trigger;

    [Tooltip("If TriggerButton isn't supported, trigger axis > threshold counts as pressed.")]
    [Range(0.05f, 0.99f)]
    public float TriggerAxisPressThreshold = 0.75f;

    [Tooltip("How Secondary() should fire.")]
    public ActionCallMode SecondaryCallMode = ActionCallMode.OnPress;

    [Tooltip("How Trigger() should fire.")]
    public ActionCallMode TriggerCallMode = ActionCallMode.OnPress;

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

    // XR edge detection
    private bool prevGrabPressed;
    private bool prevSecondaryPressed;
    private bool prevTriggerPressed;

    // XR device binder (device-based, reconnect-safe)
    private XRDeviceBinder xrBinder;

    // while-held rate limit timers
    private float nextSecondaryNetTime = 0f;
    private float nextTriggerNetTime = 0f;

    private enum ItemActionType : int
    {
        Secondary = 0,
        Trigger = 1
    }

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
    // XR DEVICE BINDER (reconnect-safe)
    // ---------------------------
    private class XRDeviceBinder
    {
        public InputDevice Device { get; private set; }

        private readonly bool _left;
        private static readonly List<InputDevice> _devices = new(8);

        public XRDeviceBinder(bool left) { _left = left; }

        public void Enable()
        {
            InputDevices.deviceConnected += OnAnyDeviceChanged;
            InputDevices.deviceDisconnected += OnAnyDeviceChanged;
            InputDevices.deviceConfigChanged += OnAnyDeviceChanged;
            Refresh();
        }

        public void Disable()
        {
            InputDevices.deviceConnected -= OnAnyDeviceChanged;
            InputDevices.deviceDisconnected -= OnAnyDeviceChanged;
            InputDevices.deviceConfigChanged -= OnAnyDeviceChanged;
            Device = default;
        }

        private void OnAnyDeviceChanged(InputDevice _) => Refresh();

        public void Refresh()
        {
            // 1) Best: XRNode endpoint (LeftHand/RightHand)
            _devices.Clear();
            InputDevices.GetDevicesAtXRNode(_left ? XRNode.LeftHand : XRNode.RightHand, _devices);
            for (int i = 0; i < _devices.Count; i++)
            {
                if (_devices[i].isValid)
                {
                    Device = _devices[i];
                    return;
                }
            }

            // 2) Fallback: Characteristics (strict-ish)
            if (TryCharacteristics(
                    InputDeviceCharacteristics.Controller |
                    InputDeviceCharacteristics.TrackedDevice |
                    InputDeviceCharacteristics.HeldInHand |
                    (_left ? InputDeviceCharacteristics.Left : InputDeviceCharacteristics.Right),
                    out var found))
            {
                Device = found;
                return;
            }

            // 3) Relaxed: Controller + Left/Right
            if (TryCharacteristics(
                    InputDeviceCharacteristics.Controller |
                    (_left ? InputDeviceCharacteristics.Left : InputDeviceCharacteristics.Right),
                    out found))
            {
                Device = found;
                return;
            }

            // 4) Most relaxed: Left/Right only
            if (TryCharacteristics(
                    (_left ? InputDeviceCharacteristics.Left : InputDeviceCharacteristics.Right),
                    out found))
            {
                Device = found;
                return;
            }

            Device = default;
        }

        private static bool TryCharacteristics(InputDeviceCharacteristics desired, out InputDevice best)
        {
            best = default;

            _devices.Clear();
            InputDevices.GetDevicesWithCharacteristics(desired, _devices);
            for (int i = 0; i < _devices.Count; i++)
            {
                if (_devices[i].isValid)
                {
                    best = _devices[i];
                    return true;
                }
            }
            return false;
        }
    }

    // ---------------------------
    // UNITY
    // ---------------------------

    private void OnEnable()
    {
        if (Mode == GrabberMode.LocalHand && UseXRInput)
        {
            xrBinder ??= new XRDeviceBinder(IsLeftHand);
            xrBinder.Enable();
        }
    }

    private void OnDisable()
    {
        if (Mode == GrabberMode.LocalHand && xrBinder != null)
            xrBinder.Disable();
    }

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

        if (UseXRInput)
            UpdateXRInput();

        if (!LocalHandAnchor) return;

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        anchorVel = (LocalHandAnchor.position - lastAnchorPos) / dt;
        anchorAngVel = AngularVelocity(lastAnchorRot, LocalHandAnchor.rotation, dt);

        lastAnchorPos = LocalHandAnchor.position;
        lastAnchorRot = LocalHandAnchor.rotation;
    }

    private void LateUpdate()
    {
        if (Mode != GrabberMode.MirrorRigRoot) return;
        if (!DriveHeldItems) return;
        if (!photonView || !photonView.IsMine) return;

        DriveAllHeldItems();
    }

    // ---------------------------
    // XR INPUT
    // ---------------------------

    private void UpdateXRInput()
    {
        if (xrBinder == null)
        {
            xrBinder = new XRDeviceBinder(IsLeftHand);
            xrBinder.Enable();
        }

        var dev = xrBinder.Device;

        if (!dev.isValid)
        {
            xrBinder.Refresh();
            dev = xrBinder.Device;
        }

        if (!dev.isValid)
            return;

        bool grabPressed = GetButtonPressed(dev, GrabToggleButton);
        bool secondaryPressed = GetButtonPressed(dev, SecondaryActionButton);
        bool triggerPressed = GetButtonPressed(dev, TriggerActionButton);

        // Grab toggle (always on press edge)
        if (grabPressed && !prevGrabPressed)
            PrimaryButton();

        // Secondary()
        if (SecondaryCallMode == ActionCallMode.OnPress)
        {
            if (secondaryPressed && !prevSecondaryPressed)
                SecondaryButton();
        }
        else
        {
            if (secondaryPressed)
                SecondaryButton();
        }

        // Trigger()
        if (TriggerCallMode == ActionCallMode.OnPress)
        {
            if (triggerPressed && !prevTriggerPressed)
                TriggerButton();
        }
        else
        {
            if (triggerPressed)
                TriggerButton();
        }

        prevGrabPressed = grabPressed;
        prevSecondaryPressed = secondaryPressed;
        prevTriggerPressed = triggerPressed;
    }

    private bool GetButtonPressed(InputDevice dev, XRButton button)
    {
        switch (button)
        {
            case XRButton.Grip:
                if (dev.TryGetFeatureValue(CommonUsages.gripButton, out bool gripBtn))
                    return gripBtn;
                if (dev.TryGetFeatureValue(CommonUsages.grip, out float gripAxis))
                    return gripAxis > 0.75f;
                return false;

            case XRButton.Primary:
                if (dev.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryBtn))
                    return primaryBtn;
                return false;

            case XRButton.Secondary:
                if (dev.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryBtn))
                    return secondaryBtn;
                return false;

            case XRButton.Trigger:
                if (dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigBtn))
                    return trigBtn;

                if (dev.TryGetFeatureValue(CommonUsages.trigger, out float trigAxis))
                    return trigAxis > TriggerAxisPressThreshold;

                return false;

            default:
                return false;
        }
    }

    // ---------------------------
    // PUBLIC INPUT (call from buttons / XR / whatever)
    // ---------------------------

    public void PrimaryButton()
    {
        if (Mode != GrabberMode.LocalHand) return;

        if (heldItemId == 0) TryGrab();
        else Release();
    }

    public void SecondaryButton()
    {
        if (Mode != GrabberMode.LocalHand) return;
        if (heldItemId == 0) return;

        // local immediate
        TryInvokeHeldItemVoid("Secondary");

        // sync to others
        TrySendItemAction(ItemActionType.Secondary);
    }

    public void TriggerButton()
    {
        if (Mode != GrabberMode.LocalHand) return;
        if (heldItemId == 0) return;

        // local immediate
        TryInvokeHeldItemVoid("Trigger");

        // sync to others
        TrySendItemAction(ItemActionType.Trigger);
    }

    public void ForceRelease()
    {
        if (Mode != GrabberMode.LocalHand) return;
        if (heldItemId != 0) Release();
    }

    private void TrySendItemAction(ItemActionType type)
    {
        if (!SyncItemActions) return;
        if (!PhotonNetwork.InRoom) return; // offline test: still works locally
        if (heldItemId == 0) return;

        // WhileHeld -> rate limit
        if (type == ItemActionType.Secondary && SecondaryCallMode == ActionCallMode.WhileHeld)
        {
            if (Time.time < nextSecondaryNetTime) return;
            nextSecondaryNetTime = Time.time + WhileHeldActionNetInterval;
        }
        if (type == ItemActionType.Trigger && TriggerCallMode == ActionCallMode.WhileHeld)
        {
            if (Time.time < nextTriggerNetTime) return;
            nextTriggerNetTime = Time.time + WhileHeldActionNetInterval;
        }

        var myMirrorRig = GetLocalMirrorRigMine();
        if (!myMirrorRig)
        {
            Log("ItemAction: no local mirror rig found (are you in a lobby / did the mirror rig spawn?).");
            return;
        }

        // Send to others only (we already ran locally)
        myMirrorRig.photonView.RPC(nameof(RPC_ItemAction), RpcTarget.Others,
            heldItemId,
            (int)type,
            CallAllActionReceivers,
            IncludeInactiveActionReceivers
        );

        if (DebugLogs)
            Debug.Log($"[HandGrabber {(IsLeftHand ? "L" : "R")}] Sent ItemAction {type} for item={heldItemId} to Others", this);
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

        Vector3 offsetLocalPos = Quaternion.Inverse(LocalHandAnchor.rotation) * (rb.position - LocalHandAnchor.position);
        Quaternion offsetLocalRot = Quaternion.Inverse(LocalHandAnchor.rotation) * rb.rotation;

        heldItemId = ni.ItemID;

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

        if (hs.item == null)
        {
            hs.item = FindItem(itemId);
            hs.rb = hs.item ? hs.item.GetComponent<Rigidbody>() : null;
        }

        if (hs.rb)
        {
            hs.rb.isKinematic = hs.prevKinematic;
            hs.rb.useGravity = hs.prevUseGravity;
            hs.rb.velocity = velocity;
            hs.rb.angularVelocity = angularVelocity;
        }

        if (DebugLogs)
            Debug.Log($"[HandGrabber] RPC_Release item={itemId}", this);
    }

    [PunRPC]
    private void RPC_ItemAction(int itemId, int actionType, bool callAll, bool includeInactive, PhotonMessageInfo info)
    {
        if (itemId == 0) return;

        // Only allow the ACTUAL holder to trigger actions for this item
        if (!heldByItemId.TryGetValue(itemId, out var hs) || hs == null)
            return;

        if (info.Sender == null || info.Sender.ActorNumber != hs.actorNr)
        {
            if (DebugLogs)
                Debug.LogWarning($"[HandGrabber] RPC_ItemAction rejected: sender={info.Sender?.ActorNumber} holder={hs.actorNr} item={itemId}", this);
            return;
        }

        string methodName = (ItemActionType)actionType == ItemActionType.Trigger ? "Trigger" : "Secondary";

        // reacquire if needed
        if (hs.item == null)
            hs.item = FindItem(itemId);

        if (!hs.item)
        {
            if (DebugLogs)
                Debug.LogWarning($"[HandGrabber] RPC_ItemAction: item {itemId} not found on this client.", this);
            return;
        }

        InvokeItemVoidOnBehaviours(hs.item, methodName, callAll, includeInactive);

        if (DebugLogs)
            Debug.Log($"[HandGrabber] RPC_ItemAction {methodName}() item={itemId} from actor={hs.actorNr}", this);
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

            if (!mirrorRigByActor.TryGetValue(hs.actorNr, out var rig) || !rig) continue;

            Transform anchor = hs.isLeftHand ? rig.LeftItemAnchor : rig.RightItemAnchor;
            if (!anchor) continue;

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

            var t = hs.item.transform;
            t.position = anchor.position + (anchor.rotation * hs.offsetLocalPos);
            t.rotation = anchor.rotation * hs.offsetLocalRot;
        }

        if (remove != null)
            foreach (var id in remove)
                heldByItemId.Remove(id);
    }

    // ---------------------------
    // ITEM ACTION INVOKE (LOCAL + REMOTE)
    // ---------------------------

    private void TryInvokeHeldItemVoid(string methodName)
    {
        if (heldItemId == 0) return;

        var item = FindItem(heldItemId);
        if (!item)
        {
            Log($"Action '{methodName}': held item {heldItemId} not found.");
            return;
        }

        bool invokedAny = InvokeItemVoidOnBehaviours(item, methodName, CallAllActionReceivers, IncludeInactiveActionReceivers);

        if (!invokedAny)
            Log($"No {methodName}() found on held item {heldItemId} (searched components).");
    }

    private static bool InvokeItemVoidOnBehaviours(NetworkItem item, string methodName, bool callAll, bool includeInactive)
    {
        if (!item) return false;

        bool invokedAny = false;

        var behaviours = item.GetComponentsInChildren<MonoBehaviour>(includeInactive);

        for (int i = 0; i < behaviours.Length; i++)
        {
            var b = behaviours[i];
            if (!b) continue;

            MethodInfo m = b.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            if (m == null) continue;
            if (m.ReturnType != typeof(void)) continue;

            var ps = m.GetParameters();
            if (ps != null && ps.Length != 0) continue;

            try
            {
                m.Invoke(b, null);
                invokedAny = true;

                if (!callAll) break;
            }
            catch
            {
                // keep going
            }
        }

        return invokedAny;
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

                if (GUILayout.Button("SecondaryButton() (calls Secondary() on held item + sync)"))
                    hg.SecondaryButton();

                if (GUILayout.Button("TriggerButton() (calls Trigger() on held item + sync)"))
                    hg.TriggerButton();
            }
        }
    }
#endif
}
