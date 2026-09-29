using System.Reflection;
using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

/// <summary>Discovers <see cref="IComposableWidget"/> implementations in plugin folders.</summary>
public static class WidgetAssemblyLoader
{
    public sealed record LoadResult(int Registered, IReadOnlyList<string> Errors);

    /// <summary>
    /// Scans <paramref name="root"/> (recursively) for *.Widget.dll and registers every public,
    /// parameterless widget type. Failures are collected, never thrown.
    /// </summary>
    public static LoadResult LoadInto(WidgetRegistry registry, string root)
    {
        var errors = new List<string>();
        var count = 0;
        if (!Directory.Exists(root)) return new LoadResult(0, errors);

        foreach (var file in Directory.EnumerateFiles(root, "*.Widget.dll", SearchOption.AllDirectories))
        {
            try
            {
                var asm = Assembly.LoadFrom(file);
                foreach (var type in asm.GetExportedTypes())
                {
                    if (type.IsAbstract || type.IsInterface || !typeof(IComposableWidget).IsAssignableFrom(type)) continue;
                    if (type.GetConstructor(Type.EmptyTypes) is null) continue;
                    try
                    {
                        registry.Register(() => (IComposableWidget)Activator.CreateInstance(type)!);
                        count++;
                    }
                    catch (WidgetRegistrationException ex) { errors.Add($"{type.FullName}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException
                                          or FileNotFoundException or TargetInvocationException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return new LoadResult(count, errors);
    }
}
