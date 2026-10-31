using System.Security.Cryptography;
using System.Text;

namespace PhotoOrganizer;

internal sealed class OperationLock : IDisposable
{
    private readonly Semaphore semaphore;
    public OperationLock(string root)
    {
        string name = "Local\\PhotoOrganizer-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(PathSafety.Normalize(FileIdentity.DirectoryPath(root)).ToUpperInvariant())));
        semaphore = new Semaphore(1, 1, name);
        if (semaphore.WaitOne(0)) return;
        semaphore.Dispose();
        throw new InvalidOperationException("BusyDestination");
    }
    public void Dispose() { semaphore.Release(); semaphore.Dispose(); }
}
