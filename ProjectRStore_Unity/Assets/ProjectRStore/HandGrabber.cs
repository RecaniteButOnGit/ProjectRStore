using UnityEngine;
using Photon.Pun;


public class HandGrabber : MonoBehaviour
{
    [Header("Settings")]
    public LayerMask itemLayer;
    public Transform grabAnchor; // usually the hand itself

    [Header("Input")]
    public bool isLeftHand; // for later input mapping

    private Rigidbody heldItem;
    private Collider[] overlapResults = new Collider[8];

    void Update()
    {
        if (PrimaryPressed())
        {
            if (heldItem == null)
                TryGrab();
            else
                Release();
        }
    }

    bool PrimaryPressed()
    {
        // TEMP: replace with your real VR input
        // Example placeholder:
        return Input.GetKeyDown(isLeftHand ? KeyCode.Q : KeyCode.E);
    }

    void TryGrab()
    {
        int count = Physics.OverlapSphereNonAlloc(
            transform.position,
            0.1f,
            overlapResults,
            itemLayer,
            QueryTriggerInteraction.Ignore
        );

        if (count == 0)
            return;

        // pick closest
        Rigidbody best = null;
        float bestDist = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Rigidbody rb = overlapResults[i].attachedRigidbody;
            if (!rb) continue;

            float d = Vector3.Distance(transform.position, rb.position);
            if (d < bestDist)
            {
                best = rb;
                bestDist = d;
            }
        }

        if (!best)
            return;

        Grab(best);
    }

    void Grab(Rigidbody rb)
    {
        heldItem = rb;

        rb.isKinematic = true;
        rb.transform.SetParent(grabAnchor, true);

        // optional: snap closer
        rb.transform.localPosition = Vector3.zero;
        rb.transform.localRotation = Quaternion.identity;

        ItemNetworkManager.Instance.GrabItem(
    heldItem.GetComponent<NetworkItem>().ItemID,
    PhotonNetwork.LocalPlayer.ActorNumber,
    isLeftHand
);

    }

    void Release()
{
    Rigidbody rb = heldItem;
    NetworkItem net = rb.GetComponent<NetworkItem>();

    heldItem = null;

    rb.transform.SetParent(null);
    rb.isKinematic = false;

    ItemNetworkManager.Instance.ReleaseItem(
        net.ItemID,
        rb.position,
        rb.rotation,
        rb.velocity,
        rb.angularVelocity
    );
}

}

// compile plz