using System.Runtime.InteropServices;

namespace NovaManager;

internal sealed record WindowsUpdateCheckResult(IReadOnlyList<string> Titles);

internal static class WindowsUpdateService
{
    public static Task<WindowsUpdateCheckResult> CheckAvailableAsync() =>
        Task.Run(CheckAvailable);

    private static WindowsUpdateCheckResult CheckAvailable()
    {
        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new InvalidOperationException("The Windows Update service is not available on this PC.");
        object? session = null;
        object? searcher = null;
        object? result = null;
        object? updates = null;
        try
        {
            dynamic automation = session = Activator.CreateInstance(sessionType)
                ?? throw new InvalidOperationException("Windows Update could not create a search session.");
            dynamic updateSearcher = searcher = automation.CreateUpdateSearcher();
            dynamic searchResult = result = updateSearcher.Search("IsInstalled=0 and IsHidden=0");
            dynamic updateCollection = updates = searchResult.Updates;
            var titles = new List<string>();
            for (var index = 0; index < (int)updateCollection.Count; index++)
            {
                dynamic update = updateCollection.Item(index);
                titles.Add((string)update.Title);
                if (Marshal.IsComObject(update))
                {
                    Marshal.ReleaseComObject(update);
                }
            }

            return new WindowsUpdateCheckResult(titles);
        }
        finally
        {
            foreach (var item in new[] { updates, result, searcher, session })
            {
                if (item is not null && Marshal.IsComObject(item))
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
    }
}
