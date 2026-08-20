using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// <see cref="DataProtectionSettings.KeyRingPath"/> is optional and nothing can be checked when it is
/// bound: null and empty both mean "use the framework's default keyring location", which is valid in
/// Development.
/// The only real rule, that the path must be set outside Development, is checked at startup inside
/// <c>AddFrontendDataProtection</c>, because Data Protection is configured before the DI container
/// exists and there is no validated options object at that point. So this validator has no rules on
/// purpose.
/// </summary>
internal sealed class DataProtectionSettingsValidator : AbstractValidator<DataProtectionSettings>;
