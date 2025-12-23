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

    public enum ButtonAction
    {
        Sell
    }

    [Header("Sell Zone (items inside here get sold)")]
    [Tooltip("Trigger collider that contains items to sell.")]
    public Collider SellZoneTrigger;

    [Header("Physical Buttons (trigger colliders)")]
    [Tooltip("Assign the trigger colliders used as physical buttons.")]
    public List<ButtonBinding> Buttons = new List<ButtonBinding>();

    [Tooltip("Only colliders on these layers can press buttons (set to your hand layer).")]
    public LayerMask InteractorLayers = ~0;

    [Tooltip("If true, the presser must be under a PhotonView that IsMine (prevents remote hands triggering locally).")]
    public bool RequireLocalPhotonView = true;

    [Tooltip("Cooldown so multiple hand colliders don't spam the button.")]
    public float ButtonCooldownSeconds = 0.35f;

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

    [Header("UI Text Outputs (all say same thing)")]
    [Tooltip("Any component with a writable .text string OR SetText(string) method (TMP_Text, TextMeshProUGUI, UnityEngine.UI.Text, etc.)")]
    public List<Component> OutputTexts = new List<Component>();

    [Header("Debug")]
    public bool DebugLogs = true;

    // Items currently inside sell zone
    private readonly HashSet<NetworkItem> itemsInZone = new HashSet<NetworkItem>();

    private bool isSelling = false;
    private float nextButtonTime = 0f;

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

        // Auto-attach forwarders so this ONE script can listen to child trigger colliders
        EnsureForwarders();
    }

    private void OnValidate()
    {
        // Keep forwarders updated while editing (safe if it doesn't run in edit mode)
        // This won't add components in edit mode unless you hit play.
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

        var fwd = col.GetComponent<SellMachinePunTriggerForwarder>();
        if (!fwd) fwd = col.gameObject.AddComponent<SellMachinePunTriggerForwarder>();

        fwd.Machine = this;
        fwd.Kind = kind;
        fwd.Action = action;

        if (!col.isTrigger)
            Log($"WARNING: Collider '{col.name}' is not marked as Trigger.");
    }

    // Called by forwarders
    internal void Forwarder_OnTriggerEnter(ForwarderKind kind, ButtonAction action, Collider other)
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
    internal void Forwarder_OnTriggerExit(ForwarderKind kind, ButtonAction action, Collider other)
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
        // Grab the NetworkItem from the collider's hierarchy
        var ni = other.attachedRigidbody
            ? other.attachedRigidbody.GetComponentInParent<NetworkItem>()
            : other.GetComponentInParent<NetworkItem>();

        if (!ni) return;

        itemsInZone.Add(ni);
    }

    private void TryRemoveItemFromCollider(Collider other)
    {
        var ni = other.attachedRigidbody
            ? other.attachedRigidbody.GetComponentInParent<NetworkItem>()
            : other.GetComponentInParent<NetworkItem>();

        if (!ni) return;

        itemsInZone.Remove(ni);
    }

    private void TryPressButton(ButtonAction action, Collider presser)
    {
        if (Time.time < nextButtonTime) return;
        if (!IsValidInteractor(presser)) return;
        if (isSelling) return;

        nextButtonTime = Time.time + ButtonCooldownSeconds;

        // Button press SFX for everyone (optional)
        if (ButtonPressSfx && PhotonNetwork.InRoom)
            photonView.RPC(nameof(RPC_PlayOneShot), RpcTarget.All, (int)SfxId.ButtonPress);
        else
            PlayOneShotLocal(ButtonPressSfx);

        switch (action)
        {
            case ButtonAction.Sell:
                RequestSell();
                break;
        }
    }

    private bool IsValidInteractor(Collider c)
    {
        if (!c) return false;

        // Layer gate
        if (((1 << c.gameObject.layer) & InteractorLayers.value) == 0)
            return false;

        // Ownership gate (optional)
        if (RequireLocalPhotonView && PhotonNetwork.InRoom)
        {
            var pv = c.GetComponentInParent<PhotonView>();
            if (pv == null) return false;
            return pv.IsMine;
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
            // Ask MasterClient to sell
            photonView.RPC(nameof(RPC_RequestSell), RpcTarget.MasterClient, actor);
        }
        else
        {
            // Offline
            StartCoroutine(SellRoutine(actor, offline: true));
        }
    }

    public void RequestTestSell() => RequestSell();

    [PunRPC]
    private void RPC_RequestSell(int requestingActor, PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (isSelling) return;

        // Start sell for everyone
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

        // Sell SFX should NOT wait until doors are closed
        PlayOneShotLocal(SellSfx);

        // Close doors + sfx
        PlayOneShotLocal(DoorCloseSfx);
        yield return LerpDoors(toOpen: false, DoorLerpSeconds);

        // Wait extra delay AFTER doors are fully closed
        if (DestroyDelayAfterDoorsClosed > 0f)
            yield return new WaitForSeconds(DestroyDelayAfterDoorsClosed);

        int payout = 0;

        // Snapshot list (since we'll clear + destroy)
        var sellList = new List<NetworkItem>(itemsInZone);

        foreach (var ni in sellList)
        {
            if (!ni) continue;

            int price = ni.GetSellPrice();
            if (price > 0) payout += price;

            // Destroy networked objects on Master when possible
            if (!offline && PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
            {
                var pv = ni.GetComponentInParent<PhotonView>();
                if (pv != null)
                    PhotonNetwork.Destroy(pv.gameObject);
                else
                    Destroy(ni.gameObject);
            }
            else
            {
                Destroy(ni.gameObject);
            }
        }

        itemsInZone.Clear();

        string msg = (payout <= 0) ? "Sold nothing." : $"Sold items for {payout}";
        SetAllOutputText(msg);

        isSelling = false;
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

    private enum SfxId { ButtonPress = 0 }

    [PunRPC]
    private void RPC_PlayOneShot(int id)
    {
        if (!Audio) return;

        switch ((SfxId)id)
        {
            case SfxId.ButtonPress:
                if (ButtonPressSfx) Audio.PlayOneShot(ButtonPressSfx);
                break;
        }
    }

    private void PlayOneShotLocal(AudioClip clip)
    {
        if (!Audio || !clip) return;
        Audio.PlayOneShot(clip);
    }

    private void SetAllOutputText(string s)
    {
        if (OutputTexts == null) return;

        for (int i = 0; i < OutputTexts.Count; i++)
        {
            var c = OutputTexts[i];
            if (!c) continue;
            TrySetAnyText(c, s);
        }
    }

    private bool TrySetAnyText(Component c, string s)
    {
        if (c == null) return false;

        var t = c.GetType();

        var prop = t.GetProperty("text");
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
        {
            prop.SetValue(c, s, null);
            return true;
        }

        var method = t.GetMethod("SetText", new[] { typeof(string) });
        if (method != null)
        {
            method.Invoke(c, new object[] { s });
            return true;
        }

        return false;
    }

    private void Log(string msg)
    {
        if (DebugLogs) Debug.Log($"[SellMachinePun] {msg}", this);
    }

    internal enum ForwarderKind { SellZone, Button }

    /// <summary>
    /// Auto-added at runtime onto your assigned trigger colliders so this stays a "one .cs file" setup.
    /// </summary>
    [DisallowMultipleComponent]
    internal class SellMachinePunTriggerForwarder : MonoBehaviour
    {
        public SellMachinePun Machine;
        public ForwarderKind Kind;
        public ButtonAction Action;

        private void OnTriggerEnter(Collider other)
        {
            if (Machine) Machine.Forwarder_OnTriggerEnter(Kind, Action, other);
        }

        private void OnTriggerExit(Collider other)
        {
            if (Machine) Machine.Forwarder_OnTriggerExit(Kind, Action, other);
        }
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
            "• If hands aren't under a PhotonView, disable RequireLocalPhotonView.\n" +
            "• Optional: garlic salt is NOT a valid layer mask.",
            UnityEditor.MessageType.Info);
    }
}
#endif
