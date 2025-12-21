using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

[RequireComponent(typeof(PhotonView))]
[DisallowMultipleComponent]
public class SellMachinePun : MonoBehaviourPunCallbacks
{
    private enum MachineState : int { OpenIdle = 0, BusyClosed = 1 }

    [Header("Trigger Zone (same GameObject as trigger collider)")]
    public Collider IntakeTrigger;

    [Header("Doors (3)")]
    public Transform[] Doors = new Transform[3];
    public float DoorClosedX = 0f;
    public float DoorOpenX = 90f;

    [Tooltip("Seconds it takes to rotate doors.")]
    public float DoorLerpDuration = 0.35f;

    [Header("UI (Worldspace Text Targets)")]
    [Tooltip("Drag your 4 TMP components OR their GameObjects here. ALL show the SAME payout text.")]
    public UnityEngine.Object[] ResultTextTargets = new UnityEngine.Object[4];

    [Header("Buttons")]
    public int ButtonCount = 11;

    [Header("Timing")]
    [Tooltip("After doors start closing, wait this long, then DESTROY the items.")]
    public float DestroyDelayAfterCloseSeconds = 3f;

    [Tooltip("After starting the SELL SFX, wait this long before opening doors + showing payout.")]
    public float RevealDelaySeconds = 5f;

    [Header("Audio")]
    public AudioClip DoorCloseSfx;
    public AudioClip DoorOpenSfx;
    public AudioClip SellSfx;

    [Tooltip("Optional. If provided, plays door sounds through these sources (index-matched to Doors). If empty, uses PlayClipAtPoint.")]
    public AudioSource[] DoorAudioSources;

    [Tooltip("Optional. If set, sell SFX plays at this position. If null, uses this transform.")]
    public Transform SellSfxPoint;

    [Header("Economy (Optional)")]
    public bool AddMoneyToPlayerProperties = false;
    public string MoneyPropKey = "Money";

    [Header("Debug")]
    public bool DebugLogs = true;

    // Items inside machine (deterministic ItemID)
    private readonly HashSet<int> _itemsInside = new();

    // ID -> object lookup
    private readonly Dictionary<int, NetworkItem> _itemById = new();

    private int _correctButton = 0;
    private MachineState _state = MachineState.OpenIdle;
    private Coroutine _doorRoutine;

    private void Reset()
    {
        IntakeTrigger = GetComponent<Collider>();
        if (IntakeTrigger != null) IntakeTrigger.isTrigger = true;
    }

    private void Awake()
    {
        if (IntakeTrigger == null) IntakeTrigger = GetComponent<Collider>();
    }

    private void Start()
    {
        RebuildItemCache();
        SetAllTexts("");

        if (PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
        {
            _correctButton = Random.Range(0, Mathf.Max(1, ButtonCount));
            _state = MachineState.OpenIdle;
            PushSnapshotBuffered(lastPayout: 0);
        }
    }

    public override void OnJoinedRoom()
    {
        if (PhotonNetwork.IsMasterClient)
        {
            _correctButton = Random.Range(0, Mathf.Max(1, ButtonCount));
            _state = MachineState.OpenIdle;
            PushSnapshotBuffered(lastPayout: 0);
        }
    }

    public override void OnMasterClientSwitched(Player newMasterClient)
    {
        if (PhotonNetwork.IsMasterClient)
        {
            if (_state != MachineState.OpenIdle)
                _state = MachineState.BusyClosed;

            _correctButton = Random.Range(0, Mathf.Max(1, ButtonCount));
            PushSnapshotBuffered(lastPayout: 0);
        }
    }

    // =========================================================
    // BUTTON HOOKS (same script)
    // =========================================================
    public void PressButton(int buttonIndex)
    {
        if (!PhotonNetwork.InRoom) return;
        if (buttonIndex < 0 || buttonIndex >= ButtonCount) return;

        int actor = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : 0;
        photonView.RPC(nameof(RPC_PressButton_Master), RpcTarget.MasterClient, buttonIndex, actor);
    }

    public void Press_0()  => PressButton(0);
    public void Press_1()  => PressButton(1);
    public void Press_2()  => PressButton(2);
    public void Press_3()  => PressButton(3);
    public void Press_4()  => PressButton(4);
    public void Press_5()  => PressButton(5);
    public void Press_6()  => PressButton(6);
    public void Press_7()  => PressButton(7);
    public void Press_8()  => PressButton(8);
    public void Press_9()  => PressButton(9);
    public void Press_10() => PressButton(10);

    public void RequestTestSell()
    {
        if (!Application.isPlaying) return;

        var pv = photonView;
        if (pv == null)
        {
            Debug.LogError("[SellMachinePun] Missing PhotonView on this GameObject.", this);
            return;
        }

        if (!PhotonNetwork.InRoom)
        {
            Debug.LogWarning("[SellMachinePun] Not in a Photon room yet.", this);
            return;
        }

        if (pv.ViewID == 0)
        {
            Debug.LogError("[SellMachinePun] PhotonView.ViewID is 0. If scene object, allocate Scene View ID. If spawned, PhotonNetwork.Instantiate.", this);
            return;
        }

        int actor = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : 0;
        pv.RPC(nameof(RPC_TestSell_Master), RpcTarget.MasterClient, actor);
    }

    // =========================================================
    // Trigger tracking
    // =========================================================
    private void OnTriggerEnter(Collider other)
    {
        if (_state != MachineState.OpenIdle) return;

        var item = other.GetComponentInParent<NetworkItem>();
        if (item == null || item.ItemID == 0) return;

        _itemsInside.Add(item.ItemID);
        CacheItemIfNeeded(item);
    }

    private void OnTriggerExit(Collider other)
    {
        if (_state != MachineState.OpenIdle) return;

        var item = other.GetComponentInParent<NetworkItem>();
        if (item == null || item.ItemID == 0) return;

        _itemsInside.Remove(item.ItemID);
    }

    // =========================================================
    // Master logic
    // =========================================================
    [PunRPC]
    private void RPC_PressButton_Master(int buttonIndex, int presserActor, PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (_state != MachineState.OpenIdle) return;
        if (_itemsInside.Count <= 0) return;

        if (buttonIndex != _correctButton) return;

        StartCoroutine(Co_MasterSellSequence(allowEmpty: false, sellerActor: presserActor));
    }

    [PunRPC]
    private void RPC_TestSell_Master(int requesterActor, PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (_state != MachineState.OpenIdle) return;

        StartCoroutine(Co_MasterSellSequence(allowEmpty: true, sellerActor: requesterActor));
    }

    private IEnumerator Co_MasterSellSequence(bool allowEmpty, int sellerActor)
    {
        if (!allowEmpty && _itemsInside.Count <= 0)
            yield break;

        _state = MachineState.BusyClosed;

        int[] itemIds = _itemsInside.ToArray();
        _itemsInside.Clear();

        RebuildItemCacheIfNeeded();

        int total = 0;
        foreach (int id in itemIds)
        {
            var ni = ResolveItem(id);
            if (ni != null) total += Mathf.Max(0, ni.GetSellPrice());
        }

        // Start SELL SFX IMMEDIATELY (does not wait for doors to finish closing)
        photonView.RPC(nameof(RPC_PlaySellSfx), RpcTarget.All);

        // Close doors right away too
        photonView.RPC(nameof(RPC_CloseDoors_PlaySfx), RpcTarget.All);

        // Snapshot (late joiners see correct door state/text)
        PushSnapshotBuffered(lastPayout: 0);

        // After 3 seconds from start of closing, destroy items
        yield return new WaitForSeconds(DestroyDelayAfterCloseSeconds);
        photonView.RPC(nameof(RPC_DestroyItems), RpcTarget.All, itemIds);

        if (AddMoneyToPlayerProperties)
            TryAddMoneyToSeller(sellerActor, total);

        // Wait the REMAINDER so doors open exactly RevealDelaySeconds after sell sfx started
        float remaining = Mathf.Max(0f, RevealDelaySeconds - DestroyDelayAfterCloseSeconds);
        yield return new WaitForSeconds(remaining);

        photonView.RPC(nameof(RPC_OpenDoors_ShowPayout), RpcTarget.All, total);

        _correctButton = Random.Range(0, Mathf.Max(1, ButtonCount));
        _state = MachineState.OpenIdle;

        PushSnapshotBuffered(lastPayout: total);
    }

    private void TryAddMoneyToSeller(int sellerActor, int amount)
    {
        if (amount <= 0) return;

        Player seller = PhotonNetwork.CurrentRoom?.Players?.Values?.FirstOrDefault(p => p.ActorNumber == sellerActor);
        if (seller == null) return;

        int current = 0;
        if (seller.CustomProperties != null && seller.CustomProperties.ContainsKey(MoneyPropKey))
        {
            object v = seller.CustomProperties[MoneyPropKey];
            if (v is int i) current = i;
        }

        var props = new ExitGames.Client.Photon.Hashtable { { MoneyPropKey, current + amount } };
        seller.SetCustomProperties(props);
    }

    // =========================================================
    // Everyone (RPCs)
    // =========================================================
    [PunRPC]
    private void RPC_PlaySellSfx()
    {
        if (SellSfx == null) return;

        Vector3 p = (SellSfxPoint != null) ? SellSfxPoint.position : transform.position;
        AudioSource.PlayClipAtPoint(SellSfx, p);

        SetAllTexts("");
    }

    [PunRPC]
    private void RPC_CloseDoors_PlaySfx()
    {
        StartDoorLerp(toOpen: false);

        if (DoorCloseSfx != null)
            for (int i = 0; i < Doors.Length; i++)
                PlayDoorClip(i, DoorCloseSfx);

        SetAllTexts("");
    }

    [PunRPC]
    private void RPC_DestroyItems(int[] itemIds)
    {
        RebuildItemCacheIfNeeded();

        foreach (int id in itemIds)
        {
            var ni = ResolveItem(id);
            if (ni != null)
            {
                _itemById.Remove(id);
                Destroy(ni.gameObject);
            }
        }

        SetAllTexts("");
    }

    [PunRPC]
    private void RPC_OpenDoors_ShowPayout(int total)
    {
        StartDoorLerp(toOpen: true);

        if (DoorOpenSfx != null)
            for (int i = 0; i < Doors.Length; i++)
                PlayDoorClip(i, DoorOpenSfx);

        SetAllTexts($"+${total}");
    }

    // =========================================================
    // Buffered snapshot for late joiners
    // =========================================================
    private void PushSnapshotBuffered(int lastPayout)
    {
        if (!PhotonNetwork.IsMasterClient) return;

        PhotonNetwork.RemoveRPCs(photonView);
        photonView.RPC(nameof(RPC_ApplySnapshot), RpcTarget.AllBuffered, (int)_state, _correctButton, lastPayout);
    }

    [PunRPC]
    private void RPC_ApplySnapshot(int stateInt, int correctButton, int lastPayout)
    {
        _state = (MachineState)stateInt;
        _correctButton = correctButton;

        bool open = (_state == MachineState.OpenIdle);
        ApplyDoorPoseImmediate(open);

        SetAllTexts(open && lastPayout > 0 ? $"+${lastPayout}" : "");
    }

    // =========================================================
    // Doors (LERP ROTATION)
    // =========================================================
    private void StartDoorLerp(bool toOpen)
    {
        if (_doorRoutine != null) StopCoroutine(_doorRoutine);
        _doorRoutine = StartCoroutine(CoDoorLerp(toOpen));
    }

    private IEnumerator CoDoorLerp(bool toOpen)
    {
        float targetX = toOpen ? DoorOpenX : DoorClosedX;
        float dur = Mathf.Max(0.01f, DoorLerpDuration);

        Quaternion[] startRot = new Quaternion[Doors.Length];
        Quaternion[] endRot = new Quaternion[Doors.Length];

        for (int i = 0; i < Doors.Length; i++)
        {
            var d = Doors[i];
            if (d == null) continue;

            Vector3 e = d.localEulerAngles;
            startRot[i] = d.localRotation;
            endRot[i] = Quaternion.Euler(targetX, e.y, e.z);
        }

        float t = 0f;
        while (t < dur)
        {
            float a = t / dur;

            for (int i = 0; i < Doors.Length; i++)
            {
                var d = Doors[i];
                if (d == null) continue;
                d.localRotation = Quaternion.Slerp(startRot[i], endRot[i], a);
            }

            t += Time.deltaTime;
            yield return null;
        }

        for (int i = 0; i < Doors.Length; i++)
        {
            var d = Doors[i];
            if (d == null) continue;
            d.localRotation = endRot[i];
        }
    }

    private void ApplyDoorPoseImmediate(bool open)
    {
        float targetX = open ? DoorOpenX : DoorClosedX;

        for (int i = 0; i < Doors.Length; i++)
        {
            var d = Doors[i];
            if (d == null) continue;
            Vector3 e = d.localEulerAngles;
            d.localRotation = Quaternion.Euler(targetX, e.y, e.z);
        }
    }

    private void PlayDoorClip(int doorIndex, AudioClip clip)
    {
        if (clip == null) return;

        if (DoorAudioSources != null &&
            doorIndex >= 0 && doorIndex < DoorAudioSources.Length &&
            DoorAudioSources[doorIndex] != null)
        {
            DoorAudioSources[doorIndex].PlayOneShot(clip);
            return;
        }

        if (Doors != null && doorIndex >= 0 && doorIndex < Doors.Length && Doors[doorIndex] != null)
        {
            AudioSource.PlayClipAtPoint(clip, Doors[doorIndex].position);
            return;
        }

        AudioSource.PlayClipAtPoint(clip, transform.position);
    }

    // =========================================================
    // Item cache / resolve
    // =========================================================
    private void RebuildItemCache()
    {
        _itemById.Clear();

        var items = FindObjectsOfType<NetworkItem>(true);
        foreach (var ni in items)
        {
            if (ni == null || ni.ItemID == 0) continue;
            if (!_itemById.ContainsKey(ni.ItemID))
                _itemById.Add(ni.ItemID, ni);
        }
    }

    private void RebuildItemCacheIfNeeded()
    {
        if (_itemById.Count == 0)
            RebuildItemCache();
    }

    private void CacheItemIfNeeded(NetworkItem ni)
    {
        if (ni == null || ni.ItemID == 0) return;
        if (!_itemById.ContainsKey(ni.ItemID))
            _itemById[ni.ItemID] = ni;
    }

    private NetworkItem ResolveItem(int itemId)
    {
        if (_itemById.TryGetValue(itemId, out var ni) && ni != null) return ni;

        var all = FindObjectsOfType<NetworkItem>(true);
        foreach (var x in all)
        {
            if (x != null && x.ItemID == itemId)
            {
                _itemById[itemId] = x;
                return x;
            }
        }
        return null;
    }

    // =========================================================
    // Text output (reflection; works with TMP, UI Text, etc.)
    // =========================================================
    private void SetAllTexts(string s)
    {
        s ??= "";
        if (ResultTextTargets == null) return;

        for (int i = 0; i < ResultTextTargets.Length; i++)
        {
            var obj = ResultTextTargets[i];
            if (obj == null) continue;

            if (obj is GameObject go)
            {
                foreach (var c in go.GetComponents<Component>())
                    if (TrySetTextOnComponent(c, s)) break;
                continue;
            }

            if (obj is Component comp)
                TrySetTextOnComponent(comp, s);
        }
    }

    private bool TrySetTextOnComponent(Component c, string s)
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
    }
}
#endif
