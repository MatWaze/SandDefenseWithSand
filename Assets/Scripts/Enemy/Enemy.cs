using System;
using UnityEngine;

namespace SandDefenseWithSand.Enemy
{
    [Serializable]
    public struct Enemy
    {
        public float maxHealth;

        public float health;

        public float speed;

        public bool alive;

        public Vector3 destination;

        public void GetDamage(float damage)
        {
            if (!alive)
            {
                return;
            }

            if (health - damage <= 0)
            {
                alive = false;
                health = 0;
            }
            else
            {
                health -= damage;
            }
        }

        public float GetHealthPercentage()
        {
            return health / maxHealth;
        }
    }    
}
