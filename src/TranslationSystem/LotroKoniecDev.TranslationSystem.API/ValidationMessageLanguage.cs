using System.Runtime.CompilerServices;
using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.API;

/// <summary>
/// English validation messages whatever the OS language (ADR-0044 §1). A module initializer, so the
/// host and every test that runs a validator get the same messages.
/// </summary>
internal static class ValidationMessageLanguage
{
    [ModuleInitializer]
    internal static void KeepEnglish()
    {
        ValidatorOptions.Global.LanguageManager.Enabled = false;
    }
}
