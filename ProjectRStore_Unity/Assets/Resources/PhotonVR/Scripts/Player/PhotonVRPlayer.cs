using System;
using System.Collections.Generic;
using UnityEngine;

using Photon.Pun;
using Photon.Realtime;
using TMPro;

namespace Photon.VR.Player
{
    public class PhotonVRPlayer : MonoBehaviourPun
    {
        [Header("Objects")]
        public Transform Head;
        public Transform Body;
        public Transform LeftHand;
        public Transform RightHand;

        [Header("Item Anchors (Mirror Only)")]
        public Transform LeftHandItemAnchor;
        public Transform RightHandItemAnchor;

        [Tooltip("The objects that will get the colour of the player applied to them")]
        public List<MeshRenderer> ColourObjects = new List<MeshRenderer>();

        [Space]
        public List<CosmeticSlot> CosmeticSlots = new List<CosmeticSlot>();

        [Header("Other")]
        public TextMeshProUGUI NameText;
        public bool HideLocalPlayer = true;

        private void Awake()
        {
            if (photonView.IsMine)
            {
                PhotonVRManager.Manager.LocalPlayer = this;

                if (HideLocalPlayer)
                {
                    if (Head) Head.gameObject.SetActive(false);
                    if (Body) Body.gameObject.SetActive(false);
                    if (RightHand) RightHand.gameObject.SetActive(false);
                    if (LeftHand) LeftHand.gameObject.SetActive(false);
                    if (NameText) NameText.gameObject.SetActive(false);
                }
            }

            DontDestroyOnLoad(gameObject);
            _RefreshPlayerValues();
        }

        private void Update()
        {
            if (!photonView.IsMine || PhotonVRManager.Manager == null)
                return;

            Head.SetPositionAndRotation(
                PhotonVRManager.Manager.Head.position,
                PhotonVRManager.Manager.Head.rotation
            );

            LeftHand.SetPositionAndRotation(
                PhotonVRManager.Manager.LeftHand.position,
                PhotonVRManager.Manager.LeftHand.rotation
            );

            RightHand.SetPositionAndRotation(
                PhotonVRManager.Manager.RightHand.position,
                PhotonVRManager.Manager.RightHand.rotation
            );
        }

        public void RefreshPlayerValues()
        {
            photonView.RPC(nameof(RPCRefreshPlayerValues), RpcTarget.AllBuffered);
        }

        [PunRPC]
        private void RPCRefreshPlayerValues()
        {
            _RefreshPlayerValues();
        }

        private void _RefreshPlayerValues()
        {
            if (NameText && photonView.Owner != null)
                NameText.text = photonView.Owner.NickName;

            if (photonView.Owner != null &&
                photonView.Owner.CustomProperties.ContainsKey("Colour"))
            {
                Color col = JsonUtility.FromJson<Color>(
                    (string)photonView.Owner.CustomProperties["Colour"]
                );

                foreach (var r in ColourObjects)
                    if (r) r.material.color = col;
            }

            if (photonView.Owner != null &&
                photonView.Owner.CustomProperties.ContainsKey("Cosmetics"))
            {
                var cosmetics =
                    photonView.Owner.CustomProperties["Cosmetics"]
                    as Dictionary<string, string>;

                if (cosmetics == null) return;

                foreach (var kvp in cosmetics)
                {
                    foreach (var slot in CosmeticSlots)
                    {
                        if (slot.SlotName != kvp.Key) continue;

                        foreach (Transform obj in slot.Object)
                            if (obj) obj.gameObject.SetActive(obj.name == kvp.Value);
                    }
                }
            }
        }

        [Serializable]
        public class CosmeticSlot
        {
            public string SlotName;
            public Transform Object;
        }
    }
}
