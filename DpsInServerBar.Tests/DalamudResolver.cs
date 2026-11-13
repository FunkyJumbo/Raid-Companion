using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace DpsInServerBar.Tests;

/// <summary>
/// The plugin is built against the Dalamud assemblies without copying them: when a test touches a type that needs
/// one, it is loaded from the local Dalamud install.
/// </summary>
internal static class DalamudResolver
{
    [ModuleInitializer]
    internal static void Register()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                  "XIVLauncher", "addon", "Hooks", "dev");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }
}
