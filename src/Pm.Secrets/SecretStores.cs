using Pm.Core.Storage;

namespace Pm.Secrets;

public static class SecretStores
{
    public static ISecretStore Create()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var store = new LibsecretStore();
                if (store.Probe()) return store;
            }
            catch
            {
                // fall through to the file store
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new DpapiStore();
            }
            catch
            {
                // fall through to the file store
            }
        }

        return new FileStore();
    }
}
