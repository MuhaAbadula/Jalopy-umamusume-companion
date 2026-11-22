using System;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// In Istanbul on the way home with her (ReturnTrip): when the map is switched to the way home she is glad
    /// (remark "home_way", within 3 min, waits for a free bubble). The talks themselves are in ReturnTrip.
    /// </summary>
    public partial class Life
    {
        /// <summary>True when a remark would show right now (no game dialogue, bubble free).</summary>
        public Func<bool> CanSay;

        bool homeSaid, homeWas;
        float homeAt = -1f;

        void IstanbulEvents(float now, bool inCar, bool asleep)
        {
            if (!ReturnTrip.Active || logic == null) return;
            if (route == null && logic.director != null) route = logic.director.GetComponent<RouteGeneratorC>();
            if (route == null || route.routeLevel != 6) return;

            bool home = route.drivingTowardsHome;
            if (home && !homeWas && homeAt < 0f) homeAt = now;
            homeWas = home;
            if (homeSaid || homeAt < 0f || !inCar || asleep) return;
            if (now - homeAt > 180f) { homeSaid = true; return; }
            if (CanSay != null && !CanSay()) return;
            homeSaid = true;
            Debug.Log("[Umamusume companion] event: heading home together");
            Attend(Look.Player, 2.5f, 0.9f);
            Emote("happy", 2.5f, 0.9f);
            SayMaybe("home_way", 1f, 600f);
        }
    }
}
