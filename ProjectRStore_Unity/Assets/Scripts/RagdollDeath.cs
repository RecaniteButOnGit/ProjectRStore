using System.Collections;
using Photon.Pun;
using UnityEngine;

/// <summary>
/// Ragdoll Death
/// - Anyone can call TriggerRagdollDeath().
/// - If caller is NOT the owner, it RPCs the OWNER to start it.
/// - OWNER then RPCs everyone to run the sequence on this object.
/// - Physics (isKinematic) is only changed by the OWNER.
/// - Now supports 3 rigidbodies: ObjectA + ObjectB + ObjectC (all treated the same for kinematic on/off).
/// </summary>
[AddComponentMenu("Ragdoll Death")]
[RequireComponent(typeof(PhotonView))]
public class RagdollDeath : MonoBehaviourPun
{
    [Header("Rigidbodies (OWNER toggles isKinematic on/off)")]
    public Rigidbody ObjectA;
    public Rigidbody ObjectB;
    public Rigidbody ObjectC;

    [Header("Reset Transform (everyone)")]
    [Tooltip("Transform to reset. If null, uses ObjectA.transform (if set) or this.transform.")]
    public Transform ResetTransform;

    public bool ResetLocalSpace = true;   // true = localPosition = Vector3.zero, false = position = Vector3.zero
    public bool ResetRotationToo = false;

    [Header("Objects to enable during the sequence (everyone)")]
    public GameObject[] ToggleObjects;

    [Header("Timing")]
    public float Duration = 5f;

    [Header("Debug")]
    public bool DebugLogs = false;

    private Coroutine _running;

    /// <summary>
    /// Public void you can call from anything.
    /// Works even if you are NOT the owner.
    /// </summary>
    public void TriggerRagdollDeath()
    {
        if (photonView.IsMine)
        {
            StartForEveryone();
            return;
        }

        if (photonView.Owner != null)
        {
            photonView.RPC(nameof(RPC_RequestStartFromOwner), photonView.Owner);
            if (DebugLogs) Debug.Log("[RagdollDeath] Requested owner to start.", this);
        }
        else
        {
            if (DebugLogs) Debug.LogWarning("[RagdollDeath] No owner found for PhotonView.", this);
        }
    }

    private void StartForEveryone()
    {
        // When the sequence starts, broadcast to everybody on that object
        photonView.RPC(nameof(RPC_StartRagdollDeath), RpcTarget.AllViaServer);
        if (DebugLogs) Debug.Log("[RagdollDeath] Owner started sequence for everyone.", this);
    }

    [PunRPC]
    private void RPC_RequestStartFromOwner()
    {
        if (!photonView.IsMine) return; // only owner responds
        StartForEveryone();
    }

    [PunRPC]
    private void RPC_StartRagdollDeath()
    {
        if (_running != null) StopCoroutine(_running);
        _running = StartCoroutine(Routine());
    }

    private IEnumerator Routine()
    {
        // Everyone: enable toggles
        SetToggleObjects(true);

        // Owner: un-kinematic all linked bodies
        if (photonView.IsMine)
            SetBodiesKinematic(false);

        yield return new WaitForSeconds(Duration);

        // Everyone: reset chosen transform
        var t = ResetTransform;
        if (t == null)
        {
            if (ObjectA != null) t = ObjectA.transform;
            else t = transform;
        }

        if (ResetLocalSpace) t.localPosition = Vector3.zero;
        else t.position = Vector3.zero;

        if (ResetRotationToo)
        {
            if (ResetLocalSpace) t.localRotation = Quaternion.identity;
            else t.rotation = Quaternion.identity;
        }

        // Owner: re-kinematic + clear velocities
        if (photonView.IsMine)
            SetBodiesKinematic(true);

        // Everyone: disable toggles
        SetToggleObjects(false);

        _running = null;
        if (DebugLogs) Debug.Log("[RagdollDeath] Finished.", this);
    }

    private void SetBodiesKinematic(bool kinematic)
    {
        ApplyBody(ObjectA, kinematic);
        ApplyBody(ObjectB, kinematic);
        ApplyBody(ObjectC, kinematic);
    }

    private void ApplyBody(Rigidbody rb, bool kinematic)
    {
        if (rb == null) return;

        if (kinematic)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }
        else
        {
            rb.isKinematic = false;
            rb.WakeUp();
        }
    }

    private void SetToggleObjects(bool on)
    {
        if (ToggleObjects == null) return;

        for (int i = 0; i < ToggleObjects.Length; i++)
        {
            if (ToggleObjects[i] != null)
                ToggleObjects[i].SetActive(on);
        }
    }
}
