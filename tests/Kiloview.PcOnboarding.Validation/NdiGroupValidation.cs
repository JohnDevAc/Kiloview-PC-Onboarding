using KiloviewPcOnboarding;

internal static class NdiGroupValidation
{
    internal static async Task RunAsync(string testRoot)
    {
        var root = Path.Combine(testRoot, "ndi-group-names");
        Directory.CreateDirectory(root);
        var ndi = Path.Combine(root, "ndi.json");
        var discovery = Path.Combine(root, "discovery.json");
        var oldNdi = Environment.GetEnvironmentVariable("KILOVIEW_NDI_CONFIG_PATH");
        var oldDiscovery = Environment.GetEnvironmentVariable("KILOVIEW_NDI_DISCOVERY_UI_CONFIG_PATH");
        const string original = """{"ndi":{"groups":{"send":"Public","recv":"Public"}}}""";
        try
        {
            Environment.SetEnvironmentVariable("KILOVIEW_NDI_CONFIG_PATH", ndi);
            Environment.SetEnvironmentVariable("KILOVIEW_NDI_DISCOVERY_UI_CONFIG_PATH", discovery);
            foreach (var length in new[] { 241, 242 })
            {
                File.WriteAllText(ndi, original);
                File.WriteAllText(discovery, "{}");
                try
                {
                    await NdiConfigurationService.ApplyConfigurationFilesAsync(
                        new("fixture", "Fixture", "Fixture", "192.0.2.20", 24),
                        new("192.0.2.30", new Uri("http://192.0.2.30:8091"), "fixture", "managed", new string('x', length), "192.0.2.31", true),
                        CancellationToken.None);
                    if (length == 242) throw new Exception("Oversized combined groups were written.");
                    if (!File.ReadAllText(ndi).Contains("Public," + new string('x', length)))
                        throw new Exception("A long compatible NDI job name was not retained.");
                }
                catch (InvalidOperationException ex) when (length == 242 && ex.Message.Contains("248-byte"))
                {
                    if (File.ReadAllText(ndi) != original || File.ReadAllText(discovery) != "{}")
                        throw new Exception("Group overflow changed configuration before reporting the error.");
                }
            }
            Console.WriteLine("NDI_GROUP_LIST_LIMIT_BEFORE_WRITES=PASS");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KILOVIEW_NDI_CONFIG_PATH", oldNdi);
            Environment.SetEnvironmentVariable("KILOVIEW_NDI_DISCOVERY_UI_CONFIG_PATH", oldDiscovery);
        }
    }
}
