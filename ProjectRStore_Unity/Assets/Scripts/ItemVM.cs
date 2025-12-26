// ItemVM.cs
// YOUR custom spawn style (no PhotonNetwork.Instantiate):
// - Master generates a UNIQUE ItemID (same for everyone)
// - Master RPCs ALL clients to Instantiate the REAL prefab locally
// - Everyone sets NetworkItem.ItemID = that ID (so your ItemNetworkManager can sync by ItemID)

using System;
using System.Collections.Generic;
using System.Reflection;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.Events;

#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(PhotonView))]
public class ItemVM : MonoBehaviourPun
{
    [Serializable]
    public class VMItem
    {
        [Header("Data")]
        public string ItemName;
        public int Price = 10;

        [Header("Prefabs")]
        [Tooltip("The REAL item prefab that gets spawned (locally on every client). Must have NetworkItem on ROOT.")]
        public GameObject RealItemPrefab;

        [Tooltip("Optional. If empty, auto-find/load <ItemName>__View for preview.")]
        public GameObject ViewPrefab;

        [Header("Optional")]
        public bool CanBeBought = true;
    }

    [Header("Networking")]
    [Tooltip("If true and in a Photon room, Master will broadcast spawns to everyone.")]
    public bool UsePhotonSpawnSync = true;

    [Tooltip("If true, spawn RPCs are buffered so late joiners also spawn previously purchased items.")]
    public bool BufferSpawnRPCsForLateJoiners = false;

    [Header("UI (drag Unity UI Buttons OR just call Next/Back/Buy from your own button scripts)")]
    public UnityEngine.Object NextButton;
    public UnityEngine.Object BackButton;
    public UnityEngine.Object BuyButton;

    [Tooltip("Drag TMP_Text / UI Text / TextMesh / or the GameObject that contains one.")]
    public UnityEngine.Object PriceText;

    [Tooltip("Optional: show item name too.")]
    public UnityEngine.Object ItemNameText;

    [Header("Preview + Drop")]
    public Transform ItemPreviewAnchor;

    [Tooltip("Where the REAL item spawns when bought. If null, uses ItemPreviewAnchor.")]
    public Transform DropSpawnPoint;

    [Tooltip("Small offset so spawned items don't clip into the machine.")]
    public Vector3 DropSpawnOffset = new Vector3(0f, 0f, 0.06f);

    [Header("Items")]
    public List<VMItem> Items = new List<VMItem>();

    [Header("Preview Behavior")]
    public bool StripPreviewPhysics = true;
    public bool SpinPreview = false;
    public float SpinSpeed = 45f;

    [Header("View Prefab Auto-Find")]
    public bool AutoFillViewPrefabsInEditor = true;
    public string EditorViewSearchFolder = "Assets/GameObject";
    public string RuntimeViewResourcesFolder = "GameObject";

    [Header("Money")]
    public PlayerStats LocalPlayerStats;
    public bool AutoFindPlayerStats = true;

    [Header("Debug")]
    public bool DebugLogs = false;

    private int _index = 0;
    private GameObject _previewInstance;

    // Master-only unique sequence (per machine)
    private int _masterSpawnSeq = 0;

    // Prevent duplicate spawns if an RPC gets replayed / buffered weirdly
    private static readonly HashSet<int> _spawnedItemIdsLocal = new HashSet<int>();

    // Cached UI button delegates (so RemoveListener works)
    private UnityAction _nextAction, _backAction, _buyAction;

    private void Awake()
    {
        _nextAction = Next;
        _backAction = Back;
        _buyAction = Buy;

        HookButtons();
    }

    private void Start()
    {
        if (DropSpawnPoint == null) DropSpawnPoint = ItemPreviewAnchor;

        if (AutoFindPlayerStats && LocalPlayerStats == null)
            LocalPlayerStats = FindObjectOfType<PlayerStats>();

        ClampIndex();
        RefreshAll();
    }

    private void Update()
    {
        if (SpinPreview && _previewInstance != null)
            _previewInstance.transform.Rotate(Vector3.up, SpinSpeed * Time.deltaTime, Space.Self);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!AutoFillViewPrefabsInEditor) return;
        if (Items == null) return;

        for (int i = 0; i < Items.Count; i++)
            TryAutoAssignViewPrefabEditor(Items[i]);
    }
#endif

    // -------------------------
    // Public button targets
    // -------------------------
    public void Next()
    {
        if (!HasItems()) return;
        _index = (_index + 1) % Items.Count;
        RefreshAll();
    }

    public void Back()
    {
        if (!HasItems()) return;
        _index = (_index - 1 + Items.Count) % Items.Count;
        RefreshAll();
    }

    public void Buy()
    {
        if (!HasItems()) return;

        var item = Items[_index];
        if (!item.CanBeBought) { Log($"Buy blocked: '{item.ItemName}' not buyable."); return; }
        if (item.RealItemPrefab == null) { Log($"Buy blocked: RealItemPrefab missing for '{item.ItemName}'."); return; }

        if (LocalPlayerStats == null)
        {
            Log("Buy blocked: LocalPlayerStats not found/assigned.");
            return;
        }

        int cost = Mathf.Max(0, item.Price);
        if (LocalPlayerStats.Money < cost)
        {
            Log($"Not enough money. Have {LocalPlayerStats.Money}, need {cost}.");
            return;
        }

        bool inPhoton = PhotonNetwork.InRoom && UsePhotonSpawnSync;

        // OFFLINE / LOCAL: spawn immediately (local-only unique ID)
        if (!inPhoton)
        {
            int localId = MakeLocalUniqueId();
            SpawnRealItemLocal(item, localId, GetDropPos(), GetDropRot());
            LocalPlayerStats.MoneyLose(cost);
            RefreshTextsOnly();
            return;
        }

        // PHOTON: master chooses the ID and tells everyone to spawn
        if (PhotonNetwork.IsMasterClient)
        {
            Master_ProcessBuy(PhotonNetwork.LocalPlayer.ActorNumber, _index);
        }
        else
        {
            photonView.RPC(nameof(RPC_RequestBuy), RpcTarget.MasterClient, PhotonNetwork.LocalPlayer.ActorNumber, _index);
        }
    }

    // -------------------------
    // Photon RPCs
    // -------------------------
    [PunRPC]
    private void RPC_RequestBuy(int buyerActorNumber, int itemIndex, PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;

        // Anti-spoof: only accept if sender matches
        if (info.Sender == null || info.Sender.ActorNumber != buyerActorNumber)
        {
            Log($"Rejected buy request: sender={info.Sender?.ActorNumber} claimed={buyerActorNumber}");
            return;
        }

        Master_ProcessBuy(buyerActorNumber, itemIndex);
    }

    private void Master_ProcessBuy(int buyerActorNumber, int itemIndex)
    {
        if (!HasItems()) return;
        if (itemIndex < 0 || itemIndex >= Items.Count) return;

        var item = Items[itemIndex];
        if (!item.CanBeBought || item.RealItemPrefab == null) return;

        int itemId = MakeMasterUniqueItemId(); // <- the important part
        Vector3 pos = GetDropPos();
        Quaternion rot = GetDropRot();

        // Spawn on ALL clients (optionally buffered for late joiners)
        var target = BufferSpawnRPCsForLateJoiners ? RpcTarget.AllBuffered : RpcTarget.All;
        photonView.RPC(nameof(RPC_SpawnItem_AllClients), target, itemIndex, itemId, pos, rot);

        // Tell ONLY the buyer to deduct money locally
        int cost = Mathf.Max(0, item.Price);
        var buyer = PhotonNetwork.CurrentRoom?.GetPlayer(buyerActorNumber);
        if (buyer != null)
            photonView.RPC(nameof(RPC_DeductMoneyLocal), buyer, cost);
    }

    [PunRPC]
    private void RPC_SpawnItem_AllClients(int itemIndex, int itemId, Vector3 pos, Quaternion rot)
    {
        if (!HasItems()) return;
        if (itemIndex < 0 || itemIndex >= Items.Count) return;

        // Prevent duplicates if buffered RPC replays while item already exists
        if (_spawnedItemIdsLocal.Contains(itemId))
        {
            Log($"Spawn ignored (duplicate id): {itemId}");
            return;
        }
        _spawnedItemIdsLocal.Add(itemId);

        var item = Items[itemIndex];
        if (item.RealItemPrefab == null)
        {
            Log($"Spawn failed: RealItemPrefab missing at index {itemIndex}");
            return;
        }

        SpawnRealItemLocal(item, itemId, pos, rot);
    }

    [PunRPC]
    private void RPC_DeductMoneyLocal(int amount)
    {
        if (LocalPlayerStats == null)
        {
            Log("RPC_DeductMoneyLocal: LocalPlayerStats missing.");
            return;
        }

        LocalPlayerStats.MoneyLose(Mathf.Max(0, amount));
        RefreshTextsOnly();
    }

    // -------------------------
    // Unique ID generation
    // -------------------------
    private int MakeMasterUniqueItemId()
    {
        // Unique across machines because photonView.ViewID is unique in room.
        // Unique across purchases because _masterSpawnSeq increments.
        _masterSpawnSeq++;

        long id = ((long)photonView.ViewID * 100000L) + _masterSpawnSeq; // safe range
        return unchecked((int)(id % int.MaxValue));
    }

    private static int _localSeq = 0;
    private int MakeLocalUniqueId()
    {
        _localSeq++;
        long id = ((long)GetInstanceID() * 100000L) + _localSeq;
        return unchecked((int)(id % int.MaxValue));
    }

    // -------------------------
    // Spawn helpers
    // -------------------------
    private Vector3 GetDropPos()
    {
        var t = DropSpawnPoint != null ? DropSpawnPoint : ItemPreviewAnchor;
        if (t == null) return transform.position + transform.forward * 0.25f;
        return t.TransformPoint(DropSpawnOffset);
    }

    private Quaternion GetDropRot()
    {
        var t = DropSpawnPoint != null ? DropSpawnPoint : ItemPreviewAnchor;
        if (t == null) return Quaternion.identity;
        return t.rotation;
    }

    private void SpawnRealItemLocal(VMItem item, int itemId, Vector3 pos, Quaternion rot)
    {
        var go = Instantiate(item.RealItemPrefab, pos, rot);

        // MUST be on ROOT per your note
        var netItem = go.GetComponent<NetworkItem>();
        if (netItem == null)
        {
            Log($"Spawned '{go.name}' but NO NetworkItem on root. (Fix: put NetworkItem on root!)");
        }
        else
        {
            ApplyNetworkItemId(netItem, itemId);
        }

        EnsureFalls(go);
        Log($"Spawned real item '{item.RealItemPrefab.name}' with ItemID={itemId}");
    }

    private void ApplyNetworkItemId(NetworkItem netItem, int itemId)
    {
        netItem.ItemID = itemId;

        // If your NetworkItem has these fields, we try to set them safely via reflection
        TrySetBool(netItem, "ComputeItemIdOnAwake", false);
        TrySetBool(netItem, "AutoRegister", true);
    }

    private void TrySetBool(object obj, string fieldName, bool value)
    {
        try
        {
            var t = obj.GetType();
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && f.FieldType == typeof(bool))
                f.SetValue(obj, value);
        }
        catch { /* ignore */ }
    }

    private void EnsureFalls(GameObject go)
    {
        var rb = go.GetComponent<Rigidbody>() ?? go.GetComponentInChildren<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.WakeUp();
        }
    }

    // -------------------------
    // Preview + UI
    // -------------------------
    private void RefreshAll()
    {
        RefreshPreview();
        RefreshTextsOnly();
    }

    private void RefreshTextsOnly()
    {
        if (!HasItems())
        {
            TrySetText(PriceText, "$0");
            TrySetText(ItemNameText, "");
            return;
        }

        var item = Items[_index];
        TrySetText(ItemNameText, item.ItemName ?? "");
        TrySetText(PriceText, "$" + Mathf.Max(0, item.Price));
    }

    private void RefreshPreview()
    {
        DestroyPreview();
        if (!HasItems()) return;

        if (ItemPreviewAnchor == null)
        {
            Log("ItemPreviewAnchor missing.");
            return;
        }

        var item = Items[_index];

        var viewPrefab = item.ViewPrefab;
        if (viewPrefab == null)
            viewPrefab = LoadViewPrefabRuntime(item.ItemName);

        if (viewPrefab == null)
        {
            Log($"No view prefab found for '{item.ItemName}'. Expected '{item.ItemName}__View'.");
            return;
        }

        _previewInstance = Instantiate(viewPrefab, ItemPreviewAnchor);
        _previewInstance.transform.localPosition = Vector3.zero;
        _previewInstance.transform.localRotation = Quaternion.identity;

        if (StripPreviewPhysics)
            StripPhysics(_previewInstance);
    }

    private void DestroyPreview()
    {
        if (_previewInstance != null)
        {
            Destroy(_previewInstance);
            _previewInstance = null;
        }
    }

    private GameObject LoadViewPrefabRuntime(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        string path = $"{RuntimeViewResourcesFolder}/{itemName}__View";
        return Resources.Load<GameObject>(path);
    }

    private void StripPhysics(GameObject root)
    {
        foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
            Destroy(rb);

        foreach (var col in root.GetComponentsInChildren<Collider>(true))
            col.enabled = false;
    }

    private void HookButtons()
    {
        HookButton(NextButton, _nextAction);
        HookButton(BackButton, _backAction);
        HookButton(BuyButton, _buyAction);
    }

    private void HookButton(UnityEngine.Object obj, UnityAction action)
    {
        if (obj == null || action == null) return;

        GameObject go = obj as GameObject;
        if (go == null && obj is Component c) go = c.gameObject;
        if (go == null) return;

        var uiButton = go.GetComponent<UnityEngine.UI.Button>();
        if (uiButton == null) return;

        uiButton.onClick.RemoveListener(action);
        uiButton.onClick.AddListener(action);
    }

    private void ClampIndex()
    {
        if (!HasItems()) { _index = 0; return; }
        _index = Mathf.Clamp(_index, 0, Items.Count - 1);
    }

    private bool HasItems() => Items != null && Items.Count > 0;

    // -------------------------
    // Text helper (TMP/TextMesh/UI Text)
    // -------------------------
    private bool TrySetText(UnityEngine.Object target, string s)
    {
        if (target == null) return false;

        if (target is GameObject go)
            return TrySetTextOnAnyComponent(go.GetComponents<Component>(), s);

        if (target is Component c)
        {
            if (TrySetTextOnComponent(c, s)) return true;
            return TrySetTextOnAnyComponent(c.GetComponents<Component>(), s);
        }

        return TrySetTextOnComponent(target, s);
    }

    private bool TrySetTextOnAnyComponent(Component[] comps, string s)
    {
        if (comps == null) return false;
        foreach (var c in comps)
        {
            if (c == null) continue;
            if (TrySetTextOnComponent(c, s)) return true;
        }
        return false;
    }

    private bool TrySetTextOnComponent(object obj, string s)
    {
        if (obj == null) return false;
        var t = obj.GetType();

        var prop = t.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
        {
            prop.SetValue(obj, s, null);
            return true;
        }

        var method = t.GetMethod("SetText", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
        if (method != null)
        {
            method.Invoke(obj, new object[] { s });
            return true;
        }

        return false;
    }

#if UNITY_EDITOR
    private void TryAutoAssignViewPrefabEditor(VMItem item)
    {
        if (item == null) return;
        if (string.IsNullOrWhiteSpace(item.ItemName)) return;
        if (item.ViewPrefab != null) return;

        string targetName = item.ItemName + "__View";
        string[] searchIn = new[] { string.IsNullOrWhiteSpace(EditorViewSearchFolder) ? "Assets" : EditorViewSearchFolder };
        string[] guids = AssetDatabase.FindAssets($"{targetName} t:prefab", searchIn);

        if (guids == null || guids.Length == 0) return;

        foreach (var guid in guids)
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p);
            if (prefab != null && prefab.name == targetName)
            {
                item.ViewPrefab = prefab;
                EditorUtility.SetDirty(this);
                break;
            }
        }

        if (item.ViewPrefab == null)
        {
            string p = AssetDatabase.GUIDToAssetPath(guids[0]);
            item.ViewPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(p);
            EditorUtility.SetDirty(this);
        }
    }
#endif

    private void Log(string msg)
    {
        if (DebugLogs) Debug.Log($"[ItemVM] {msg}", this);
    }
}
