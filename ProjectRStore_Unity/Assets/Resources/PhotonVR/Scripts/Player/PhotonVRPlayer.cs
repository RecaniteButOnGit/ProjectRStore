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

        [Tooltip("The objects that will get the colour of the player applied to them")]
        public List<MeshRenderer> ColourObjects = new List<MeshRenderer>();

        [Space]
        [Tooltip("Feel free to add as many slots as you feel necessary")]
        public List<CosmeticSlot> CosmeticSlots = new List<CosmeticSlot>();

        [Header("Other")]
        public TextMeshProUGUI NameText; // ✅ FIXED TYPE
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

            // Auto-cleaned by Photon when leaving room
            DontDestroyOnLoad(gameObject);

            _RefreshPlayerValues();
        }

        private void Update()
        {
            if (!photonView.IsMine)
                return;

            if (PhotonVRManager.Manager == null)
                return;

            // Mirror real rig → network rig
            Head.position = PhotonVRManager.Manager.Head.position;
            Head.rotation = PhotonVRManager.Manager.Head.rotation;

            RightHand.position = PhotonVRManager.Manager.RightHand.position;
            RightHand.rotation = PhotonVRManager.Manager.RightHand.rotation;

            LeftHand.position = PhotonVRManager.Manager.LeftHand.position;
            LeftHand.rotation = PhotonVRManager.Manager.LeftHand.rotation;
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
            // ---- NAME ----
            if (NameText != null && photonView.Owner != null)
            {
                NameText.text = photonView.Owner.NickName;
            }

            // ---- COLOUR ----
            if (photonView.Owner != null &&
                photonView.Owner.CustomProperties.ContainsKey("Colour"))
            {
                Color col = JsonUtility.FromJson<Color>(
                    (string)photonView.Owner.CustomProperties["Colour"]
                );

                foreach (MeshRenderer renderer in ColourObjects)
                {
                    if (renderer != null)
                        renderer.material.color = col;
                }
            }

            // ---- COSMETICS ----
            if (photonView.Owner != null &&
                photonView.Owner.CustomProperties.ContainsKey("Cosmetics"))
            {
                var cosmetics =
                    photonView.Owner.CustomProperties["Cosmetics"]
                    as Dictionary<string, string>;

                if (cosmetics != null)
                {
                    foreach (var cosmetic in cosmetics)
                    {
                        foreach (CosmeticSlot slot in CosmeticSlots)
                        {
                            if (slot.SlotName != cosmetic.Key)
                                continue;

                            foreach (Transform obj in slot.Object)
                            {
                                if (obj != null)
                                    obj.gameObject.SetActive(obj.name == cosmetic.Value);
                            }
                        }
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
