using System;

namespace UmamusumeCompanion
{
    internal static class ShoppingRules
    {
        // RefuelLogicC stops at nine litres and charges one currency unit per litre.
        internal static int FuelLitres(float cash, float canPrice, int room, int level)
        {
            if (room < 0 || canPrice < 0 || level < 0 || level >= 9) return 0;
            return (int)Math.Max(0f, Math.Min(9 - level, cash - canPrice - room));
        }

        internal static bool Affordable(float cash, float price, int room)
        {
            return room >= 0 && price > 0 && cash - price >= room;
        }

        internal static string Checks()
        {
            int checks = 0;
            foreach (int room in new[] { 0, 20, 25, 30, 35, 40, 45 })
            foreach (int price in new[] { 5, 15 })
            foreach (int delta in new[] { -1, 0, 1 })
            {
                if (Affordable(room + price + delta, price, room) != (delta >= 0))
                    throw new InvalidOperationException("Room reserve boundary");
                checks++;
            }
            foreach (int room in new[] { 0, 20, 45 })
            foreach (int canPrice in new[] { 0, 10 })
            foreach (int cash in new[] { room - 1, room, room + canPrice, room + canPrice + 1, room + canPrice + 9, 200 })
            foreach (int level in new[] { 0, 4, 9 })
            {
                int litres = FuelLitres(cash, canPrice, room, level);
                int expected = 0;
                while (expected + level < 9 && cash - canPrice - expected - 1 >= room) expected++;
                if (litres != expected) throw new InvalidOperationException("Can refill / room reserve");
                checks++;
            }
            return checks + " checks passed (prices, refill, room reserve).";
        }
    }
}
