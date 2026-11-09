using System.Runtime.InteropServices;
using Pm.Core;
using Pm.Core.Storage;

namespace Pm.Secrets;

/// <summary>
/// GNOME Keyring / KWallet through libsecret. Values are base64 because libsecret stores text.
/// Uses the *v* variants that take a GHashTable, because .NET cannot call C varargs on Unix.
/// </summary>
public sealed class LibsecretStore : ISecretStore
{
    private const string SecretLibrary = "libsecret-1.so.0";
    private const string GlibLibrary = "libglib-2.0.so.0";

    private static readonly IntPtr HashFn;
    private static readonly IntPtr EqualFn;
    private static readonly IntPtr FreeFn;

    static LibsecretStore()
    {
        var glib = NativeLibrary.Load(GlibLibrary);
        HashFn = NativeLibrary.GetExport(glib, "g_str_hash");
        EqualFn = NativeLibrary.GetExport(glib, "g_str_equal");
        FreeFn = NativeLibrary.GetExport(glib, "g_free");
    }

    public string Name => "GNOME Keyring";
    public bool Secure => true;

    public byte[]? Get(string key)
    {
        using var attributes = new Attributes(key);
        var schema = Schema();
        IntPtr error = IntPtr.Zero;

        var result = secret_password_lookupv_sync(ref schema, attributes.Pointer, IntPtr.Zero, out error);
        ThrowOnError(error);
        if (result == IntPtr.Zero) return null;

        try
        {
            var text = Marshal.PtrToStringUTF8(result);
            return text is null ? null : Convert.FromBase64String(text);
        }
        finally
        {
            g_free(result);
        }
    }

    public void Set(string key, byte[] value)
    {
        using var attributes = new Attributes(key);
        var schema = Schema();
        var label = Marshal.StringToHGlobalAnsi("Pm device key");
        var password = Marshal.StringToHGlobalAnsi(Convert.ToBase64String(value));
        IntPtr error = IntPtr.Zero;

        try
        {
            var ok = secret_password_storev_sync(ref schema, attributes.Pointer, IntPtr.Zero,
                label, password, IntPtr.Zero, out error);
            ThrowOnError(error);
            if (!ok) throw new SecretStoreException("libsecret не сохранил ключ");
        }
        finally
        {
            Marshal.FreeHGlobal(label);
            Marshal.FreeHGlobal(password);
        }
    }

    public void Delete(string key)
    {
        using var attributes = new Attributes(key);
        var schema = Schema();
        IntPtr error = IntPtr.Zero;
        secret_password_clearv_sync(ref schema, attributes.Pointer, IntPtr.Zero, out error);
        ThrowOnError(error);
    }

    public bool Probe()
    {
        try
        {
            Get("probe");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SecretSchema Schema()
    {
        var attributes = new SecretSchemaAttribute[32];
        attributes[0] = new SecretSchemaAttribute
        {
            Name = Marshal.StringToHGlobalAnsi("key"),
            Type = 0,
        };

        return new SecretSchema
        {
            Name = Marshal.StringToHGlobalAnsi("dev.pm.devicekey"),
            Flags = 0,
            Attributes = attributes,
        };
    }

    private static void ThrowOnError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;

        try
        {
            var gerror = Marshal.PtrToStructure<GError>(error);
            var message = Marshal.PtrToStringUTF8(gerror.Message) ?? "неизвестная ошибка ключницы";
            throw new SecretStoreException("ключница: " + message);
        }
        finally
        {
            g_error_free(error);
        }
    }

    private sealed class Attributes : IDisposable
    {
        private readonly IntPtr _table;

        public Attributes(string key)
        {
            _table = g_hash_table_new_full(HashFn, EqualFn, FreeFn, FreeFn);
            g_hash_table_insert(_table, g_strdup("key"), g_strdup(key));
        }

        public IntPtr Pointer => _table;

        public void Dispose() => g_hash_table_destroy(_table);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchemaAttribute
    {
        public IntPtr Name;
        public int Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchema
    {
        public IntPtr Name;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public SecretSchemaAttribute[] Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public IntPtr Message;
    }

    [DllImport(SecretLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool secret_password_storev_sync(ref SecretSchema schema, IntPtr attributes,
        IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);

    [DllImport(SecretLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secret_password_lookupv_sync(ref SecretSchema schema,
        IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(SecretLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool secret_password_clearv_sync(ref SecretSchema schema,
        IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(GlibLibrary)]
    private static extern IntPtr g_hash_table_new_full(IntPtr hash, IntPtr equal,
        IntPtr keyDestroy, IntPtr valueDestroy);

    [DllImport(GlibLibrary)]
    private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(GlibLibrary)]
    private static extern void g_hash_table_destroy(IntPtr table);

    [DllImport(GlibLibrary)]
    private static extern IntPtr g_strdup(string value);

    [DllImport(GlibLibrary)]
    private static extern void g_free(IntPtr memory);

    [DllImport(GlibLibrary)]
    private static extern void g_error_free(IntPtr error);
}
