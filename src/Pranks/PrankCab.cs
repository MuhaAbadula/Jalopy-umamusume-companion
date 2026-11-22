using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 38: cab parts and car checks shared by the cab pranks (N1 washer, N2 horn, N4 handbrake).
    /// Parts are looked up once under the player's car (no FindObjectsOfType per frame), with the reach test's lookup as fallback.
    /// </summary>
    public partial class Life
    {
        /// <summary>A cab part of type T in the player's car: under the car driver root, else the car logic root, else the nearest
        /// one to her head (reach test lookup, within 5 u). Cache it in the caller.</summary>
        T CabPart<T>() where T : Component
        {
            T c = null;
            if (carCtl == null) SpeedKmh();
            if (carCtl != null) c = carCtl.transform.root.GetComponentInChildren<T>(true);
            if (c == null && car != null) c = car.transform.root.GetComponentInChildren<T>(true);
            if (c == null)
            {
                var t = NearestOf<T>();
                if (t != null) c = t.GetComponent<T>();
            }
            return c;
        }

        CarPerformanceC carPerf;

        CarPerformanceC Perf()
        {
            if (carPerf == null && car != null) carPerf = car.GetComponent<CarPerformanceC>();
            return carPerf;
        }

        /// <summary>Null = a battery with charge is in, else why not (the game shows a red warning or throws without one).</summary>
        string BatteryWhy()
        {
            var p = Perf();
            if (p == null) return "no car parts";
            if (p.installedBattery == null) return "no battery";
            var b = p.installedBattery.GetComponent<EngineComponentC>();
            if (b == null || b.charge <= 0f) return "battery flat";
            return null;
        }

        /// <summary>Washer water charges left (-1 = no tank).</summary>
        int WaterCharges()
        {
            var p = Perf();
            if (p == null || p.waterTankObj == null) return -1;
            var w = p.waterTankObj.GetComponent<EngineComponentC>();
            return w != null ? w.currentWaterCharges : -1;
        }

        DirectorC directorC;

        /// <summary>Game daytime (DirectorC.isDay), else our clock (not 22-5 h).</summary>
        bool IsDay()
        {
            if (directorC == null && logic != null && logic.director != null) directorC = logic.director.GetComponent<DirectorC>();
            return directorC != null ? directorC.isDay && !night : !night;
        }

        /// <summary>The driver presses the throttle (his key bindings 8/9 or the pad), same test as WheelScriptPCC.</summary>
        static bool ThrottleDown()
        {
            try
            {
                var m = MainMenuC.Global;
                if (m != null && m.assignedInputStrings != null && m.assignedInputStrings.Length > 9
                    && (Input.GetKey(m.assignedInputStrings[8]) || Input.GetKey(m.assignedInputStrings[9]))) return true;
            }
            catch (System.Exception) { }
            try { return Input.GetAxis("JoypadAccelerate") > 0.1f; }
            catch (System.Exception) { return false; }
        }
    }
}
