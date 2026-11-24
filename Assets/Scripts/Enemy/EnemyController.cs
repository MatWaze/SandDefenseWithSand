using SandDefenseWithSand.Enemy;
using UnityEngine;
using UnityEngine.AI;

public class EnemyController : MonoBehaviour
{
    [SerializeField]
    private Animator _enemyAnimator;

    [SerializeField]
    private Enemy _enemy;

    private NavMeshAgent _agent;

    void Start()
    {
        _agent = GetComponent<NavMeshAgent>();

        SetWalkAnimation(true);
        _agent.speed = _enemy.speed;
        _agent.destination = _enemy.destination;
    }

    void Update()
    {
        if (_enemy.alive && _enemy.health <= 0f)
        {
            _agent.isStopped = true;
            _agent.speed = 0f;
            _enemy.alive = false;
            SetDeathAnimation();
        }
    }

    private void SetDeathAnimation()
    {
        string trigger = "WalkToDeath";

        _enemyAnimator.ResetTrigger(trigger);
        _enemyAnimator.SetTrigger(trigger);
    }

    private void SetWalkAnimation(bool isWalking)
    {
        string trigger = isWalking ? "IdleToWalk" : "WalkToIdle";

        _enemyAnimator.ResetTrigger(trigger);
        _enemyAnimator.SetTrigger(trigger);
    }
}
