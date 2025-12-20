using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;


public class ItemNetworkManager : MonoBehaviourPun
{
    public static ItemNetworkManager Instance;

    private Dictionary<int, Rigidbody> items = new();

    void Awake()
    {
        Instance = this;

        foreach (NetworkItem item in FindObjectsOfType<NetworkItem>())
        {
            Rigidbody rb = item.GetComponent<Rigidbody>();
            items[item.ItemID] = rb;
        }
    }

    // ---------- GRAB ----------
    public void GrabItem(int itemID, int actorNumber, bool leftHand)
    {
        photonView.RPC(
            nameof(RPC_GrabItem),
            RpcTarget.Others,
            itemID,
            actorNumber,
            leftHand
        );
    }

    [PunRPC]
    void RPC_GrabItem(int itemID, int actorNumber, bool leftHand)
    {
        if (!items.TryGetValue(itemID, out Rigidbody rb))
            return;

        rb.isKinematic = true;
    }

    // ---------- RELEASE ----------
    public void ReleaseItem(
        int itemID,
        Vector3 position,
        Quaternion rotation,
        Vector3 velocity,
        Vector3 angularVelocity)
    {
        photonView.RPC(
            nameof(RPC_ReleaseItem),
            RpcTarget.Others,
            itemID,
            position,
            rotation,
            velocity,
            angularVelocity
        );
    }

    [PunRPC]
    void RPC_ReleaseItem(
        int itemID,
        Vector3 position,
        Quaternion rotation,
        Vector3 velocity,
        Vector3 angularVelocity)
    {
        if (!items.TryGetValue(itemID, out Rigidbody rb))
            return;

        rb.transform.SetParent(null);
        rb.transform.SetPositionAndRotation(position, rotation);

        rb.isKinematic = false;
        rb.velocity = velocity;
        rb.angularVelocity = angularVelocity;
    }

    // ---------- LATE JOIN ----------
    public void SendFullState(Player newPlayer)
    {
        foreach (var pair in items)
        {
            Rigidbody rb = pair.Value;

            photonView.RPC(
                nameof(RPC_Snapshot),
                newPlayer,
                pair.Key,
                rb.transform.position,
                rb.transform.rotation,
                rb.velocity,
                rb.angularVelocity,
                rb.isKinematic
            );
        }
    }

    [PunRPC]
    void RPC_Snapshot(
        int itemID,
        Vector3 pos,
        Quaternion rot,
        Vector3 vel,
        Vector3 angVel,
        bool kinematic)
    {
        if (!items.TryGetValue(itemID, out Rigidbody rb))
            return;

        rb.transform.SetPositionAndRotation(pos, rot);
        rb.isKinematic = kinematic;

        if (!kinematic)
        {
            rb.velocity = vel;
            rb.angularVelocity = angVel;
        }
    }
}
