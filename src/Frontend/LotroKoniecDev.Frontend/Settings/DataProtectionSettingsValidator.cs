using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// No rules: the one real rule, a path outside Development, is checked in
/// <c>AddFrontendDataProtection</c>, which runs before the DI container exists.
/// </summary>
internal sealed class DataProtectionSettingsValidator : AbstractValidator<DataProtectionSettings>;
