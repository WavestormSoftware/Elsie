// Empty assembly so the Elsie metapackage lists net8.0 / net10.0 on nuget.org.
// Runtime types live in Elsie.Web / Elsie.Core (package dependencies).

namespace Elsie;

/// <summary>Marker type for the <c>Elsie</c> metapackage assembly (no API surface).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class ElsiePackage
{
}
