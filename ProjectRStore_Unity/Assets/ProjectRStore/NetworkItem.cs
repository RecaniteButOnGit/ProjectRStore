using System.Collections;
using System.Text;
using UnityEngine;

public class NetworkItem : MonoBehaviour
{
    [Header("Identity")]
    public int ItemID;

    [Tooltip("If true, overwrites ItemID on Awake/Start using a deterministic hash (scene + hierarchy path + sibling indices + position).")]
    public bool ComputeItemIdOnAwake = true;

    [Tooltip("Automatically registers this item with ItemNetworkManager when ready.")]
    public bool AutoRegister = true;

    [Header("Economy")]
    public int BuyPrice = 0;
    public int SellPriceOverride = 0;

    [Header("Grab Behavior")]
    public bool SnapGrab = false;
    public Vector3 SnapLocalPosition;
    public Vector3 SnapLocalEulerAngles;

    [Header("Backpack Behavior")]
    public bool CanBeStoredInBackpack = true;

    [Header("Debug")]
    public bool DebugLogs = false;

    private Rigidbody _rb;
    private bool _registered;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>() ?? GetComponentInChildren<Rigidbody>(true);

        if (ComputeItemIdOnAwake)
        {
            // Only compute if unset OR if you explicitly want recompute behavior.
            if (ItemID == 0)
                ItemID = ComputeDeterministicItemId(transform);
        }

        if (AutoRegister)
            TryRegisterOrRetry();
    }

    private void Start()
    {
        // In case something changes during initialization order
        if (ComputeItemIdOnAwake && ItemID == 0)
            ItemID = ComputeDeterministicItemId(transform);

        if (AutoRegister)
            TryRegisterOrRetry();
    }

    private void TryRegisterOrRetry()
    {
        if (_registered) return;

        if (ItemID == 0)
        {
            LogWarn("ItemID is 0; won't register yet.");
            return;
        }

        if (_rb == null)
        {
            LogWarn("No Rigidbody found on item or children; won't register.");
            return;
        }

        // Manager might spawn after this item; retry a bit.
        if (ItemNetworkManager.Instance == null)
        {
            StartCoroutine(CoWaitForManagerThenRegister());
            return;
        }

        ItemNetworkManager.Instance.RegisterItem(this, _rb);
        _registered = true;
        Log($"Registered (ItemID={ItemID})");
    }

    private IEnumerator CoWaitForManagerThenRegister()
    {
        const float timeout = 2.0f;
        float t = 0f;

        while (!_registered && t < timeout)
        {
            if (ItemNetworkManager.Instance != null && ItemID != 0 && _rb != null)
            {
                ItemNetworkManager.Instance.RegisterItem(this, _rb);
                _registered = true;
                Log($"Registered after wait (ItemID={ItemID})");
                yield break;
            }

            t += Time.deltaTime;
            yield return null;
        }

        if (!_registered)
            LogWarn("Timed out waiting for ItemNetworkManager.Instance; item not registered.");
    }

    public int GetSellPrice()
    {
        if (SellPriceOverride > 0)
            return SellPriceOverride;

        if (BuyPrice <= 0)
            return 0;

        // /20 rule (max players = 20)
        return Mathf.Max(1, BuyPrice / 20);
    }

    public void ApplySnapIfNeeded(Transform t)
    {
        if (!SnapGrab)
            return;

        t.localPosition = SnapLocalPosition;
        t.localRotation = Quaternion.Euler(SnapLocalEulerAngles);
    }

    // =========================================================
    // Deterministic ID (no PhotonView needed)
    // =========================================================

    private static int ComputeDeterministicItemId(Transform t)
    {
        // Build a stable key: scene + full hierarchy path with sibling indices + position.
        // This should match across clients as long as the scene hierarchy is the same.
        var sb = new StringBuilder(256);

        var sceneName = t.gameObject.scene.IsValid() ? t.gameObject.scene.name : "NoScene";
        sb.Append(sceneName).Append('|');

        // Hierarchy path with sibling indices (more stable than names alone)
        // Example segment: 0003:Backpack/0000:ItemRoot/0001:Coin
        var stack = new System.Collections.Generic.List<Transform>(16);
        Transform cur = t;
        while (cur != null)
        {
            stack.Add(cur);
            cur = cur.parent;
        }
        stack.Reverse();

        for (int i = 0; i < stack.Count; i++)
        {
            var tr = stack[i];
            sb.Append(tr.GetSiblingIndex().ToString("D4"))
              .Append(':')
              .Append(tr.name);

            if (i != stack.Count - 1) sb.Append('/');
        }

        // Add position (rounded) to reduce chance of collisions if hierarchy matches but there are duplicates
        Vector3 p = t.position;
        sb.Append("|pos=")
          .Append(Round3(p.x)).Append(',')
          .Append(Round3(p.y)).Append(',')
          .Append(Round3(p.z));

        // Hash to int (FNV-1a 32-bit)
        uint hash = 2166136261u;
        string key = sb.ToString();
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= 16777619u;
        }

        // Make it a positive non-zero int
        int id = (int)(hash & 0x7FFFFFFF);
        if (id == 0) id = 1;
        return id;
    }

    private static string Round3(float v)
    {
        // 3 decimals is usually plenty for scene-placed object uniqueness
        return (Mathf.Round(v * 1000f) / 1000f).ToString("0.000");
    }

    private void Log(string msg)
    {
        if (DebugLogs) Debug.Log($"[NetworkItem] {msg}", this);
    }

    private void LogWarn(string msg)
    {
        if (DebugLogs) Debug.LogWarning($"[NetworkItem] {msg}", this);
    }
}
