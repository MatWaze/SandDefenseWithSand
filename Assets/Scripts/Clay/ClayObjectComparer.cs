using System.Collections.Generic;
using Clayxels;
using UnityEngine;

public class ClayObjectComparer : IComparer<ClayObject>
{
    public int Compare(ClayObject x, ClayObject y)
    {
        int result = x.transform.position.y.CompareTo(y.transform.position.y);

        if (result == 0)
        {
            return x.GetEntityId().CompareTo(y.GetEntityId());
        }

        return result;
    }
}
