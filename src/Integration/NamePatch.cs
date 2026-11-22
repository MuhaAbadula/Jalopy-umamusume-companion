using HarmonyLib;

namespace UmamusumeCompanion
{
    /// <summary>
    /// The dialogue bubble resolves the speaker name from a localization key; the uncle's is "ui_name_01".
    /// After DirectorC.BubbleOn has filled the name, swap it for the character's own (other speakers untouched).
    /// </summary>
    public static class NamePatch
    {
        public const string UncleKey = "ui_name_01";
        public static string Name, NameEn;
        static bool applied;

        public static void Apply()
        {
            if (applied) return;
            var harmony = new Harmony("osobl.jalopy.umamusumecompanion");
            harmony.Patch(AccessTools.Method(typeof(DirectorC), "BubbleOn"),
                postfix: new HarmonyMethod(typeof(NamePatch), nameof(Postfix)));
            applied = true;
        }

        static void Postfix(DirectorC __instance, string _name)
        {
            if (_name != UncleKey || __instance == null || __instance.dialogueHolder == null) return;
            var ds = __instance.dialogueHolder.GetComponent<DialogueStuffsC>();
            if (ds == null) return;
            string pick = HasCyrillic(ds._name) ? (Name ?? NameEn) : (NameEn ?? Name);
            if (!string.IsNullOrEmpty(pick)) ds._name = pick;
        }

        static bool HasCyrillic(string s)
        {
            if (s == null) return false;
            foreach (char c in s)
                if (c >= 'Ѐ' && c <= 'ӿ') return true;
            return false;
        }
    }
}
