using System.Runtime.CompilerServices;
using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.API;

/// <summary>
/// Keeps the validation messages of this API in English, whatever the language of the OS
/// (ADR-0044 §1, #1092). A module initializer runs before any other code of this assembly, so the
/// host and every test that runs one of its validators get the same messages.
/// </summary>
internal static class ValidationMessageLanguage
{
    [ModuleInitializer]
    internal static void KeepEnglish()
    {
        ValidatorOptions.Global.LanguageManager.Enabled = false;
    }
}
