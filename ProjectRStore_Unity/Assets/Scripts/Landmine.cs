using Photon.Pun;
using UnityEngine;

/// <summary>
/// Landmine
/// - Put this on a networked mine object with a PhotonView.
/// - Needs at least ONE Collider set to IsTrigger to detect exits.
/// - When something exits the trigger:
///     * Spawns an explosion using Photon (PhotonNetwork.Instantiate)
///     * Disables ALL mine Colliders + Renderers (so it "vanishes" + can't trigger)
/// - After RespawnDelay seconds, everything comes back.
/// 
/// IMPORTANT for explosion:
/// - Put your explosion prefab in a Resources folder (ex: Assets/Resources/Explosion.prefab)
/// - The explosion prefab should have a PhotonView if you want it networked.
/// 
/// Save as: Landmine.cs
/// </summary>
[RequireComponent(typeof(PhotonView))]
public class Landmine : MonoBehaviourPun
{
    public enum TriggerAuthority
    {
        MasterClient,
        Owner,
        Anyone
    }

    [Header("Authority (prevents double-triggers)")]
    public TriggerAuthority Authority = TriggerAuthority.MasterClient;

    [Header("Explosion (Photon)")]
    [Tooltip("Resources prefab name for PhotonNetwork.Instantiate. Example: 'Explosion' (for Resources/Explosion.prefab)")]
    public string ExplosionPrefabResourceName = "Explosion";

    [Tooltip("Offset for explosion spawn (world space).")]
    public Vector3 ExplosionOffset = Vector3.zero;

    [Header("Respawn")]
    public float RespawnDelay = 30f;

    [Header("Optional filter")]
    [Tooltip("If set, only colliders with this tag (or their root) will trigger the mine. Leave blank for anything.")]
    public string RequiredTag = "Player";

    [Header("Auto-collected (leave empty to auto-fill)")]
    public Collider[] MineColliders;
    public Renderer[] MineRenderers;

    [Header("Debug")]
    public bool DebugLogs = false;

    private bool _armed = true;
    private double _respawnAt = -1;

    private void Awake()
    {
        // Auto-collect everything if not assigned
        if (MineColliders == null || MineColliders.Length == 0)
            MineColliders = GetComponentsInChildren<Collider>(true);

        if (MineRenderers == null || MineRenderers.Length == 0)
            MineRenderers = GetComponentsInChildren<Renderer>(true);

        ApplyState(_armed);
    }

    private void Update()
    {
        if (_armed) return;
        if (_respawnAt <= 0) return;
        if (!PhotonNetwork.InRoom) return;

        if (PhotonNetwork.Time >= _respawnAt)
        {
            // Whoever currently has authority will re-arm it (covers master-switch reasonably well)
            if (HasAuthority())
            {
                photonView.RPC(nameof(RPC_SetState), RpcTarget.AllViaServer, true, -1d);
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!_armed) return;
        if (other == null) return;

        // ignore self / own hierarchy
        if (other.transform.root == transform.root)
            return;

        // optional tag filter
        if (!string.IsNullOrWhiteSpace(RequiredTag))
        {
            if (!other.CompareTag(RequiredTag) && !other.transform.root.CompareTag(RequiredTag))
                return;
        }

        // Only one "authority" should start it to avoid duplicates
        if (!HasAuthority())
            return;

        // Start explosion + hide mine for everyone
        photonView.RPC(nameof(RPC_Explode), RpcTarget.AllViaServer, PhotonNetwork.Time);
    }

    private bool HasAuthority()
    {
        switch (Authority)
        {
            case TriggerAuthority.MasterClient:
                return PhotonNetwork.IsMasterClient;

            case TriggerAuthority.Owner:
                return photonView.IsMine;

            case TriggerAuthority.Anyone:
                return true;

            default:
                return PhotonNetwork.IsMasterClient;
        }
    }

    [PunRPC]
    private void RPC_Explode(double serverTime)
    {
        if (!_armed) return; // already exploded (prevents duplicate RPC spam)

        _armed = false;
        _respawnAt = serverTime + Mathf.Max(0f, RespawnDelay);

        ApplyState(false);

        // Spawn explosion ONCE using Photon (only the authority does the instantiate)
        if (HasAuthority())
            SpawnExplosionPhoton();

        if (DebugLogs) Debug.Log($"[Landmine] Exploded. Respawn at (PhotonTime) {_respawnAt:0.00}", this);
    }

    private void SpawnExplosionPhoton()
    {
        if (string.IsNullOrWhiteSpace(ExplosionPrefabResourceName))
            return;

        Vector3 pos = transform.position + ExplosionOffset;

        // Note: prefab must be in Resources and have a PhotonView to be networked properly.
        PhotonNetwork.Instantiate(ExplosionPrefabResourceName, pos, Quaternion.identity);
    }

    [PunRPC]
    private void RPC_SetState(bool armed, double respawnAt)
    {
        _armed = armed;
        _respawnAt = respawnAt;

        ApplyState(armed);

        if (DebugLogs) Debug.Log($"[Landmine] State set: armed={armed}", this);
    }

    private void ApplyState(bool enabled)
    {
        if (MineColliders != null)
        {
            for (int i = 0; i < MineColliders.Length; i++)
            {
                var c = MineColliders[i];
                if (c != null) c.enabled = enabled;
            }
        }

        if (MineRenderers != null)
        {
            for (int i = 0; i < MineRenderers.Length; i++)
            {
                var r = MineRenderers[i];
                if (r != null) r.enabled = enabled;
            }
        }
    }
}
