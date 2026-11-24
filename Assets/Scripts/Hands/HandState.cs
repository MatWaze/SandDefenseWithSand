using UnityEngine;

namespace SandDefenseWithSand.Hands
{
    public enum HandState
    {
        Idle,
        Grab
    }

    public enum HandStateTransitions
    {
        IdleToGrab,
        IdleToGrabRight,
        GrabToIdle,
        GrabToIdleRight,
        GrabToDigLeft,
        GrabToDigRight,
        DigToGrabLeft,
        DigToGrabRight
    }
}
