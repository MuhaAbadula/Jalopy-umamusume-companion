using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 16: after she boards, no radio / window action of her own for a while (the base chance and cooldowns stay as they were).
    /// Life.PickBeat asks SelfActBlocked(); the timer runs down only while she is in her seat and the player sits too.
    /// </summary>
    public partial class Life
    {
        /// <summary>Average block after boarding, seconds (slider "SelfActDelay"). 0 = no block (the old behaviour).</summary>
        public static float BoardDelay = 90f;

        bool selfActWasInCar;
        float selfActLeft;
        SeatLogicC playerSeatLogic;

        /// <summary>Called every frame from Update. A boarding = she was not in the car and now is (outing return, morning warp, level load).</summary>
        void SelfActStep(float dt, bool inCar)
        {
            if (inCar && !selfActWasInCar)
            {
                if (BoardDelay > 0.5f)
                {
                    selfActLeft = Random.Range(BoardDelay * 0.667f, BoardDelay * 1.333f);
                    Debug.Log("[UncleSwap] SelfAct: boarded, radio/window blocked for " + Mathf.RoundToInt(selfActLeft) + " s");
                }
                else selfActLeft = 0f;
            }
            selfActWasInCar = inCar;
            if (selfActLeft > 0f && inCar && PlayerSeated())
            {
                selfActLeft -= dt;
                if (selfActLeft <= 0f) Debug.Log("[UncleSwap] SelfAct: block over, radio/window free");
            }
        }

        bool PlayerSeated()
        {
            try
            {
                if (playerSeatLogic == null && car != null && car.playerSeat != null) playerSeatLogic = car.playerSeat.GetComponent<SeatLogicC>();
                return playerSeatLogic == null || playerSeatLogic.isSat;   // unknown seat: count anyway
            }
            catch (System.Exception) { return true; }
        }

        /// <summary>True while she must not turn the radio or the window crank on her own.</summary>
        bool SelfActBlocked() { return selfActLeft > 0f; }
    }
}
