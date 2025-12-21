using UnityEngine;

public class NetworkItem : MonoBehaviour
{
    [Header("Identity")]
    public int ItemID;

    [Header("Economy")]
    public int BuyPrice = 0;
    public int SellPriceOverride = 0;

    [Header("Grab Behavior")]
    public bool SnapGrab = false;
    public Vector3 SnapLocalPosition;
    public Vector3 SnapLocalEulerAngles;

    [Header("Backpack Behavior")]
    public bool CanBeStoredInBackpack = true;

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
}
