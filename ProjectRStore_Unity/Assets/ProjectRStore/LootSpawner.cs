using UnityEngine;
using Photon.Pun;

[RequireComponent(typeof(PhotonView))]
public class LootSpawner : MonoBehaviourPun
{
    [Header("Spawn List (prefabs must have NetworkItem)")]
    public GameObject[] SpawnPrefabs;

    [Header("Ranges")]
    public float PlayerTriggerRadius = 30f;
    public float BlockIfItemWithin = 1.5f;

    [Header("Spawn Point")]
    public Transform SpawnPoint; // if null, uses this transform

    [Header("Player Detection")]
    public string PlayerTag = "PlayerTag";
    public float CheckInterval = 0.5f;

    [Header("Behavior")]
    public bool SpawnOnce = true;

    [Header("Debug Gizmos")]
    public bool DrawGizmos = true;

    private float _nextCheck;
    private bool _hasSpawned;
    private int _spawnedItemId;

    private void Reset()
    {
        SpawnPoint = transform;
    }

    private void Update()
    {
        if (Time.time < _nextCheck) return;
        _nextCheck = Time.time + Mathf.Max(0.05f, CheckInterval);

        // Only the Master decides + sends RPC, so you don’t get 12 spawns from 12 clients.
        if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient) return;

        if (SpawnOnce && _hasSpawned) return;

        if (SpawnPrefabs == null || SpawnPrefabs.Length == 0) return;

        Vector3 pos = (SpawnPoint != null) ? SpawnPoint.position : transform.position;
        Quaternion rot = (SpawnPoint != null) ? SpawnPoint.rotation : transform.rotation;

        if (!AnyPlayerWithinRadius(pos, PlayerTriggerRadius)) return;
        if (IsAnyItemBlocking(pos, BlockIfItemWithin)) return;

        // Decide what to spawn (master only) and broadcast it.
        int prefabIndex = Random.Range(0, SpawnPrefabs.Length);

        int itemId = ComputeSpawnItemId(prefabIndex);
        _spawnedItemId = itemId;

        if (PhotonNetwork.InRoom)
        {
            photonView.RPC(nameof(RPC_SpawnLoot), RpcTarget.AllBuffered, prefabIndex, itemId, pos, rot);
        }
        else
        {
            // Offline testing: just spawn locally.
            RPC_SpawnLoot(prefabIndex, itemId, pos, rot);
        }
    }

    private bool AnyPlayerWithinRadius(Vector3 center, float radius)
    {
        float r2 = radius * radius;

        GameObject[] players;
        try
        {
            players = GameObject.FindGameObjectsWithTag(PlayerTag);
        }
        catch
        {
            // Tag not defined = Unity throws. So we fail silently-ish.
            return false;
        }

        for (int i = 0; i < players.Length; i++)
        {
            var p = players[i];
            if (p == null) continue;

            Vector3 d = p.transform.position - center;
            if (d.sqrMagnitude <= r2) return true;
        }

        return false;
    }

    private bool IsAnyItemBlocking(Vector3 center, float radius)
    {
        var cols = Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (!c) continue;

            // Anything with a NetworkItem counts as "an item is here"
            var ni = c.GetComponentInParent<NetworkItem>();
            if (ni != null) return true;
        }
        return false;
    }

    [PunRPC]
    private void RPC_SpawnLoot(int prefabIndex, int itemId, Vector3 pos, Quaternion rot)
    {
        if (SpawnOnce && _hasSpawned) return;

        if (SpawnPrefabs == null || prefabIndex < 0 || prefabIndex >= SpawnPrefabs.Length) return;

        // Double-check on every client to avoid stacking duplicates in the same spot
        if (IsAnyItemBlocking(pos, BlockIfItemWithin)) return;

        GameObject prefab = SpawnPrefabs[prefabIndex];
        if (prefab == null) return;

        GameObject go = Instantiate(prefab, pos, rot);

        // Force shared ItemID (no PhotonView on item needed)
        var ni = go.GetComponentInChildren<NetworkItem>(true);
        if (ni != null)
            ni.InitializeFromSpawner(itemId, registerNow: true);

        _hasSpawned = true;
        _spawnedItemId = itemId;
    }

    private int ComputeSpawnItemId(int prefabIndex)
    {
        // Deterministic per-spawner base id, then mix in prefabIndex.
        int baseId = ComputeDeterministicIdForTransform(transform);

        unchecked
        {
            int id = baseId;
            id = (id * 486187739) ^ (prefabIndex * 16777619) ^ 0x5F3759DF;
            id &= 0x7FFFFFFF;
            if (id == 0) id = 1;
            return id;
        }
    }

    private static int ComputeDeterministicIdForTransform(Transform t)
    {
        // Similar idea to your NetworkItem deterministic ID, but lighter.
        // Scene + hierarchy path (with sibling indices) is stable across clients in the same scene.
        System.Text.StringBuilder sb = new System.Text.StringBuilder(256);

        string sceneName = t.gameObject.scene.IsValid() ? t.gameObject.scene.name : "NoScene";
        sb.Append(sceneName).Append('|');

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
            sb.Append(tr.GetSiblingIndex().ToString("D4")).Append(':').Append(tr.name);
            if (i != stack.Count - 1) sb.Append('/');
        }

        // FNV-1a 32-bit
        uint hash = 2166136261u;
        string key = sb.ToString();
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= 16777619u;
        }

        int id = (int)(hash & 0x7FFFFFFF);
        if (id == 0) id = 1;
        return id;
    }

    private void OnDrawGizmos()
    {
        if (!DrawGizmos) return;

        Vector3 pos = (SpawnPoint != null) ? SpawnPoint.position : transform.position;

        Gizmos.DrawWireSphere(pos, PlayerTriggerRadius);
        Gizmos.DrawWireSphere(pos, BlockIfItemWithin);
    }
}
//compile