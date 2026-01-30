using LotroKoniecDev.Application.Abstractions;

namespace LotroKoniecDev.Infrastructure.Diagnostics;

public sealed class WriteAccessChecker : IWriteAccessChecker
{
    public bool CanWriteTo(string directoryPath)
    {
        try
        {
            string testFile = Path.Combine(directoryPath,
                $".lotrokoniec_writetest_{Guid.NewGuid():N}");

            using (File.Create(testFile, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
