using System.Runtime.Versioning;

namespace SharpAstro.AppShell;

/// <summary>Where the per-user registry values of <see cref="AppIdentity.Register(string, string, string?)"/> and
/// <see cref="LaunchScheme"/> are read and written: the registry of the current user on Windows, a fake in a test
/// (a unit test touches no registry). Every sub key is under <c>HKCU</c>.</summary>
internal interface IUserRegistry
{
    /// <summary>The string value <paramref name="name"/> (<c>""</c> is the key's default) of
    /// <c>HKCU\<paramref name="subKey"/></c>, or null when the key or the value is missing or not a string.</summary>
    string? Read(string subKey, string name);

    /// <summary>Whether <c>HKCU\<paramref name="subKey"/></c> exists, whatever it holds.</summary>
    bool KeyExists(string subKey);

    /// <summary>Sets the string value, creating the key.</summary>
    void Write(string subKey, string name, string value);

    /// <summary>Deletes the key and everything under it; a key that is not there is not an error.</summary>
    void DeleteTree(string subKey);
}

/// <summary>The registry of the current user (Windows only).</summary>
[SupportedOSPlatform("windows")]
internal sealed class UserRegistry : IUserRegistry
{
    /// <inheritdoc />
    public string? Read(string subKey, string name)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(name) as string;
    }

    /// <inheritdoc />
    public bool KeyExists(string subKey)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
        return key is not null;
    }

    /// <inheritdoc />
    public void Write(string subKey, string name, string value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void DeleteTree(string subKey) =>
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
}
