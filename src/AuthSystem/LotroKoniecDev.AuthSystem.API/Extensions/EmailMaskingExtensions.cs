namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class EmailMaskingExtensions
{
    extension(string email)
    {
        public string MaskEmail()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(email);
            
            int atIndex = email.IndexOf('@', StringComparison.Ordinal);
            return atIndex <= 0 ? "***" : string.Concat(email[0].ToString(), "***", email[atIndex..]);
        }
    }
}
