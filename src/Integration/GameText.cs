namespace UmamusumeCompanion
{
    internal static class GameText
    {
        internal static bool Russian => Language.CurrentLanguage() == LanguageCode.RU;

        internal static bool English => Language.CurrentLanguage() == LanguageCode.EN || RegionalEnglish(Language.CurrentLanguage());

        internal static bool RegionalEnglish(LanguageCode language) => language == LanguageCode.EN_GB || language == LanguageCode.EN_US;

        internal static string Choose(string english, string russian) => Russian ? russian : english;
    }
}
