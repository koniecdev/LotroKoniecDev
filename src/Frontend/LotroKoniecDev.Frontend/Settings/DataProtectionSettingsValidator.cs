using FluentValidation;

namespace LotroKoniecDev.Frontend.Settings;

/// <summary>
/// <see cref="DataProtectionSettings.KeyRingPath"/> is optional and has no binding-time constraint:
/// null or empty both legitimately mean "use the framework default keyring location" (valid in
/// Development). The only real rule — the path MUST be set outside Development — is a startup guard
/// inside <c>AddFrontendDataProtection</c>, because Data Protection is configured before the DI
/// container is built, so there is no validated options instance to resolve at that point. This
/// validator therefore intentionally carries no rules.
/// </summary>
internal sealed class DataProtectionSettingsValidator : AbstractValidator<DataProtectionSettings>;
