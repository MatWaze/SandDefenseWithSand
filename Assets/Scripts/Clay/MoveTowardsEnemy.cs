using UnityEngine;

namespace SandDefenseWithSand.Clay
{
    public class MoveTowardsEnemy : MonoBehaviour
    {
        [SerializeField]
        private Transform enemyTarget;

        [SerializeField]
        private float speed = 5f;

        void Update()
        {
            transform.position = Vector3.MoveTowards(transform.position, enemyTarget.position, speed * Time.deltaTime);
        }
    }
}
