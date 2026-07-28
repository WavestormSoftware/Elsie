using System.Reflection;

namespace Elsie;

/// <summary>
/// Configuration for Elsie module discovery and runtime behavior.
/// </summary>
public sealed class ElsieOptions
{
    /// <summary>
    /// Assemblies scanned for <see cref="ElsieModule"/> subclasses when AddElsie enables scanning.
    /// </summary>
    public IList<Assembly> AssembliesToScan { get; } = new List<Assembly>();

    /// <summary>
    /// When true (default), the entry assembly is included in module scanning.
    /// </summary>
    public bool ScanEntryAssembly { get; set; } = true;
}
