using KiloviewPcOnboarding;

internal static class LocalTransactionValidation
{
    internal static async Task RunAsync(string root)
    {
        var oldNdi = Environment.GetEnvironmentVariable("KILOVIEW_NDI_CONFIG_PATH");
        var oldDiscovery = Environment.GetEnvironmentVariable("KILOVIEW_NDI_DISCOVERY_UI_CONFIG_PATH");
        try
        {
            foreach (var scenario in new[] { "second-file-locked", "membership-failure", "created-files-failure", "cancelled", "recovery-incomplete", "success" })
            {
                var directory = Path.Combine(root, "local-transaction", scenario);
                Directory.CreateDirectory(directory);
                var ndi = Path.Combine(directory, "ndi.json");
                var discovery = Path.Combine(directory, "discovery.json");
                var agent = Path.Combine(directory, "agent.json");
                if (scenario != "created-files-failure")
                {
                    File.WriteAllText(ndi, """{"sentinel":"retain","ndi":{"groups":{"send":"Old job","recv":"Old job"}}}""");
                    File.WriteAllText(discovery, """{"sentinel":"retain-discovery"}""");
                    File.WriteAllText(agent, "old membership");
                }
                var originals = ConfigurationTransaction.CaptureFiles([ndi, discovery, agent]);
                Environment.SetEnvironmentVariable("KILOVIEW_NDI_CONFIG_PATH", ndi);
                Environment.SetEnvironmentVariable("KILOVIEW_NDI_DISCOVERY_UI_CONFIG_PATH", discovery);
                using var cancellation = new CancellationTokenSource();
                FileStream? locked = scenario == "second-file-locked"
                    ? new(discovery, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                var completed = false;
                Exception? failure = null;
                try
                {
                    await NdiConfigurationService.PreflightAsync(cancellation.Token);
                    await ServerOnboardingCommand.ApplyConfigurationAsync(
                        new("fixture", "Fixture", "Fixture", "192.0.2.20", 24),
                        new("192.0.2.30", new Uri("http://192.0.2.30:8091"), "fixture", "managed", "New job", "192.0.2.31", true),
                        agent, "Old job", () =>
                        {
                            completed = true;
                            File.WriteAllText(agent, "new membership");
                            if (scenario == "recovery-incomplete") locked = new(ndi, FileMode.Open, FileAccess.Read, FileShare.Read);
                            if (scenario is "membership-failure" or "created-files-failure" or "recovery-incomplete") throw new IOException("fixture membership failure");
                            if (scenario == "cancelled") cancellation.Cancel();
                            return Task.FromResult(true);
                        }, cancellation.Token);
                }
                catch (Exception ex) { failure = ex; }
                finally { locked?.Dispose(); }

                bool Restored(string path) => originals[path] is { } bytes
                    ? File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes) : !File.Exists(path);
                if (scenario == "success")
                {
                    Check(failure is null && completed && !Restored(ndi) && !Restored(discovery) && File.ReadAllText(agent) == "new membership",
                        "Successful local onboarding did not commit all configuration.");
                    Check(File.ReadAllText(ndi).Contains("retain") && File.ReadAllText(discovery).Contains("retain-discovery"), "Unrelated configuration was lost.");
                }
                else if (scenario == "recovery-incomplete")
                {
                    Check(failure is AggregateException && failure.Message.Contains("recovery was incomplete"), "Incomplete restoration was not reported.");
                    Check(!Restored(ndi) && Restored(discovery) && Restored(agent), "One failed restoration prevented independent recovery.");
                }
                else
                {
                    Check(failure is InvalidOperationException && failure.Message.Contains("was restored"), "Local failure did not report successful recovery.");
                    Check(originals.Keys.All(Restored), "Local failure changed NDI or membership state.");
                    if (scenario == "second-file-locked") Check(!completed, "Local completion ran after the failed NDI write.");
                }
                // The transaction must release its lease on every path.
                await using var reacquired = await NdiConfigurationService.AcquireConfigurationLockAsync(CancellationToken.None);
            }
            Console.WriteLine("LOCAL_NDI_MEMBERSHIP_TRANSACTION_RECOVERY=PASS (6 scenarios)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KILOVIEW_NDI_CONFIG_PATH", oldNdi);
            Environment.SetEnvironmentVariable("KILOVIEW_NDI_DISCOVERY_UI_CONFIG_PATH", oldDiscovery);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
