using System.Text;

namespace NdiSuite.Configuration;

internal static class NdiGroupName
{
    // https://docs.ndi.video/all/getting-started/white-paper/discovery-and-registration/ndi-groups
    internal const int MaximumListBytes = 248;

    internal static bool IsValid(string? name) => !string.IsNullOrWhiteSpace(name)
        && !name.Contains(',')
        && !name.Any(char.IsControl)
        && Encoding.UTF8.GetByteCount(name) <= MaximumListBytes;

    internal static bool ListFits(string groups) => Encoding.UTF8.GetByteCount(groups) <= MaximumListBytes;
}
