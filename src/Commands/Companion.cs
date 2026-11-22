using UnityEngine;

namespace UmamusumeCompanion
{
    internal enum Command { Exit, Follow, Stop, Board, Fetch }

    internal sealed class Companion
    {
        readonly CompanionDriver driver;
        readonly UncleLogicC actor;

        public Companion(CompanionDriver driver)
        {
            this.driver = driver;
            actor = driver.GetComponent<UncleLogicC>();
        }

        public bool Present => driver != null && driver.isActiveAndEnabled && actor != null &&
                               !actor.uncleGoneForever && !actor.uncleAtHome;
        public string Unavailable(Command cmd)
        {
            if (!Present) return "Умы сейчас нет рядом";
            if (CompanionDriver.MorningWalk.IsWalking(driver) && !actor.readyToBoard) return "Ума идёт к машине";
            var state = driver.OutingState;
            if (state == CompanionDriver.Out.Exiting) return "Ума выходит из машины";
            if (state == CompanionDriver.Out.Boarding || actor.readyToBoard) return "Ума садится в машину";
            if (driver.MornHolding) return "Ума ждёт в номере";
            bool seated = state == CompanionDriver.Out.InCar && actor.isSat;
            bool outside = state == CompanionDriver.Out.Follow || state == CompanionDriver.Out.Wait;
            bool fetching = driver.Fetching;
            switch (cmd)
            {
                case Command.Exit: return seated ? null : "Ума уже снаружи";
                case Command.Follow:
                    if (state == CompanionDriver.Out.Follow && !fetching) return "Ума уже идёт за тобой";
                    break;
                case Command.Stop:
                    if (state == CompanionDriver.Out.Wait) return "Ума уже ждёт";
                    break;
                case Command.Board:
                    if (seated) return "Ума уже в машине";
                    if (actor.uncleCarTransform == null || actor.uncleDoorTrigger == null ||
                        actor.carNavBlocker == null || actor.relayNodeTransform == null) return "Машина недоступна";
                    break;
                case Command.Fetch:
                    if (fetching) return "Ума уже забирает ящик";
                    if (seated || outside) return null;
                    break;
            }
            return outside ? null : seated ? "Сначала попроси Уму выйти" : "Ума сейчас занята";
        }

        public string Run(Command cmd)
        {
            string reason = Unavailable(cmd);
            if (reason != null) return reason;
            if (cmd == Command.Fetch) return Feedback(driver.FetchKey());
            if (driver.Fetching)
            {
                driver.EndFetch();
                // A fetch temporarily uses the follow state with the crate as target.
                // Once cancelled, CompanionDriver refreshes the player target on its next update.
                if (cmd == Command.Follow) return "Ума идёт за тобой";
            }
            if (cmd == Command.Board)
            {
                driver.StartBoarding(actor, "command wheel");
                return "Ума возвращается в машину";
            }
            if (cmd == Command.Exit) Doze.OnG();
            return Feedback(driver.OutingKey());
        }

        static string Feedback(string result)
        {
            switch (result)
            {
                case "Outing: she gets out.": return "Ума выходит из машины";
                case "Outing: she waits here.": return "Ума ждёт здесь";
                case "Outing: she comes along.": return "Ума идёт за тобой";
                case "Outing: the car is moving.": return "Сначала останови машину";
                case "Outing: her door is blocked.": return "Освободи место у пассажирской двери";
                case "Outing: not now, talking.": return "Подожди, пока Ума договорит";
                case "Outing: not now, at the border.": return "Подожди до выезда с границы";
                case "Outing: not now, at the motel (she gets out there anyway).": return "У мотеля Ума выходит сама";
                case "Outing: not now, repair tutorial.": return "Сначала закончи ремонт";
                case "Outing: not now, asleep.": return "Ума спит";
                case "Fetching the nearest roadside crate.": return "Ума идёт за ящиком";
                case "No roadside crate nearby.": return "В радиусе 15 м от машины нет ящика";
                case "Boot lid unavailable.": return "Багажник недоступен";
                case "Stop with the passenger door clear first.": return "Останови машину и освободи пассажирскую дверь";
                default: return "Ума сейчас занята";
            }
        }
    }
}
