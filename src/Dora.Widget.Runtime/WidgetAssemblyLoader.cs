using System.Reflection;
using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

/// <summary>Discovers <see cref="IComposableWidget"/> implementations in plugin folders.</summary>
public static class WidgetAssemblyLoader
{
    public sealed record LoadResult(int Registered, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Scans <paramref name="root"/> (recursively) for *.Widget.dll and registers every public,
    /// parameterless widget type. A broken plugin must never stop the Host from starting, so every
    /// failure (bad image, missing dependency, throwing constructor, invalid manifest) is collected.
    /// </summary>
    public static LoadResult LoadInto(WidgetRegistry registry, string root, ICollection<IWidgetDetachHandler>? detachHandlers = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var count = 0;
        if (!Directory.Exists(root)) return new LoadResult(0, errors, warnings);

        foreach (var file in Directory.EnumerateFiles(root, "*.Widget.dll", SearchOption.AllDirectories))
        {
            try
            {
                var asm = Assembly.LoadFrom(file);
                foreach (var type in asm.GetExportedTypes())
                {
                    // Optional hand-over support that ships beside a widget (see IWidgetDetachHandler).
                    if (detachHandlers != null && !type.IsAbstract && !type.IsInterface &&
                        typeof(IWidgetDetachHandler).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
                    {
                        try { detachHandlers.Add((IWidgetDetachHandler)Activator.CreateInstance(type)!); }
                        catch (Exception ex)
                        {
                            var reason = ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
                            errors.Add($"{type.FullName}: {reason}");
                        }
                        continue;
                    }
                    if (type.IsAbstract || type.IsInterface || !typeof(IComposableWidget).IsAssignableFrom(type)) continue;
                    if (type.GetConstructor(Type.EmptyTypes) is null) continue;
                    try
                    {
                        var issues = registry.Register(() => (IComposableWidget)Activator.CreateInstance(type)!);
                        count++;
                        foreach (var w in issues.Where(i => i.Severity == ValidationSeverity.Warning))
                            warnings.Add($"{type.FullName}: {w.Message}");
                    }
                    catch (Exception ex)
                    {
                        // includes WidgetRegistrationException and constructors that throw
                        var reason = ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
                        errors.Add($"{type.FullName}: {reason}");
                    }
                }
            }
            catch (Exception ex)
            {
                var reason = ex is ReflectionTypeLoadException r
                    ? string.Join("; ", r.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct())
                    : ex.Message;
                errors.Add($"{Path.GetFileName(file)}: {reason}");
            }
        }
        return new LoadResult(count, errors, warnings);
    }
}
