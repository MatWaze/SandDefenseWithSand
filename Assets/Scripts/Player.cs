using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SandDefenseWithSand
{
    public class Player : MonoBehaviour
    {
        [SerializeField]
        private Transform leftArmTransform;

        public void Move()
        {
            leftArmTransform.localPosition += new Vector3(0, .4f, 0);
        }
    }
}
