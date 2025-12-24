using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

public class SellMachinePun : MonoBehaviourPunCallbacks
{
    [Serializable]
    public class ButtonBinding
    {
        public Collider Trigger;
        public ButtonAction Action = ButtonAction.Sell;
    }

    public enum ButtonAction { Sell }
    public enum ForwarderKind { SellZone, Button }

    [Header("Sell Zone (items inside here get sold)")]
    [Tooltip("Trigger collider that contains items to sell.")]
    public Collider SellZoneTrigger;

    [Header("Physical Buttons (trigger colliders)")]
    [Tooltip("Assign the trigger colliders used as physical buttons.")]
    public List<ButtonBinding> Buttons = new List<ButtonBinding>();

    [Tooltip("Only colliders on these layers can press buttons (set to your hand layer).")]
    public LayerMask InteractorLayers = ~0;

    [Tooltip("If true and in-room: if a PhotonView is FOUND on the presser, it must be IsMine. (If none found, it still passes unless RejectIfNoPhotonViewInRoom = true.)")]
    public bool RequireLocalPhotonView = true;

    [Tooltip("Only used if RequireLocalPhotonView is true. If enabled, presses with NO PhotonView in parents will be rejected.")]
    public bool RejectIfNoPhotonViewInRoom = false;

    [Tooltip("Cooldown so multiple hand colliders don't spam the button.")]
    public float ButtonCooldownSeconds = 0.35f;

    [Header("Physics helper")]
    [Tooltip("If true, auto-adds a kinematic Rigidbody to SellZoneTrigger + Button triggers if they don't already have one, so trigger events always fire.")]
    public bool AutoAddKinematicRigidbodyToTriggers = true;

    [Header("Doors")]
    public Transform[] Doors;

    [Tooltip("Door closed local euler angles (if empty, uses current door rotations at Awake).")]
    public Vector3[] DoorClosedLocalEuler;

    [Tooltip("Door open local euler angles.")]
    public Vector3[] DoorOpenLocalEuler;

    public float DoorLerpSeconds = 0.6f;

    [Header("Sell Timing")]
    [Tooltip("Extra wait AFTER doors finish closing before items are destroyed.")]
    public float DestroyDelayAfterDoorsClosed = 3f;

    [Header("Audio (optional)")]
    public AudioSource Audio;
    public AudioClip ButtonPressSfx;
    public AudioClip DoorCloseSfx;
    public AudioClip SellSfx;

    [Header("UI Text Outputs (drag GameObjects, we scan for Text components)")]
    [Tooltip("Drag GameObjects that CONTAIN text components (or parents). We'll scan them for TMP_Text / UI.Text / TextMesh etc.")]
    public List<GameObject> OutputTextRoots = new List<GameObject>();

    [Tooltip("If true, also scans children of OutputTextRoots for text components.")]
    public bool ScanChildrenForText = true;

    [Header("PlayerStats payout")]
    [Tooltip("Optional. If empty, auto-finds PlayerStats in scene on this client.")]
    public PlayerStats PlayerStatsRef;

    [Header("Debug")]
    public bool DebugLogs = true;

    // Items currently inside sell zone
    private readonly HashSet<NetworkItem> itemsInZone = new HashSet<NetworkItem>();

    // Cached text targets found by scanning OutputTextRoots
    private readonly List<Component> _cachedTextTargets = new List<Component>();

    private bool isSelling = false;
    private float nextButtonTime = 0f;

    // Reflection caches for text setting
    private static readonly Dictionary<Type, PropertyInfo> TextPropCache = new Dictionary<Type, PropertyInfo>();
    private static readonly Dictionary<Type, MethodInfo> SetTextMethodCache = new Dictionary<Type, MethodInfo>();

    private void Awake()
    {
        // Capture closed rotations if not provided / mismatched
        if (Doors != null && Doors.Length > 0)
        {
            if (DoorClosedLocalEuler == null || DoorClosedLocalEuler.Length != Doors.Length)
            {
                DoorClosedLocalEuler = new Vector3[Doors.Length];
                for (int i = 0; i < Doors.Length; i++)
                    DoorClosedLocalEuler[i] = Doors[i] ? Doors[i].localEulerAngles : Vector3.zero;
            }

            if (DoorOpenLocalEuler == null || DoorOpenLocalEuler.Length != Doors.Length)
            {
                DoorOpenLocalEuler = new Vector3[Doors.Length];
                for (int i = 0; i < Doors.Length; i++)
                    DoorOpenLocalEuler[i] = DoorClosedLocalEuler[i]; // default = no movement
            }
        }

        EnsureForwarders();
        RebuildTextCacheIfNeeded(force: true);
        EnsurePlayerStats();
    }

    private void Start()
    {
        // in case stuff was assigned after Awake
        EnsureForwarders();
        RebuildTextCacheIfNeeded(force: false);
        EnsurePlayerStats();
    }

    private void EnsurePlayerStats()
    {
        if (PlayerStatsRef != null) return;

        // Find local PlayerStats on this client
        PlayerStatsRef = FindObjectOfType<PlayerStats>();

        if (PlayerStatsRef == null)
            Log("PlayerStats not found (Money payout will do nothing). Drag it into PlayerStatsRef or ensure it's in-scene.");
    }

    private void EnsureForwarders()
    {
        if (SellZoneTrigger)
            AttachOrUpdateForwarder(SellZoneTrigger, ForwarderKind.SellZone, ButtonAction.Sell);

        if (Buttons != null)
        {
            for (int i = 0; i < Buttons.Count; i++)
            {
                var b = Buttons[i];
                if (b == null || !b.Trigger) continue;
                AttachOrUpdateForwarder(b.Trigger, ForwarderKind.Button, b.Action);
            }
        }
    }

    private void AttachOrUpdateForwarder(Collider col, ForwarderKind kind, ButtonAction action)
    {
        if (!col) return;

        if (!col.isTrigger)
            Log($"WARNING: Collider '{col.name}' is not marked as Trigger (OnTriggerEnter won't fire).");

        if (AutoAddKinematicRigidbodyToTriggers)
        {
            if (col.attachedRigidbody == null)
            {
                var rb = col.GetComponent<Rigidbody>();
                if (!rb)
                {
                    rb = col.gameObject.AddComponent<Rigidbody>();
                    rb.isKinematic = true;
                    rb.useGravity = false;
                    Log($"Added kinematic Rigidbody to '{col.name}' so trigger events fire.");
                }
            }
        }

        var fwd = col.GetComponent<SellMachinePunTriggerForwarder>();
        if (!fwd) fwd = col.gameObject.AddComponent<SellMachinePunTriggerForwarder>();

        fwd.Machine = this;
        fwd.Kind = kind;
        fwd.Action = action;
    }

    // Called by forwarders
    public void Forwarder_OnTriggerEnter(ForwarderKind kind, ButtonAction action, Collider other)
    {
        if (!other) return;

        if (kind == ForwarderKind.SellZone)
        {
            TryAddItemFromCollider(other);
            return;
        }

        if (kind == ForwarderKind.Button)
        {
            TryPressButton(action, other);
            return;
        }
    }

    // Called by forwarders
    public void Forwarder_OnTriggerExit(ForwarderKind kind, ButtonAction action, Collider other)
    {
        if (!other) return;

        if (kind == ForwarderKind.SellZone)
        {
            TryRemoveItemFromCollider(other);
            return;
        }
    }

    private void TryAddItemFromCollider(Collider other)
    {
        var ni = other.attachedRigidbody
            ? other.attachedRigidbody.GetComponentInParent<NetworkItem>()
            : other.GetComponentInParent<NetworkItem>();

        if (!ni) return;

        if (itemsInZone.Add(ni))
            Log($"Item entered sell zone: {ni.name} (count: {itemsInZone.Count})");
    }

    private void TryRemoveItemFromCollider(Collider other)
    {
        var ni = other.attachedRigidbody
            ? other.attachedRigidbody.GetComponentInParent<NetworkItem>()
            : other.GetComponentInParent<NetworkItem>();

        if (!ni) return;

        if (itemsInZone.Remove(ni))
            Log($"Item exited sell zone: {ni.name} (count: {itemsInZone.Count})");
    }

    private void TryPressButton(ButtonAction action, Collider presser)
    {
        if (Time.time < nextButtonTime) return;
        if (isSelling) { Log("Press ignored: already selling."); return; }

        if (!IsValidInteractor(presser, out string whyNot))
        {
            Log($"Press rejected by '{presser.name}': {whyNot}");
            return;
        }

        nextButtonTime = Time.time + ButtonCooldownSeconds;

        Log($"Button pressed by '{presser.name}' -> {action}");

        // button press sfx for everyone
        PlaySfxAllOrLocal(SfxId.ButtonPress);

        if (action == ButtonAction.Sell)
            RequestSell();
    }

    private bool IsValidInteractor(Collider c, out string whyNot)
    {
        whyNot = "";

        if (!c) { whyNot = "Null collider"; return false; }

        if (((1 << c.gameObject.layer) & InteractorLayers.value) == 0)
        {
            whyNot = $"Layer '{LayerMask.LayerToName(c.gameObject.layer)}' not in InteractorLayers";
            return false;
        }

        if (RequireLocalPhotonView && PhotonNetwork.InRoom)
        {
            var pv = c.GetComponentInParent<PhotonView>();

            if (pv == null)
            {
                if (RejectIfNoPhotonViewInRoom)
                {
                    whyNot = "No PhotonView found in parents (RejectIfNoPhotonViewInRoom = true)";
                    return false;
                }
                return true; // allow local rigs without PV
            }

            if (!pv.IsMine)
            {
                whyNot = $"PhotonView found but not mine (OwnerActor={pv.OwnerActorNr})";
                return false;
            }
        }

        return true;
    }

    // ---- SELL FLOW ----

    public void RequestSell()
    {
        if (isSelling) return;

        int actor = PhotonNetwork.InRoom ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

        if (PhotonNetwork.InRoom)
        {
            photonView.RPC(nameof(RPC_RequestSell), RpcTarget.MasterClient, actor);
        }
        else
        {
            StartCoroutine(SellRoutine(actor, offline: true));
        }
    }

    public void RequestTestSell() => RequestSell();

    [PunRPC]
    private void RPC_RequestSell(int requestingActor, PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (isSelling) return;

        photonView.RPC(nameof(RPC_BeginSell), RpcTarget.All, requestingActor);
    }

    [PunRPC]
    private void RPC_BeginSell(int requestingActor)
    {
        if (isSelling) return;
        StartCoroutine(SellRoutine(requestingActor, offline: false));
    }

    private IEnumerator SellRoutine(int requestingActor, bool offline)
    {
        isSelling = true;

        EnsurePlayerStats();
        RebuildTextCacheIfNeeded(force: false);

        Log($"SELL BEGIN (offline={offline}) trackedItems={itemsInZone.Count} requesterActor={requestingActor}");

        // Sell SFX should NOT wait until doors are closed
        PlaySfxAllOrLocal(SfxId.Sell);

        // Close doors + sfx
        PlaySfxAllOrLocal(SfxId.DoorClose);
        yield return LerpDoors(toOpen: false, DoorLerpSeconds);

        if (DestroyDelayAfterDoorsClosed > 0f)
            yield return new WaitForSeconds(DestroyDelayAfterDoorsClosed);

        int payout = 0;

        if (offline)
        {
            // Offline: compute + destroy locally
            payout = ComputePayout(itemsInZone);

            DestroyItemsOffline(itemsInZone);
            itemsInZone.Clear();

            if (payout > 0) AwardMoneyLocal(payout);

            string msg = (payout <= 0) ? "Sold nothing." : $"Sold items for {payout}";
            SetAllOutputText(msg);
        }
        else
        {
            // Multiplayer:
            // Only Master computes payout + destroys, then:
            // - sends payout ONLY to requester client
            // - sends message to everyone
            if (PhotonNetwork.IsMasterClient)
            {
                payout = ComputePayout(itemsInZone);

                DestroyItemsAsMaster(itemsInZone);
                itemsInZone.Clear();

                if (payout > 0)
                    AwardMoneyToActor(requestingActor, payout);

                string msg = (payout <= 0) ? "Sold nothing." : $"Sold items for {payout}";
                photonView.RPC(nameof(RPC_SetOutputText), RpcTarget.All, msg);
            }
            else
            {
                // Non-master: don't destroy, just clear local tracking (master nukes the objects anyway)
                itemsInZone.Clear();
            }
        }

        isSelling = false;
        Log("SELL END");
    }

    private int ComputePayout(HashSet<NetworkItem> set)
    {
        int payout = 0;
        foreach (var ni in set)
        {
            if (!ni) continue;
            int price = ni.GetSellPrice();
            if (price > 0) payout += price;
        }
        return payout;
    }

    private void DestroyItemsOffline(HashSet<NetworkItem> set)
    {
        var list = new List<NetworkItem>(set);
        foreach (var ni in list)
        {
            if (!ni) continue;
            Destroy(ni.gameObject);
        }
    }

    private void DestroyItemsAsMaster(HashSet<NetworkItem> set)
    {
        var list = new List<NetworkItem>(set);
        foreach (var ni in list)
        {
            if (!ni) continue;

            var pv = ni.GetComponentInParent<PhotonView>();
            if (pv != null)
                PhotonNetwork.Destroy(pv.gameObject);
            else
                Destroy(ni.gameObject);
        }
    }

    private void AwardMoneyLocal(int amount)
    {
        EnsurePlayerStats();
        if (PlayerStatsRef != null)
            PlayerStatsRef.MoneyGain(amount);
        else
            Log($"Tried to award money ({amount}) but PlayerStatsRef is null.");
    }

    private void AwardMoneyToActor(int actorNumber, int amount)
    {
        // Master sends ONLY to the player who pressed the button
        var room = PhotonNetwork.CurrentRoom;
        if (room == null)
        {
            Log("No room while awarding money (weird).");
            return;
        }

        Player target = room.GetPlayer(actorNumber);
        if (target != null)
        {
            photonView.RPC(nameof(RPC_GrantMoney), target, amount);
        }
        else
        {
            Log($"Could not find player with ActorNumber={actorNumber} to award money.");
        }
    }

    [PunRPC]
    private void RPC_GrantMoney(int amount)
    {
        // Runs ONLY on the receiving client
        AwardMoneyLocal(amount);
    }

    [PunRPC]
    private void RPC_SetOutputText(string msg)
    {
        RebuildTextCacheIfNeeded(force: false);
        SetAllOutputText(msg);
    }

    private IEnumerator LerpDoors(bool toOpen, float seconds)
    {
        if (Doors == null || Doors.Length == 0) yield break;

        seconds = Mathf.Max(0.01f, seconds);

        var start = new Quaternion[Doors.Length];
        var end = new Quaternion[Doors.Length];

        for (int i = 0; i < Doors.Length; i++)
        {
            if (!Doors[i]) continue;

            start[i] = Doors[i].localRotation;
            Vector3 e = toOpen ? DoorOpenLocalEuler[i] : DoorClosedLocalEuler[i];
            end[i] = Quaternion.Euler(e);
        }

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / seconds;
            float a = Mathf.Clamp01(t);

            for (int i = 0; i < Doors.Length; i++)
            {
                if (!Doors[i]) continue;
                Doors[i].localRotation = Quaternion.Slerp(start[i], end[i], a);
            }

            yield return null;
        }
    }

    private enum SfxId { ButtonPress = 0, DoorClose = 1, Sell = 2 }

    private void PlaySfxAllOrLocal(SfxId id)
    {
        if (PhotonNetwork.InRoom)
            photonView.RPC(nameof(RPC_PlayOneShot), RpcTarget.All, (int)id);
        else
            RPC_PlayOneShot((int)id);
    }

    [PunRPC]
    private void RPC_PlayOneShot(int id)
    {
        if (!Audio) return;

        switch ((SfxId)id)
        {
            case SfxId.ButtonPress:
                if (ButtonPressSfx) Audio.PlayOneShot(ButtonPressSfx);
                break;
            case SfxId.DoorClose:
                if (DoorCloseSfx) Audio.PlayOneShot(DoorCloseSfx);
                break;
            case SfxId.Sell:
                if (SellSfx) Audio.PlayOneShot(SellSfx);
                break;
        }
    }

    // --------------------
    // UI TEXT SCANNING
    // --------------------
    private void RebuildTextCacheIfNeeded(bool force)
    {
        if (!force && _cachedTextTargets.Count > 0) return;

        _cachedTextTargets.Clear();

        if (OutputTextRoots == null || OutputTextRoots.Count == 0)
            return;

        for (int i = 0; i < OutputTextRoots.Count; i++)
        {
            var root = OutputTextRoots[i];
            if (!root) continue;

            Component[] comps = ScanChildrenForText
                ? root.GetComponentsInChildren<Component>(true)
                : root.GetComponents<Component>();

            for (int c = 0; c < comps.Length; c++)
            {
                var comp = comps[c];
                if (!comp) continue;

                if (LooksLikeTextTarget(comp))
                    _cachedTextTargets.Add(comp);
            }
        }

        Log($"UI Scan: found {_cachedTextTargets.Count} text targets from {OutputTextRoots.Count} roots.");
    }

    private bool LooksLikeTextTarget(Component c)
    {
        if (c == null) return false;

        Type t = c.GetType();

        // property .text
        if (!TextPropCache.TryGetValue(t, out PropertyInfo prop))
        {
            prop = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            TextPropCache[t] = prop;
        }
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
            return true;

        // method SetText(string)
        if (!SetTextMethodCache.TryGetValue(t, out MethodInfo m))
        {
            m = t.GetMethod("SetText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            SetTextMethodCache[t] = m;
        }
        if (m != null) return true;

        return false;
    }

    private void SetAllOutputText(string s)
    {
        if (_cachedTextTargets.Count == 0)
            RebuildTextCacheIfNeeded(force: true);

        for (int i = 0; i < _cachedTextTargets.Count; i++)
        {
            var c = _cachedTextTargets[i];
            if (!c) continue;
            TrySetAnyText(c, s);
        }
    }

    private bool TrySetAnyText(Component c, string s)
    {
        if (c == null) return false;

        Type t = c.GetType();

        if (!TextPropCache.TryGetValue(t, out PropertyInfo prop))
        {
            prop = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            TextPropCache[t] = prop;
        }
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
        {
            prop.SetValue(c, s, null);
            return true;
        }

        if (!SetTextMethodCache.TryGetValue(t, out MethodInfo m))
        {
            m = t.GetMethod("SetText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            SetTextMethodCache[t] = m;
        }
        if (m != null)
        {
            m.Invoke(c, new object[] { s });
            return true;
        }

        return false;
    }

    private void Log(string msg)
    {
        if (DebugLogs) Debug.Log($"[SellMachinePun] {msg}", this);
    }
}

/// <summary>
/// Auto-added onto your assigned trigger colliders so SellMachinePun can receive trigger events.
/// Must be TOP-LEVEL + public for Unity to reliably AddComponent it.
/// </summary>
[DisallowMultipleComponent]
public class SellMachinePunTriggerForwarder : MonoBehaviour
{
    [HideInInspector] public SellMachinePun Machine;
    [HideInInspector] public SellMachinePun.ForwarderKind Kind;
    [HideInInspector] public SellMachinePun.ButtonAction Action;

    private void OnTriggerEnter(Collider other)
    {
        if (Machine) Machine.Forwarder_OnTriggerEnter(Kind, Action, other);
    }

    private void OnTriggerExit(Collider other)
    {
        if (Machine) Machine.Forwarder_OnTriggerExit(Kind, Action, other);
    }
}

#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(SellMachinePun))]
public class SellMachinePunEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        UnityEngine.GUILayout.Space(10);
        var machine = (SellMachinePun)target;

        using (new UnityEditor.EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (UnityEngine.GUILayout.Button("TEST: Sell Now (Requests Master)"))
                machine.RequestTestSell();
        }

        UnityEngine.GUILayout.Space(6);
        UnityEditor.EditorGUILayout.HelpBox(
            "Setup quickie:\n" +
            "• Assign SellZoneTrigger (trigger collider where items sit).\n" +
            "• Add your physical button trigger colliders to Buttons.\n" +
            "• Set InteractorLayers to your hand collider layer.\n" +
            "• Drag UI GameObjects into OutputTextRoots (we scan for text comps).\n" +
            "• Drag PlayerStats or leave empty (auto-find).\n" +
            "• If Audio is None, your SFX are basically whispering into the void.\n",
            UnityEditor.MessageType.Info);
    }
}
#endif
