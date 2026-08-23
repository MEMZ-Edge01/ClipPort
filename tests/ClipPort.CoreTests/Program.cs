using System.Collections.Concurrent;
using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ClipPort.Models;
using ClipPort.Services;
using Microsoft.Win32;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("localization resource coverage", TestLocalizationResourceCoverageAsync),
            ("localized string lookup", TestLocalizedStringLookupAsync),
            ("Windows accent preview stays independent", TestWindowsAccentPreviewAsync),
            ("application accent reaches WinUI control states", TestApplicationAccentControlResourcesAsync),
            ("copy and SHA-256 verification", TestCopyAndVerifyAsync),
            ("verification algorithms detect corruption", TestVerificationAlgorithmsAsync),
            ("opportunistic verification never blocks committed copies", TestOpportunisticVerificationAsync),
            ("verification-only mode never copies", TestVerificationOnlyAsync),
            ("verification mismatch can be overwritten", TestOverwriteVerificationMismatchAsync),
            ("FastCopy pipeline copy and verification", TestFastCopyAlgorithmAsync),
            ("packaged native engine availability", TestPackagedNativeEngineAvailabilityAsync),
            ("pause and resume", TestPauseAndResumeAsync),
            ("cancellation preserves existing destination", TestCancellationSafetyAsync),
            ("corruption is detected", TestCorruptionDetectionAsync),
            ("file failure continues and can retry", TestFileFailureRecoveryAsync),
            ("empty source completes", TestEmptySourceAsync),
            ("existing file can be skipped", TestSkipExistingAsync),
            ("existing file can create a copy", TestCreateCopyAsync),
            ("verification can be disabled", TestVerificationDisabledAsync),
            ("ask mode supports per-file decisions", TestAskPerFileDecisionsAsync),
            ("path safety and root-folder naming", TestPathSafetyAsync),
            ("shared display formatting", TestDisplayFormattingAsync),
            ("copy throughput waveform sampling", TestCopyThroughputSamplingAsync),
            ("source scanning yields the caller thread", TestSourceScanningYieldsCallerAsync),
            ("waveform timeline fills and compresses", TestWaveformTimelineAsync),
            ("wide layouts keep stretching", TestWideLayoutsKeepStretchingAsync),
            ("quick-start requests preserve the opposite directory", TestQuickStartRequestsAsync),
            ("traditional quick-start registration is certificate-free", TestLegacyExplorerContextMenuRegistrationAsync),
            ("traditional quick-start registry lifecycle is reversible", TestLegacyExplorerContextMenuRegistryLifecycleAsync),
            ("invalid settings enums recover safely", TestInvalidSettingsEnumsAsync),
            ("failed settings save prevents package uninstall", TestPackageUninstallSaveFailureAsync),
            ("package uninstall saves disabled state first", TestPackageUninstallSaveOrderAsync),
            ("package uninstall preserves enabled sibling state", TestPackageUninstallPreservesSiblingSettingAsync),
            ("package removal disables the live menu before deployment", TestPackageRemovalDisablesLiveStateAsync),
            ("package removal preserves sibling integration configuration", TestExplorerContextMenuConfigurationPolicyAsync),
            ("Explorer package identity stays publisher-scoped", TestExplorerPackageIdentityAsync),
            ("certificate store access failures remain visible", TestCertificateStoreSearchAsync),
            ("Explorer integration operations are serialized", TestExplorerIntegrationOperationGateAsync),
            ("certificate installer lifetime remains awaited", TestCertificateInstallerWorkflowAsync),
            ("task reports follow the selected language", TestLocalizedTaskReportAsync),
            ("local history persistence", TestHistoryPersistenceAsync),
            ("history isolates malformed records", TestHistoryMalformedRecordIsolationAsync),
            ("history retention protects active jobs", TestHistoryRetentionProtectsActiveJobsAsync),
            ("legacy failure reasons normalize", TestLegacyFailureReasonNormalizationAsync),
            ("retry results preserve warnings", TestRetryResultWarningsAsync),
            ("priority task status and badges stay visible", TestPriorityTaskPresentationAsync),
            ("priority tasks preempt ordinary tasks", TestPrioritySchedulerAsync),
            ("update version comparison follows semantic versioning", TestUpdateVersionComparisonAsync),
            ("update release assets match the portable zip pattern", TestUpdateReleaseAssetSelectionAsync)
        };

        foreach (var test in tests)
        {
            await test.Run();
            Console.WriteLine($"PASS: {test.Name}");
        }

        Console.WriteLine($"All {tests.Length} core tests passed.");
        return 0;
    }

    private static async Task TestPackageUninstallSaveFailureAsync()
    {
        var settings = new AppSettings
        {
            ExplorerContextMenuEnabled = true
        };
        int uninstallCalls = 0;

        ExplorerIntegrationUninstallResult<string> result =
            await ExplorerIntegrationUninstallWorkflow.RunAsync(
                settings,
                true,
                _ => Task.FromException(new IOException("simulated save failure")),
                () =>
                {
                    uninstallCalls++;
                    return Task.FromResult("removed");
                });

        Assert(result.SettingsSaveError is IOException,
            "The workflow should report the settings save error.");
        Assert(uninstallCalls == 0,
            "The package must not be removed before the disabled state is persisted.");
        Assert(settings.ExplorerContextMenuEnabled,
            "A failed save should restore the in-memory setting to its persisted value.");
    }

    private static async Task TestPackageUninstallSaveOrderAsync()
    {
        var settings = new AppSettings
        {
            ExplorerContextMenuEnabled = true
        };
        var calls = new List<string>();

        ExplorerIntegrationUninstallResult<string> result =
            await ExplorerIntegrationUninstallWorkflow.RunAsync(
                settings,
                true,
                currentSettings =>
                {
                    Assert(!currentSettings.ExplorerContextMenuEnabled,
                        "The disabled state must be saved before package removal.");
                    calls.Add("save");
                    return Task.CompletedTask;
                },
                () =>
                {
                    calls.Add("uninstall");
                    return Task.FromResult("removed");
                });

        Assert(result.SettingsSaveError is null && result.OperationResult == "removed",
            "A successful save should allow the package uninstall to complete.");
        Assert(calls.SequenceEqual(["save", "uninstall"]),
            "The workflow must persist the disabled state before uninstalling the package.");
    }

    private static async Task TestPackageUninstallPreservesSiblingSettingAsync()
    {
        var settings = new AppSettings
        {
            ExplorerContextMenuEnabled = true
        };
        int saveCalls = 0;

        ExplorerIntegrationUninstallResult<string> result =
            await ExplorerIntegrationUninstallWorkflow.RunAsync(
                settings,
                disableSavedSettingBeforeUninstall: false,
                _ =>
                {
                    saveCalls++;
                    return Task.CompletedTask;
                },
                () => Task.FromResult("removed"));

        Assert(result.SettingsSaveError is null && result.OperationResult == "removed",
            "A sibling-preserving uninstall should still remove the selected package.");
        Assert(saveCalls == 0 && settings.ExplorerContextMenuEnabled,
            "Removing one package must retain the shared enabled setting when a sibling remains.");
    }

    private static Task TestExplorerIntegrationOperationGateAsync()
    {
        var gate = new ExplorerIntegrationOperationGate();

        Assert(gate.TryBegin(out long startupOperationId),
            "Startup synchronization should acquire the integration operation gate.");
        Assert(gate.IsBusy && !gate.TryBegin(out _),
            "UI maintenance actions must remain blocked during startup synchronization.");
        Assert(!gate.CanUpdateSharedConfiguration,
            "Language-driven shared configuration updates must remain disabled while maintenance is active.");
        Assert(!gate.Complete(startupOperationId + 1),
            "A refresh without the owning operation token must not release the gate.");
        Assert(gate.IsBusy,
            "An unrelated status refresh must not release the active operation gate.");
        Assert(gate.Complete(startupOperationId),
            "The operation that acquired the gate should release it.");
        Assert(!gate.IsBusy && gate.TryBegin(out _),
            "A new operation should start after the owner applies its final status.");
        Assert(!gate.CanUpdateSharedConfiguration,
            "A newly acquired operation should immediately gate shared configuration updates.");

        return Task.CompletedTask;
    }

    private static async Task TestCertificateInstallerWorkflowAsync()
    {
        var installerExited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task waitTask = CertificateInstallerWorkflow.WaitForExitAsync(
            Process.GetCurrentProcess(),
            _ => installerExited.Task);

        Assert(!waitTask.IsCompleted,
            "Certificate maintenance must stay active until the installer exits.");
        installerExited.SetResult();
        await waitTask;
        Assert(waitTask.IsCompletedSuccessfully,
            "Certificate maintenance should complete after the installer exits.");
    }

    private static async Task TestPackageRemovalDisablesLiveStateAsync()
    {
        var calls = new List<string>();

        try
        {
            await ExplorerIntegrationPackageRemovalWorkflow.RunAsync(
                () => calls.Add("disable-live-state"),
                () =>
                {
                    calls.Add("remove-package");
                    return Task.FromException(new IOException("simulated deployment failure"));
                },
                () => calls.Add("clear-configuration"));
            throw new InvalidOperationException(
                "The simulated deployment failure should escape the removal workflow.");
        }
        catch (IOException)
        {
            // Expected: the live state remains disabled while configuration is
            // retained for a later retry.
        }

        Assert(calls.SequenceEqual(["disable-live-state", "remove-package"]),
            "A failed deployment must leave the live menu disabled and retain configuration.");

        calls.Clear();
        await ExplorerIntegrationPackageRemovalWorkflow.RunAsync(
            () => calls.Add("disable-live-state"),
            () =>
            {
                calls.Add("remove-package");
                return Task.CompletedTask;
            },
            () => calls.Add("clear-configuration"));
        Assert(calls.SequenceEqual(
                ["disable-live-state", "remove-package", "clear-configuration"]),
            "Configuration should be removed only after deployment succeeds.");
    }

    private static Task TestExplorerPackageIdentityAsync()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="MEMZEdge01.ClipPort.ShellIntegration"
                        Publisher="CN=ClipPort Development"
                        Version="1.0.0.0"
                        ProcessorArchitecture="x64" />
            </Package>
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest));
        ExplorerPackageIdentity identity = ExplorerPackageIdentity.ReadManifest(stream);

        using var malformedStream = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes("<Package><Identity"));
        try
        {
            ExplorerPackageIdentity.ReadManifest(malformedStream);
            throw new InvalidOperationException(
                "A malformed package manifest should not be accepted.");
        }
        catch (InvalidDataException ex)
        {
            Assert(ex.InnerException is XmlException,
                "Malformed manifest XML should be translated to InvalidDataException.");
        }

        Assert(identity.Matches(
                "MEMZEdge01.ClipPort.ShellIntegration",
                "CN=ClipPort Development"),
            "The bundled package identity should match its own name and publisher.");
        Assert(!identity.Matches(
                "MEMZEdge01.ClipPort.ShellIntegration",
                "CN=ClipPort Production"),
            "A same-name package from another publisher must never be removed.");
        Assert(!identity.Matches(
                "MEMZEdge01.AnotherPackage",
                "CN=ClipPort Development"),
            "A package with another identity name must never be removed.");
        Assert(!identity.MatchesAny(
                [
                    new ExplorerPackageRegistration(
                        "MEMZEdge01.ClipPort.ShellIntegration",
                        "CN=ClipPort Production",
                        "C:\\AnotherApp")
                ],
                "C:\\ClipPort"),
            "A same-name package from another publisher must not make this installation appear registered.");
        Assert(!identity.MatchesAny(
                [
                    new ExplorerPackageRegistration(
                        "MEMZEdge01.ClipPort.ShellIntegration",
                        "CN=ClipPort Development",
                        "C:\\AnotherApp")
                ],
                "C:\\ClipPort"),
            "A matching package identity from another application directory must not appear removable.");
        Assert(identity.MatchesAny(
                [
                    new ExplorerPackageRegistration(
                        "MEMZEdge01.ClipPort.ShellIntegration",
                        "CN=ClipPort Development",
                        "C:\\AnotherApp")
                ]),
            "Certificate removal must detect a matching package identity in another application directory.");
        Assert(identity.MatchesAny(
                [
                    new ExplorerPackageRegistration(
                        "MEMZEdge01.ClipPort.ShellIntegration",
                        "CN=ClipPort Development",
                        "C:\\ClipPort")
                ],
                "C:\\ClipPort\\"),
            "The selected package identity should recognize its matching registration.");

        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ClipPort.PackageIdentity.{Guid.NewGuid():N}");
        string looseManifestPath = Path.Combine(
            testDirectory,
            "ShellIntegration.Development",
            "AppxManifest.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(looseManifestPath)!);
        try
        {
            File.WriteAllText(looseManifestPath, manifest);
            string packagePath = Path.Combine(
                testDirectory,
                "ClipPort.ShellIntegration.msix");
            using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("AppxManifest.xml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(manifest.Replace(
                    "CN=ClipPort Development",
                    "CN=ClipPort Development, OID.2.25.311729368913984317654407730594956997722=1",
                    StringComparison.Ordinal));
            }

            ExplorerPackageIdentity looseIdentity = ExplorerPackageIdentity.Resolve(
                packagePath,
                looseManifestPath,
                Path.Combine(testDirectory, "missing.cer"),
                "MEMZEdge01.ClipPort.ShellIntegration");

            Assert(looseIdentity == identity,
                "A loose development manifest should take precedence over its retained unsigned MSIX.");

            File.Delete(looseManifestPath);
            string certificatePath = Path.Combine(testDirectory, "package.cer");
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=ClipPort Production",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(
                certificatePath,
                certificate.Export(X509ContentType.Cert));

            ExplorerPackageIdentity certificateIdentity = ExplorerPackageIdentity.Resolve(
                Path.Combine(testDirectory, "missing.msix"),
                looseManifestPath,
                certificatePath,
                "MEMZEdge01.ClipPort.ShellIntegration");
            Assert(certificateIdentity.Matches(
                    "MEMZEdge01.ClipPort.ShellIntegration",
                    certificate.Subject),
                "A remaining certificate should identify its signed package publisher when the MSIX is absent.");
            Assert(!certificateIdentity.Matches(
                    "MEMZEdge01.ClipPort.ShellIntegration",
                    "CN=ClipPort Development"),
                "A certificate identity must not match a same-name package from another publisher.");

            File.WriteAllText(looseManifestPath, manifest);
            ExplorerPackageIdentity preferredPackageIdentity =
                ExplorerPackageIdentity.Resolve(
                    Path.Combine(testDirectory, "missing.msix"),
                    looseManifestPath,
                    certificatePath,
                    "MEMZEdge01.ClipPort.ShellIntegration");
            ExplorerPackageIdentity certificateRemovalIdentity =
                ExplorerPackageIdentity.FromCertificate(
                    "MEMZEdge01.ClipPort.ShellIntegration",
                    certificate);
            Assert(preferredPackageIdentity == identity,
                "Package maintenance should continue to prefer the loose development registration.");
            Assert(certificateRemovalIdentity == certificateIdentity,
                "Certificate removal must use the certificate publisher even when a loose development manifest remains.");
            File.Delete(looseManifestPath);

            string malformedCertificatePath = Path.Combine(
                testDirectory,
                "malformed.cer");
            File.WriteAllBytes(malformedCertificatePath, [0x01, 0x02, 0x03]);
            try
            {
                ExplorerPackageIdentity.Resolve(
                    Path.Combine(testDirectory, "missing.msix"),
                    looseManifestPath,
                    malformedCertificatePath,
                    "MEMZEdge01.ClipPort.ShellIntegration");
                throw new InvalidOperationException(
                    "Malformed certificate data should not be accepted as a package identity.");
            }
            catch (CryptographicException)
            {
                // Synchronization translates this recoverable package-data failure
                // into an Explorer integration status instead of escaping startup.
            }

            ExplorerPackageIdentity? registeredIdentity =
                ExplorerPackageIdentity.FindRegisteredForExternalPath(
                    [
                        new ExplorerPackageRegistration(
                            "MEMZEdge01.ClipPort.ShellIntegration",
                            "CN=ClipPort Production",
                            Path.Combine(testDirectory, "another-app")),
                        new ExplorerPackageRegistration(
                            "MEMZEdge01.ClipPort.ShellIntegration",
                            "CN=ClipPort Development",
                            testDirectory)
                    ],
                    "MEMZEdge01.ClipPort.ShellIntegration",
                    testDirectory + Path.DirectorySeparatorChar);
            Assert(registeredIdentity == identity,
                "The registered package bound to this application directory should remain identifiable after all package files are removed.");

            ExplorerPackageIdentity? unrelatedIdentity =
                ExplorerPackageIdentity.FindRegisteredForExternalPath(
                    [
                        new ExplorerPackageRegistration(
                            "MEMZEdge01.ClipPort.ShellIntegration",
                            "CN=ClipPort Production",
                            Path.Combine(testDirectory, "another-app"))
                    ],
                    "MEMZEdge01.ClipPort.ShellIntegration",
                    testDirectory);
            Assert(unrelatedIdentity is null,
                "A same-name package bound to another application directory must not block certificate removal.");

            Assert(!ExplorerPackageIdentity.ExternalPathsEqual(
                    "C:\\ClipPort\\bad<path",
                    "C:\\ClipPort"),
                "A malformed stored path must compare safely instead of crashing maintenance.");
            Assert(ExplorerPackageIdentity.ExternalPathsEqual(
                    "C:\\ClipPort\\",
                    "C:\\ClipPort"),
                "Normalized equal paths should still compare as equal.");
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task TestExplorerContextMenuConfigurationPolicyAsync()
    {
        const string packageName = "MEMZEdge01.ClipPort.ShellIntegration";
        var configuration = new ExplorerContextMenuConfiguration(
            true,
            "en-US",
            "C:\\ClipPort");
        var siblingRegistrations = new[]
        {
            new ExplorerPackageRegistration(
                packageName,
                "CN=ClipPort Production",
                "D:\\ClipPort"),
            new ExplorerPackageRegistration(
                packageName,
                "CN=ClipPort Development",
                "E:\\ClipPort")
        };
        ExplorerPackageRegistration[] registrationsWithCurrent =
        [
            .. siblingRegistrations,
            new ExplorerPackageRegistration(
                packageName,
                "CN=ClipPort Production",
                "C:\\ClipPort")
        ];

        Assert(ExplorerContextMenuConfigurationPolicy.HasSiblingRegistration(
                siblingRegistrations,
                packageName,
                "C:\\ClipPort"),
            "A same-name registration in another directory should preserve the shared enabled setting.");
        Assert(!ExplorerContextMenuConfigurationPolicy.HasSiblingRegistration(
                [
                    new ExplorerPackageRegistration(
                        packageName,
                        "CN=ClipPort Production",
                        "C:\\ClipPort\\")
                ],
                packageName,
                "C:\\ClipPort"),
            "The registration being removed must not count as its own sibling.");

        Assert(ExplorerContextMenuConfigurationPolicy.ShouldDisableBeforeRemoval(
                configuration,
                siblingRegistrations,
                packageName,
                "C:\\ClipPort\\"),
            "Removing the configured package should disable its live menu before deployment.");
        var activeSiblingConfiguration = configuration with
        {
            InstallDirectory = "D:\\ClipPort"
        };
        Assert(!ExplorerContextMenuConfigurationPolicy.ShouldDisableBeforeRemoval(
                activeSiblingConfiguration,
                siblingRegistrations,
                packageName,
                "C:\\ClipPort"),
            "Removing an inactive sibling package must not disable the configured live menu.");
        Assert(ExplorerContextMenuConfigurationPolicy
                .ShouldDeferSynchronizationToConfigurationOwner(
                    activeSiblingConfiguration,
                    siblingRegistrations,
                    packageName,
                    "C:\\ClipPort"),
            "Startup must not overwrite a shared configuration owned by another registered copy.");
        Assert(!ExplorerContextMenuConfigurationPolicy
                .ShouldDeferSynchronizationToConfigurationOwner(
                    configuration,
                    registrationsWithCurrent,
                    packageName,
                    "C:\\ClipPort\\"),
            "The configured owner should continue synchronizing its own registration.");
        Assert(!ExplorerContextMenuConfigurationPolicy
                .ShouldDeferSynchronizationToConfigurationOwner(
                    configuration with { InstallDirectory = "Z:\\StaleClipPort" },
                    registrationsWithCurrent,
                    packageName,
                    "C:\\ClipPort"),
            "A stale configuration owner must not block a valid copy from repairing shared state.");
        Assert(ExplorerContextMenuConfigurationPolicy
                .ShouldDeferSynchronizationToConfigurationOwner(
                    null,
                    siblingRegistrations,
                    packageName,
                    "C:\\ClipPort"),
            "An unregistered copy must not reinstall itself while a sibling registration remains.");
        Assert(!ExplorerContextMenuConfigurationPolicy
                .ShouldDeferSynchronizationToConfigurationOwner(
                    null,
                    [
                        new ExplorerPackageRegistration(
                            packageName,
                            "CN=ClipPort Production",
                            "C:\\ClipPort")
                    ],
                    packageName,
                    "C:\\ClipPort"),
            "A registered copy should repair missing shared configuration during startup.");
        Assert(ExplorerContextMenuConfigurationPolicy.ShouldDisableBeforeRemoval(
                configuration with { InstallDirectory = "Z:\\StaleClipPort" },
                [],
                packageName,
                "C:\\ClipPort"),
            "A stale shared configuration should still be disabled before the last package is removed.");
        Assert(ExplorerContextMenuConfigurationPolicy.ShouldDisableBeforeRemoval(
                configuration with { InstallDirectory = "C:\\bad<path" },
                siblingRegistrations,
                packageName,
                "C:\\ClipPort"),
            "A malformed shared configuration should still disable the live menu before removal.");

        ExplorerContextMenuConfiguration? reconciled =
            ExplorerContextMenuConfigurationPolicy.ReconcileAfterRemoval(
                configuration,
                siblingRegistrations,
                packageName);
        Assert(reconciled == configuration with { InstallDirectory = "D:\\ClipPort" },
            "After removing the configured package, shared state should move to a remaining registration without changing its enabled state or language.");
        reconciled = ExplorerContextMenuConfigurationPolicy.ReconcileAfterRemoval(
            configuration with { InstallDirectory = "C:\\bad<path" },
            siblingRegistrations,
            packageName);
        Assert(reconciled == configuration with { InstallDirectory = "D:\\ClipPort" },
            "A malformed stored path should fall back to a remaining registration instead of crashing.");

        var siblingConfiguration = configuration with
        {
            InstallDirectory = "E:\\ClipPort\\"
        };
        reconciled = ExplorerContextMenuConfigurationPolicy.ReconcileAfterRemoval(
            siblingConfiguration,
            siblingRegistrations,
            packageName);
        Assert(reconciled == configuration with { InstallDirectory = "E:\\ClipPort" },
            "An already configured sibling registration should remain the shared configuration owner.");

        Assert(ExplorerContextMenuConfigurationPolicy.ReconcileAfterRemoval(
                configuration,
                [],
                packageName) is null,
            "Shared configuration should be removed only when no package registration remains.");

        return Task.CompletedTask;
    }

    private static Task TestCertificateStoreSearchAsync()
    {
        string[] targets = ["CurrentUser/Root", "LocalMachine/Root"];
        List<string> matches = CertificateStoreSearch.FindMatches(
            targets,
            target => target == "CurrentUser/Root");

        Assert(matches.SequenceEqual(["CurrentUser/Root"]),
            "Certificate store search should return only matching stores.");

        try
        {
            CertificateStoreSearch.FindMatches(
                targets,
                _ => throw new CryptographicException("store unavailable"));
            throw new InvalidOperationException(
                "Certificate store access failures must not be treated as an empty store.");
        }
        catch (CryptographicException ex)
        {
            Assert(ex.Message == "store unavailable",
                "Certificate store access failures should propagate to the operation status.");
        }

        return Task.CompletedTask;
    }

    private static Task TestLocalizationResourceCoverageAsync()
    {
        string localizationDirectory = Path.Combine(AppContext.BaseDirectory, "Localization");
        string stringsDirectory = Path.Combine(AppContext.BaseDirectory, "Strings");
        Dictionary<AppLanguage, Dictionary<string, string>> resourcesByLanguage =
            AppLanguages.Supported.ToDictionary(
                definition => definition.Language,
                definition => LoadStringResources(Path.Combine(
                    stringsDirectory,
                    definition.LanguageTag,
                    "Resources.resw")));
        Dictionary<string, string> chinese =
            resourcesByLanguage[AppLanguage.SimplifiedChinese];

        Assert(
            AppLanguages.Supported.Select(definition => definition.Language).Distinct().Count() ==
            AppLanguages.Supported.Count &&
            AppLanguages.Supported.Select(definition => definition.LanguageTag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == AppLanguages.Supported.Count,
            "Every supported language should have a unique enum value and language tag.");

        foreach (AppLanguageDefinition language in AppLanguages.Supported)
        {
            Dictionary<string, string> localized = resourcesByLanguage[language.Language];
            Assert(chinese.Keys.ToHashSet().SetEquals(localized.Keys),
                $"Language '{language.LanguageTag}' should contain every resource key.");

            foreach (string key in chinese.Keys)
            {
                string[] chinesePlaceholders = ExtractFormatPlaceholders(chinese[key]);
                string[] localizedPlaceholders = ExtractFormatPlaceholders(localized[key]);
                Assert(chinesePlaceholders.SequenceEqual(localizedPlaceholders),
                    $"Resource '{key}' should use the same format placeholders in " +
                    $"language '{language.LanguageTag}'.");
            }
        }

        string[] xamlFiles =
        [
            Path.Combine(localizationDirectory, "MainWindow.xaml"),
            Path.Combine(localizationDirectory, "SettingsView.xaml")
        ];
        string[] localizedProperties =
        [
            "Text",
            "Content",
            "Header",
            "OnContent",
            "OffContent",
            "Title",
            "PrimaryButtonText",
            "SecondaryButtonText",
            "CloseButtonText",
            "PlaceholderText",
            "ToolTipService.ToolTip"
        ];

        foreach (string xamlFile in xamlFiles)
        {
            XDocument document = XDocument.Load(xamlFile, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                // SettingsView populates ComboBoxItem labels from ResourceService because
                // WinUI does not apply x:Uid resources reliably to collection items.
                if (element.Name.LocalName == "ComboBoxItem")
                {
                    continue;
                }

                string? uid = element.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "Uid")
                    ?.Value;
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (!localizedProperties.Contains(attribute.Name.LocalName) ||
                        !ContainsChinese(attribute.Value))
                    {
                        continue;
                    }

                    IXmlLineInfo lineInfo = (IXmlLineInfo)element;
                    string location = $"{Path.GetFileName(xamlFile)}:{lineInfo.LineNumber}";
                    Assert(!string.IsNullOrWhiteSpace(uid),
                        $"{location} contains Chinese UI text without x:Uid.");

                    string resourceKey = $"{uid}.{attribute.Name.LocalName}";
                    Assert(resourcesByLanguage.Values.All(resources =>
                            resources.ContainsKey(resourceKey)),
                        $"{location} is missing resource key '{resourceKey}'.");
                }
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestLocalizedStringLookupAsync()
    {
        ResourceService.SetLanguage(AppLanguage.SimplifiedChinese);
        Assert(ResourceService.GetString("NewJobButtonText.Text") == "创建任务",
            "Simplified Chinese resource lookup should return the localized value.");
        Assert(ResourceService.GetString("Button.RestartLater") == "稍后重启" &&
               ResourceService.GetString("Button.RestartNow") == "现在重启",
            "Simplified Chinese should provide both language restart actions.");

        ResourceService.SetLanguage(AppLanguage.English);
        Assert(ResourceService.GetString("NewJobButtonText.Text") == "Create task",
            "English resource lookup should return the localized value.");
        Assert(ResourceService.GetString("Button.RestartLater") == "Restart later" &&
               ResourceService.GetString("Button.RestartNow") == "Restart now",
            "English should provide both language restart actions.");
        Assert(ResourceService.GetString("创建任务") == "Create task",
            "Legacy persisted Chinese values should resolve through their resource key.");

        ResourceService.SetLanguage(AppLanguage.ClassicalChinese);
        Assert(ResourceService.GetString("NewJobButtonText.Text") == "立新役",
            "Classical Chinese resource lookup should return the translated value.");
        Assert(ResourceService.GetString("创建任务") == "立新役",
            "Legacy persisted Chinese values should resolve to Classical Chinese.");
        Assert(ResourceService.GetString("Settings.ClassicalChinese") == "文言文",
            "The Classical Chinese language should have a visible selector label.");
        Assert(ResourceService.GetString("Button.RestartLater") == "后复启" &&
               ResourceService.GetString("Button.RestartNow") == "今复启",
            "Classical Chinese should provide both language restart actions.");
        Assert(ResourceService.GetString("Missing.Resource.Key") == "Missing.Resource.Key",
            "Missing resources should fall back to the key.");

        ResourceService.SetLanguage(AppLanguage.SimplifiedChinese);
        return Task.CompletedTask;
    }

    private static Task TestWindowsAccentPreviewAsync()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument settingsView = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Localization", "SettingsView.xaml"));
        XElement systemAccentButton = settingsView
            .Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute("Tag") == "System");
        XElement previewEllipse = systemAccentButton
            .Descendants(presentation + "Ellipse")
            .Single();

        Assert((string?)previewEllipse.Attribute("Fill") ==
               "{StaticResource WindowsAccentPreviewBrush}",
            "The Windows accent preview must not use the mutable application AccentBrush.");

        XDocument theme = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Themes", "TraeWorkTheme.xaml"));
        XElement previewBrush = theme
            .Descendants(presentation + "SolidColorBrush")
            .Single(element =>
                (string?)element.Attribute(x + "Key") == "WindowsAccentPreviewBrush");
        Assert((string?)previewBrush.Attribute("Color") ==
               "{ThemeResource SystemAccentColor}",
            "The Windows accent preview brush must read the current Windows accent color.");

        return Task.CompletedTask;
    }

    private static Task TestApplicationAccentControlResourcesAsync()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument theme = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Themes", "TraeWorkTheme.xaml"));

        var expectedAliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccentButtonBackground"] = "AccentBrush",
            ["AccentButtonBackgroundPointerOver"] = "AccentHoverBrush",
            ["AccentButtonBackgroundPressed"] = "AccentActiveBrush",
            ["RadioButtonOuterEllipseCheckedFill"] = "AccentBrush",
            ["RadioButtonOuterEllipseCheckedFillPointerOver"] = "AccentHoverBrush",
            ["ToggleSwitchFillOn"] = "AccentBrush",
            ["ToggleSwitchFillOnPointerOver"] = "AccentHoverBrush",
            ["CheckBoxCheckBackgroundFillChecked"] = "AccentBrush",
            ["ComboBoxItemPillFillBrush"] = "AccentBrush",
            ["ProgressBarForeground"] = "AccentBrush",
            ["InfoBarInformationalSeverityBackgroundBrush"] = "AccentSoftBrush",
            ["InfoBarInformationalSeverityIconBackground"] = "AccentBrush"
        };

        foreach ((string resourceKey, string expectedBrushKey) in expectedAliases)
        {
            XElement alias = theme
                .Descendants(presentation + "StaticResource")
                .Single(element => (string?)element.Attribute(x + "Key") == resourceKey);
            Assert((string?)alias.Attribute("ResourceKey") == expectedBrushKey,
                $"{resourceKey} must follow the mutable application {expectedBrushKey}.");
        }

        XDocument mainWindow = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Localization", "MainWindow.xaml"));
        foreach (string buttonName in new[]
                 {
                     "NewJobButton",
                     "StartButton",
                     "RestartJobButton",
                     "ApplyDuplicateChoicesButton",
                     "RetryFailedFilesButton"
                 })
        {
            XElement button = mainWindow
                .Descendants(presentation + "Button")
                .Single(element => (string?)element.Attribute(x + "Name") == buttonName);
            Assert((string?)button.Attribute("Style") == "{StaticResource AccentButtonStyle}",
                $"{buttonName} must use the accent button visual states, not the default Windows hover states.");
        }

        return Task.CompletedTask;
    }

    private static Dictionary<string, string> LoadStringResources(string path) =>
        XDocument.Load(path)
            .Descendants("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty);

    private static string[] ExtractFormatPlaceholders(string value) =>
        Regex.Matches(value, @"\{\d+(?:[^}]*)?\}")
            .Select(match => Regex.Replace(match.Value, @"[:,].*", "}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool ContainsChinese(string value) =>
        value.Any(character => character is >= '\u4E00' and <= '\u9FFF');

    private static async Task TestCopyAndVerifyAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            Directory.CreateDirectory(Path.Combine(source, "DCIM", "100MEDIA"));
            Directory.CreateDirectory(Path.Combine(source, "EMPTY_FOLDER"));
            await File.WriteAllTextAsync(Path.Combine(source, "notes.txt"), "ClipPort test - 你好");
            await File.WriteAllBytesAsync(Path.Combine(source, "empty.bin"), []);
            byte[] media = RandomNumberGenerator.GetBytes(6 * 1024 * 1024 + 137);
            string sourceMedia = Path.Combine(source, "DCIM", "100MEDIA", "clip.bin");
            await File.WriteAllBytesAsync(sourceMedia, media);
            DateTime expectedWriteTime = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(sourceMedia, expectedWriteTime);

            var events = new List<CopyProgressInfo>();
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask, CancellationToken.None);

            Assert(result.Success, "A normal copy should pass verification.");
            Assert(result.FileCount == 3, "All source files should be counted.");
            Assert(events.Any(item => item.Phase == CopyPhase.Copying && item.ProcessedFiles == 3),
                "The final copied-file count must be reported.");
            Assert(events.Any(item => item.Phase == CopyPhase.Verifying), "Verification progress is missing.");
            Assert(events.Last().Phase == CopyPhase.Completed, "The final phase should be Completed.");

            foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, sourceFile);
                string destinationFile = Path.Combine(destination, relative);
                Assert(File.Exists(destinationFile), $"Missing destination file: {relative}");
                Assert(await HashAsync(sourceFile) == await HashAsync(destinationFile),
                    $"Hash mismatch for {relative}");
            }

            Assert(Directory.Exists(Path.Combine(destination, "EMPTY_FOLDER")),
                "Empty source directories should be preserved.");
            TimeSpan writeTimeDifference = File.GetLastWriteTimeUtc(Path.Combine(destination, "DCIM", "100MEDIA", "clip.bin")) - expectedWriteTime;
            Assert(Math.Abs(writeTimeDifference.TotalSeconds) < 2, "Last-write time should be preserved.");
            Assert(!Directory.EnumerateFiles(destination, "*.clipport-partial", SearchOption.AllDirectories).Any(),
                "No partial files should remain after success.");
        });
    }

    private static async Task TestFastCopyAlgorithmAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            Directory.CreateDirectory(Path.Combine(source, "DCIM"));
            await File.WriteAllBytesAsync(
                Path.Combine(source, "DCIM", "large-clip.bin"),
                RandomNumberGenerator.GetBytes(18 * 1024 * 1024 + 137));
            await File.WriteAllTextAsync(Path.Combine(source, "metadata.txt"), "FastCopy pipeline");

            var events = new List<CopyProgressInfo>();
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination,
                new CopyOptions(ExistingFilePolicy.Overwrite, true, true),
                new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask, CancellationToken.None);

            Assert(result.Success && result.VerificationPerformed,
                "The FastCopy pipeline should copy and verify successfully.");
            Assert(result.FileCount == 2 && result.VerifiedFiles.Count == 2,
                "Every file should be copied and verified by the FastCopy pipeline.");
            Assert(events.Last().Phase == CopyPhase.Completed,
                "The FastCopy pipeline should report completion.");
            foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, sourceFile);
                string destinationFile = Path.Combine(destination, relative);
                Assert(await HashAsync(sourceFile) == await HashAsync(destinationFile),
                    $"FastCopy pipeline hash mismatch for {relative}");
            }
            Assert(!Directory.EnumerateFiles(destination, "*.clipport-partial", SearchOption.AllDirectories).Any(),
                "The FastCopy pipeline must not leave partial files after success.");
        });
    }

    private static async Task TestVerificationAlgorithmsAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            byte[] content = RandomNumberGenerator.GetBytes(512 * 1024 + 19);
            await File.WriteAllBytesAsync(Path.Combine(source, "manifest.bin"), content);

            foreach (VerificationAlgorithmKind algorithm in Enum.GetValues<VerificationAlgorithmKind>())
            {
                string algorithmDestination = Path.Combine(destination, algorithm.ToString());
                var options = new CopyOptions(
                    ExistingFilePolicy: ExistingFilePolicy.Overwrite,
                    VerifyFiles: true,
                    UseFastCopyAlgorithm: false,
                    SkipCopy: false,
                    VerificationAlgorithm: algorithm);
                CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                    source,
                    algorithmDestination,
                    options,
                    new InlineProgress<CopyProgressInfo>(_ => { }),
                    _ => Task.CompletedTask,
                    CancellationToken.None);

                Assert(result.Success && result.VerificationPerformed &&
                       result.VerificationAlgorithm == algorithm,
                    $"{VerificationAlgorithms.GetDisplayName(algorithm)} should verify a copied file.");
                Assert(result.VerifiedFiles.Count == 1 &&
                       result.VerifiedFiles[0].SourceHash == result.VerifiedFiles[0].DestinationHash &&
                       !string.IsNullOrEmpty(result.VerifiedFiles[0].SourceHash),
                    $"{VerificationAlgorithms.GetDisplayName(algorithm)} should record matching digests.");

                await File.WriteAllTextAsync(
                    Path.Combine(algorithmDestination, "manifest.bin"),
                    "corrupted payload");
                CopyResult reverification = await new FileCopyService().CopyAndVerifyAsync(
                    source,
                    algorithmDestination,
                    options with { SkipCopy = true },
                    new InlineProgress<CopyProgressInfo>(_ => { }),
                    _ => Task.CompletedTask,
                    CancellationToken.None);

                Assert(!reverification.Success &&
                       reverification.FailedFiles.Single().IsVerificationMismatch,
                    $"{VerificationAlgorithms.GetDisplayName(algorithm)} should detect a changed destination file.");
            }
        });
    }

    private static Task TestPackagedNativeEngineAvailabilityAsync()
    {
        string nativeLibraryPath = Path.Combine(
            AppContext.BaseDirectory,
            "ClipPort.NativeCopy.dll");
        if (File.Exists(nativeLibraryPath))
        {
            Assert(NativeCopyEngine.IsAvailable,
                "A packaged native engine with the expected API version should be available.");
        }

        return Task.CompletedTask;
    }

    private static async Task TestOpportunisticVerificationAsync()
    {
        Assert(new CopyOptions().VerificationExecutionMode == VerificationExecutionMode.AfterCopy,
            "Existing callers must retain verification-after-copy by default.");

        await WithTempFoldersAsync(async (source, destination) =>
        {
            const int fileCount = 12;
            for (int index = 0; index < fileCount; index++)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(source, $"item-{index:D2}.bin"),
                    RandomNumberGenerator.GetBytes(512 * 1024 + index));
            }

            var events = new ConcurrentQueue<CopyProgressInfo>();
            int verificationBeforeCommit = 0;
            var progress = new InlineProgress<CopyProgressInfo>(info =>
            {
                events.Enqueue(info);
                if (info.Phase == CopyPhase.Verifying &&
                    !string.IsNullOrWhiteSpace(info.CurrentFile) &&
                    !File.Exists(Path.Combine(destination, info.CurrentFile)))
                {
                    Interlocked.Exchange(ref verificationBeforeCommit, 1);
                }
            });
            var options = new CopyOptions(
                ExistingFilePolicy.Overwrite,
                VerifyFiles: true,
                UseFastCopyAlgorithm: false,
                VerificationExecutionMode: VerificationExecutionMode.OpportunisticDuringCopy);

            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source,
                destination,
                options,
                progress,
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(result.Success && result.VerifiedFileCount == fileCount &&
                   result.VerifiedFiles.Count == fileCount,
                "Background verification and its non-blocking backlog must verify every file.");
            Assert(events.Any(info => info.Phase == CopyPhase.Verifying),
                "Opportunistic mode must publish independent verification progress.");
            Assert(events.Any(info =>
                    info.Phase == CopyPhase.Verifying &&
                    !info.IsPhaseActive &&
                    info.BytesPerSecond == 0 &&
                    string.IsNullOrEmpty(info.CurrentFile)),
                "Background verification must publish a zero-speed idle state instead of leaving the last rate visible.");
            Assert(verificationBeforeCommit == 0,
                "A file must be atomically committed before background verification starts.");
            Assert(!Directory.EnumerateFiles(destination, "*.clipport-partial", SearchOption.AllDirectories).Any(),
                "Opportunistic verification must never observe or leave partial files.");
        });
    }

    private static async Task TestVerificationOnlyAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "same.txt"), "same");
            await File.WriteAllTextAsync(Path.Combine(destination, "same.txt"), "same");
            await File.WriteAllTextAsync(Path.Combine(source, "different.txt"), "new source content");
            await File.WriteAllTextAsync(Path.Combine(destination, "different.txt"), "keep destination content");
            await File.WriteAllTextAsync(Path.Combine(source, "missing.txt"), "must not be copied");
            Directory.CreateDirectory(Path.Combine(source, "empty-source-folder"));

            var events = new List<CopyProgressInfo>();
            string outsideDestination = Path.Combine(
                Directory.GetParent(destination)!.FullName,
                "outside-target.txt");
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source,
                destination,
                new CopyOptions(
                    ExistingFilePolicy: ExistingFilePolicy.Overwrite,
                    VerifyFiles: true,
                    SkipCopy: true)
                {
                    DestinationPaths = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["same.txt"] = outsideDestination
                    }
                },
                new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(!result.Success, "Mismatched and missing destination files must fail verification.");
            Assert(result.VerificationPerformed, "Verification-only mode must always perform verification.");
            Assert(!events.Any(item => item.Phase == CopyPhase.Copying),
                "Verification-only mode must never enter the copying phase.");
            Assert(events.Any(item => item.Phase == CopyPhase.Verifying),
                "Verification-only mode must report verification progress.");
            Assert(await File.ReadAllTextAsync(Path.Combine(destination, "different.txt")) == "keep destination content",
                "Verification-only mode must not overwrite destination files.");
            Assert(!File.Exists(Path.Combine(destination, "missing.txt")),
                "Verification-only mode must not copy missing destination files.");
            Assert(!Directory.Exists(Path.Combine(destination, "empty-source-folder")),
                "Verification-only mode must not create destination directories.");
            Assert(result.FailedFiles.All(item => item.Stage == FileOperationStage.Verifying),
                "Verification-only failures must be reported as verification failures.");
            Assert(result.FailedFiles.Any(item => item.RelativePath == "same.txt"),
                "A persisted destination mapping must not escape the recorded destination root.");
        });
    }

    private static async Task TestOverwriteVerificationMismatchAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "mismatch.txt");
            string destinationFile = Path.Combine(destination, "mismatch.txt");
            await File.WriteAllTextAsync(sourceFile, "authoritative source");
            await File.WriteAllTextAsync(destinationFile, "stale destination");

            var service = new FileCopyService();
            CopyOptions options = new(
                ExistingFilePolicy.Overwrite,
                VerifyFiles: true,
                SkipCopy: true,
                VerificationAlgorithm: VerificationAlgorithmKind.Md5);
            CopyResult result = await service.CopyAndVerifyAsync(
                source,
                destination,
                options,
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(result.FailedFiles.Count == 1 &&
                   result.FailedFiles[0].IsVerificationMismatch &&
                   result.VerificationAlgorithm == VerificationAlgorithmKind.Md5,
                "A hash mismatch should be eligible for overwrite.");

            FileRetryResult retry = await service.RetryFailedFilesAsync(
                result.FailedFiles,
                options,
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask,
                CancellationToken.None);
            Assert(retry.FailedFiles.Count == 1,
                "Retrying a mismatch without overwrite should only reverify it.");
            Assert(await File.ReadAllTextAsync(destinationFile) == "stale destination",
                "The ordinary retry action must not overwrite a verification mismatch.");

            var overwriteEvents = new List<CopyProgressInfo>();
            FileRetryResult overwrite = await service.OverwriteVerificationMismatchesAsync(
                result.FailedFiles,
                options,
                new InlineProgress<CopyProgressInfo>(overwriteEvents.Add),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(overwrite.FailedFiles.Count == 0,
                "Overwrite should clear a verification mismatch after copying the source file.");
            Assert(overwrite.CopiedFiles == 1 && overwrite.CopiedBytes > 0,
                "Overwrite should report one genuinely copied file.");
            Assert(overwrite.VerificationResults.Single().SourceHash.Length == 32,
                "Failure retries should keep the task's selected MD5 algorithm.");
            Assert(await File.ReadAllTextAsync(destinationFile) == "authoritative source",
                "Overwrite should replace the destination with the source file.");
            Assert(overwriteEvents.Any(item => item.Phase == CopyPhase.Copying),
                "Overwrite should report copy progress even for a verification-only task.");
        });
    }

    private static async Task TestPauseAndResumeAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllBytesAsync(Path.Combine(source, "paused.bin"), RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
            var enteredPause = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;

            async Task PauseOnSecondCheckpoint(CancellationToken token)
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    enteredPause.TrySetResult(true);
                    await resume.Task.WaitAsync(token);
                }
            }

            Task<CopyResult> task = new FileCopyService().CopyAndVerifyAsync(
                source, destination, new InlineProgress<CopyProgressInfo>(_ => { }),
                PauseOnSecondCheckpoint, CancellationToken.None);

            await enteredPause.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert(!task.IsCompleted, "The copy must remain blocked while paused.");
            resume.TrySetResult(true);
            CopyResult result = await task;
            Assert(result.Success, "The copy should finish after resume.");
        });
    }

    private static async Task TestCancellationSafetyAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "video.bin");
            string destinationFile = Path.Combine(destination, "video.bin");
            await File.WriteAllBytesAsync(sourceFile, RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
            await File.WriteAllTextAsync(destinationFile, "keep-existing-file");

            var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            async Task PauseUntilCancelled(CancellationToken token)
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    paused.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }

            using var cancellation = new CancellationTokenSource();
            Task<CopyResult> task = new FileCopyService().CopyAndVerifyAsync(
                source, destination,
                new CopyOptions(ExistingFilePolicy.Overwrite, true, true),
                new InlineProgress<CopyProgressInfo>(_ => { }),
                PauseUntilCancelled, cancellation.Token);
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            bool cancelled = false;
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert(cancelled, "Cancellation should propagate to the caller.");
            Assert(await File.ReadAllTextAsync(destinationFile) == "keep-existing-file",
                "Cancellation must not overwrite an existing completed file.");
            Assert(!Directory.EnumerateFiles(
                    destination,
                    "*.clipport-partial",
                    SearchOption.AllDirectories).Any(),
                "The partial file must be cleaned up after cancellation.");
        });
    }

    private static async Task TestCorruptionDetectionAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "manifest.txt"), "original data");
            bool corrupted = false;
            var progress = new InlineProgress<CopyProgressInfo>(info =>
            {
                if (!corrupted && info.Phase == CopyPhase.Copying && info.ProcessedFiles == info.TotalFiles)
                {
                    File.AppendAllText(Path.Combine(destination, "manifest.txt"), "tampered");
                    corrupted = true;
                }
            });

            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, progress, _ => Task.CompletedTask, CancellationToken.None);

            Assert(corrupted, "The test must alter the copied file before verification.");
            Assert(!result.Success, "A corrupted destination must fail verification.");
            Assert(result.Errors.Count == 1, "The mismatched file must be listed once.");
            Assert(result.VerifiedFileCount == 0,
                "A mismatched file must not be counted as successfully verified.");
        });
    }

    private static async Task TestEmptySourceAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            var events = new List<CopyProgressInfo>();
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask, CancellationToken.None);
            Assert(result.Success && result.FileCount == 0 && result.TotalBytes == 0,
                "An empty card should complete successfully.");
            Assert(events.Last().Phase == CopyPhase.Completed, "An empty copy should report completion.");
        });
    }

    private static async Task TestSkipExistingAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "clip.txt");
            string destinationFile = Path.Combine(destination, "clip.txt");
            await File.WriteAllTextAsync(sourceFile, "new card data");
            await File.WriteAllTextAsync(destinationFile, "existing archive data");
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new CopyOptions(ExistingFilePolicy.Skip, false),
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask, CancellationToken.None);
            Assert(result.Success && !result.VerificationPerformed,
                "Skipping an existing file should complete without verification.");
            Assert(result.CopiedFiles == 0 && result.CopiedBytes == 0,
                "A skipped existing file must not be counted as copied.");
            Assert(await File.ReadAllTextAsync(destinationFile) == "existing archive data",
                "Skip must preserve the existing destination file.");
        });
    }

    private static async Task TestCreateCopyAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "clip.mov");
            string destinationFile = Path.Combine(destination, "clip.mov");
            string duplicateFile = Path.Combine(destination, "clip (1).mov");
            await File.WriteAllTextAsync(sourceFile, "new card data");
            await File.WriteAllTextAsync(destinationFile, "existing archive data");
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new CopyOptions(ExistingFilePolicy.CreateCopy, true),
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask, CancellationToken.None);
            Assert(result.Success && result.VerificationPerformed,
                "The created copy should pass SHA-256 verification.");
            Assert(await File.ReadAllTextAsync(destinationFile) == "existing archive data",
                "Create-copy must preserve the original destination file.");
            Assert(File.Exists(duplicateFile), "Create-copy should use the '(1)' suffix.");
            Assert(await HashAsync(sourceFile) == await HashAsync(duplicateFile),
                "The created copy must match the source.");
            Assert(result.DestinationPaths["clip.mov"] == duplicateFile,
                "The actual create-copy destination should be returned for later verification.");

            CopyResult reverify = await new FileCopyService().CopyAndVerifyAsync(
                source,
                destination,
                new CopyOptions(
                    ExistingFilePolicy.Overwrite,
                    VerifyFiles: true,
                    SkipCopy: true)
                {
                    DestinationPaths = result.DestinationPaths
                },
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask,
                CancellationToken.None);
            Assert(reverify.Success && reverify.VerifiedFileCount == 1,
                "Reverification should use the persisted create-copy destination.");
        });
    }

    private static async Task TestVerificationDisabledAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "fast-copy.txt"), "copy only");
            var events = new List<CopyProgressInfo>();
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new CopyOptions(ExistingFilePolicy.Overwrite, false),
                new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask, CancellationToken.None);
            Assert(result.Success && !result.VerificationPerformed,
                "A copy-only task should succeed without verification.");
            Assert(result.VerifiedFiles.Count == 0, "No verification results should be created.");
            Assert(!events.Any(item => item.Phase == CopyPhase.Verifying),
                "The verifying phase must not run when disabled.");
            Assert(events.Last().Phase == CopyPhase.Completed,
                "A copy-only task should still report completion.");
        });
    }
    private static async Task TestAskPerFileDecisionsAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "overwrite.txt"), "new overwrite data");
            await File.WriteAllTextAsync(Path.Combine(source, "skip.txt"), "same skip data");
            await File.WriteAllTextAsync(Path.Combine(source, "copy.txt"), "new copy data");
            await File.WriteAllTextAsync(Path.Combine(source, "fresh.txt"), "fresh data");
            await File.WriteAllTextAsync(Path.Combine(destination, "overwrite.txt"), "old overwrite data");
            await File.WriteAllTextAsync(Path.Combine(destination, "skip.txt"), "same skip data");
            await File.WriteAllTextAsync(Path.Combine(destination, "copy.txt"), "old copy data");
            DateTime skipWriteTime = DateTime.UtcNow.AddDays(-4);
            File.SetLastWriteTimeUtc(Path.Combine(destination, "skip.txt"), skipWriteTime);

            var events = new List<CopyProgressInfo>();
            var conflicts = new List<DuplicateFileConflict>();
            bool resolverObservedVerifiedFreshFile = false;
            async Task<IReadOnlyDictionary<string, ExistingFilePolicy>> ResolveAsync(
                IReadOnlyList<DuplicateFileConflict> pending,
                CancellationToken token)
            {
                resolverObservedVerifiedFreshFile = events.Any(item => item.Phase == CopyPhase.Verifying) &&
                    File.Exists(Path.Combine(destination, "fresh.txt"));
                await Task.Yield();
                return new Dictionary<string, ExistingFilePolicy>(StringComparer.OrdinalIgnoreCase)
                {
                    ["overwrite.txt"] = ExistingFilePolicy.Overwrite,
                    ["skip.txt"] = ExistingFilePolicy.Skip,
                    ["copy.txt"] = ExistingFilePolicy.CreateCopy
                };
            }

            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new CopyOptions(ExistingFilePolicy.Ask, true),
                new InlineProgress<CopyProgressInfo>(events.Add),
                new InlineProgress<DuplicateFileConflict>(conflicts.Add),
                ResolveAsync, _ => Task.CompletedTask, CancellationToken.None);

            Assert(result.Success, "Per-file duplicate decisions should complete and verify.");
            Assert(conflicts.Count == 3, "Every duplicate file should be reported once.");
            Assert(resolverObservedVerifiedFreshFile,
                "Non-conflicting files should copy and verify before duplicate choices are applied.");
            Assert(await File.ReadAllTextAsync(Path.Combine(destination, "overwrite.txt")) == "new overwrite data",
                "The overwrite decision must replace only its selected file.");
            Assert(Math.Abs((File.GetLastWriteTimeUtc(Path.Combine(destination, "skip.txt")) - skipWriteTime).TotalSeconds) < 2,
                "The skip decision must leave its selected file untouched.");
            Assert(await File.ReadAllTextAsync(Path.Combine(destination, "copy.txt")) == "old copy data",
                "Create-copy must preserve the conflicting original.");
            Assert(await File.ReadAllTextAsync(Path.Combine(destination, "copy (1).txt")) == "new copy data",
                "Create-copy must write the selected duplicate to a suffixed file.");
            Assert(events.Any(item => item.Phase == CopyPhase.WaitingForDuplicateDecision),
                "Ask mode should report that it is waiting for per-file choices.");
        });
    }

    private static async Task TestFileFailureRecoveryAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "good.txt"), "good data");
            await File.WriteAllTextAsync(Path.Combine(source, "blocked.txt"), "retry data");
            string blockedDestination = Path.Combine(destination, "blocked.txt");
            Directory.CreateDirectory(blockedDestination);

            var events = new List<CopyProgressInfo>();
            var service = new FileCopyService();
            CopyOptions options = new(ExistingFilePolicy.Overwrite, false, false);
            CopyResult result = await service.CopyAndVerifyAsync(
                source,
                destination,
                options,
                new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(!result.Success, "A blocked destination should be reported as a file failure.");
            Assert(result.FailedFiles.Count == 1 && result.FailedFiles[0].RelativePath == "blocked.txt",
                "The blocked file should be returned as a structured failure.");
            Assert(result.CopiedFiles == 1,
                "Only the successfully copied file should be counted as copied.");
            Assert(result.CopiedBytes == new FileInfo(Path.Combine(source, "good.txt")).Length,
                "Failed-file bytes must not be added to the successful copied-byte count.");
            CopyProgressInfo completed = events.Last();
            Assert(completed.ProcessedFiles == result.FileCount &&
                   completed.SuccessfulFiles == result.CopiedFiles,
                "Terminal progress should distinguish processed files from successful copies.");
            Assert(File.Exists(Path.Combine(destination, "good.txt")),
                "Files after an individual failure must continue copying.");
            Assert(events.Last().Phase == CopyPhase.Completed,
                "The overall pass should reach completion while failures await a decision.");

            Directory.Delete(blockedDestination, true);
            FileRetryResult retry = await service.RetryFailedFilesAsync(
                result.FailedFiles,
                options,
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(retry.FailedFiles.Count == 0, "The failed file should succeed after its blocker is removed.");
            Assert(await File.ReadAllTextAsync(blockedDestination) == "retry data",
                "Retry should copy only the previously failed file to its intended destination.");
        });
    }

    private static async Task TestHistoryPersistenceAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClipPort-HistoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new JobHistoryService(root);
            var item = new JobHistoryItem
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = "Card A",
                SourcePath = @"F:\",
                DestinationPath = @"D:\Media\CardA",
                StartedAt = new DateTimeOffset(2026, 7, 21, 10, 30, 0, TimeSpan.FromHours(8)),
                FinishedAt = new DateTimeOffset(2026, 7, 21, 10, 45, 0, TimeSpan.FromHours(8)),
                TotalBytes = 123456789,
                FileCount = 42,
                CopiedBytes = 0,
                CopiedFiles = 0,
                VerifiedFiles = 42,
                CopySeconds = 0,
                VerifySeconds = 300,
                Status = JobStatus.Completed,
                CopyEnabled = false,
                VerificationEnabled = true,
                VerificationAlgorithm = VerificationAlgorithmKind.XxHash64,
                UseFastCopyAlgorithm = true,
                IsPriority = true,
                PreventSleep = false,
                IsAcknowledged = false,
                CopyByteSpeedSamples = Enumerable.Range(1, 91)
                    .Select(index => index * 1024d)
                    .ToList(),
                CopyItemSpeedSamples = Enumerable.Range(1, 91)
                    .Select(index => index / 10d)
                    .ToList(),
                CopyThroughputProgressSamples = Enumerable.Range(1, 91)
                    .Select(index => index / 91d)
                    .ToList(),
                DuplicateFiles = [new DuplicateFileConflict("clip.mov", @"F:\clip.mov", @"D:\Media\CardA\clip.mov", 1024)],
                DuplicateDecisions = new Dictionary<string, ExistingFilePolicy>(StringComparer.OrdinalIgnoreCase)
                {
                    ["clip.mov"] = ExistingFilePolicy.CreateCopy
                }
            };
            var opportunisticItem = new JobHistoryItem
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = "Background verification",
                SourcePath = @"F:\Media",
                DestinationPath = @"D:\Archive",
                StartedAt = DateTimeOffset.Now,
                Status = JobStatus.Completed,
                CopyEnabled = true,
                VerificationEnabled = true,
                VerificationExecutionMode = VerificationExecutionMode.OpportunisticDuringCopy
            };

            await service.SaveAsync([item, opportunisticItem]);
            List<JobHistoryItem> loaded = await service.LoadAsync();
            Assert(loaded.Count == 2, "Every history item should be restored.");
            Assert(loaded[0].Id == item.Id && loaded[0].TotalBytes == item.TotalBytes,
                "Persisted history details should round-trip.");
            Assert(loaded[0].Status == JobStatus.Completed,
                "The job status should round-trip as an enum value.");
            Assert(loaded[0].VerificationEnabled,
                "The verification setting should round-trip.");
            Assert(loaded[0].VerificationAlgorithm == VerificationAlgorithmKind.XxHash64,
                "The verification algorithm should round-trip.");
            Assert(loaded[0].VerificationExecutionMode == VerificationExecutionMode.AfterCopy &&
                   loaded[1].VerificationExecutionMode == VerificationExecutionMode.OpportunisticDuringCopy,
                "Legacy defaults and opportunistic verification mode should round-trip safely.");
            Assert(VerificationAlgorithms.Normalize((VerificationAlgorithmKind)999) ==
                   VerificationAlgorithmKind.Sha256,
                "An unknown persisted verification algorithm should fall back to SHA-256.");
            Assert(!loaded[0].CopyEnabled,
                "The copy setting should round-trip.");
            Assert(loaded[0].StatusText == "Result.VerificationCompleted" &&
                   loaded[0].CanStartVerification &&
                   loaded[0].CanExportReport,
                "A completed verification job should offer reverification.");
            var copyOnly = new JobHistoryItem
                {
                    Status = JobStatus.Completed,
                    CopyEnabled = true,
                    VerificationEnabled = false
                };
            Assert(copyOnly.StatusText == "Result.CopyCompletedShort" &&
                   copyOnly.CanStartVerification &&
                   copyOnly.CanExportReport,
                "A copy-only job should offer starting verification.");
            Assert(new JobHistoryItem
                {
                    Status = JobStatus.Completed,
                    CopyEnabled = true,
                    VerificationEnabled = true
                }.StatusText == "Result.TaskCompleted",
                "A copy-and-verification job should be labeled as task completed.");
            Assert(!new JobHistoryItem
                {
                    Status = JobStatus.Running,
                    CopyEnabled = true,
                    VerificationEnabled = false
                }.CanExportReport,
                "A running job should not offer report export.");
            JobStatus[] restartableStatuses =
            [
                JobStatus.CompletedWithErrors,
                JobStatus.VerificationFailed,
                JobStatus.Failed,
                JobStatus.Cancelled,
                JobStatus.Interrupted
            ];
            Assert(restartableStatuses.All(status => new JobHistoryItem { Status = status }.CanRestart),
                "Every unsuccessful terminal state should offer restarting.");
            Assert(!new JobHistoryItem { Status = JobStatus.Completed }.CanRestart &&
                   !new JobHistoryItem { Status = JobStatus.Running }.CanRestart,
                "Completed and running jobs should not offer restarting.");
            Assert(loaded[0].UseFastCopyAlgorithm,
                "The FastCopy algorithm setting should round-trip.");
            Assert(loaded[0].IsPriority,
                "The priority setting should round-trip.");
            Assert(!loaded[0].PreventSleep,
                "The sleep-prevention setting should round-trip.");
            Assert(loaded[0].DuplicateFiles.Count == 1 &&
                loaded[0].DuplicateDecisions["clip.mov"] == ExistingFilePolicy.CreateCopy,
                "Per-file duplicate decisions should round-trip.");
            Assert(!loaded[0].IsAcknowledged,
                "The acknowledgement state should round-trip.");
            Assert(loaded[0].CopyByteSpeedSamples.SequenceEqual(item.CopyByteSpeedSamples) &&
                   loaded[0].CopyItemSpeedSamples.SequenceEqual(item.CopyItemSpeedSamples) &&
                   loaded[0].CopyThroughputProgressSamples.SequenceEqual(
                       item.CopyThroughputProgressSamples),
                "Copy waveform samples should round-trip with task history.");
            Assert(loaded[0].NeedsAttention,
                "An unacknowledged completed job should request attention.");
            Assert(loaded[0].MetaText.Contains("117.74 MB"),
                "The history card should expose a formatted size.");

            string reportFile = await service.SaveReportAsync(item.Id, "report-body");
            Assert(await service.ReadReportAsync(reportFile) == "report-body",
                "A history report should be readable after restart.");
            await service.DeleteReportAsync(reportFile);
            Assert(await service.ReadReportAsync(reportFile) is null,
                "Deleting a history report should remove only that report.");
            Assert(!File.Exists(Path.Combine(root, "history.json.tmp")),
                "Atomic history writes should not leave a temporary file.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static Task TestPriorityTaskPresentationAsync()
    {
        var ordinaryJob = new JobHistoryItem
        {
            Status = JobStatus.Running,
            CopyEnabled = true
        };
        ordinaryJob.IsWaitingForPriority = true;
        Assert(ordinaryJob.StatusText == "Status.Waiting",
            "A running ordinary task preempted by priority work must appear as waiting in the sidebar.");
        ordinaryJob.IsWaitingForPriority = false;
        Assert(ordinaryJob.StatusText == "Status.Copying",
            "A resumed ordinary task must return to its live copy status in the sidebar.");

        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument mainWindow = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Localization", "MainWindow.xaml"));
        XElement[] priorityBadges = mainWindow
            .Descendants(presentation + "Border")
            .Where(element => element
                .Elements(presentation + "TextBlock")
                .Any(text => (string?)text.Attribute(x + "Uid") == "PriorityTaskBadgeText"))
            .ToArray();
        Assert(priorityBadges.Length == 3,
            "Priority badges must appear beside both sidebar task titles and the selected task title.");
        Assert(priorityBadges.All(element =>
                (string?)element.Attribute("Style") ==
                "{StaticResource PriorityTaskBadgeBorderStyle}") &&
               mainWindow.Descendants(presentation + "Style").Any(style =>
                   (string?)style.Attribute(x + "Key") == "PriorityTaskBadgeBorderStyle"),
            "Every priority badge must use the shared gray rounded-rectangle style.");

        return Task.CompletedTask;
    }

    private static async Task TestPrioritySchedulerAsync()
    {
        var scheduler = new CopyJobScheduler();

        // 空队列时第一个普通任务立即获得执行权，其余普通任务排队等待。
        using CopyJobScheduler.CopyJobScheduleRegistration firstOrdinary = scheduler.Register(false);
        using CopyJobScheduler.CopyJobScheduleRegistration secondOrdinary = scheduler.Register(false);
        using CopyJobScheduler.CopyJobScheduleRegistration thirdOrdinary = scheduler.Register(false);

        Task firstOrdinaryTurn = scheduler.WaitForTurnAsync(firstOrdinary);
        Task secondOrdinaryTurn = scheduler.WaitForTurnAsync(secondOrdinary);
        Task thirdOrdinaryTurn = scheduler.WaitForTurnAsync(thirdOrdinary);

        Assert(firstOrdinaryTurn.IsCompletedSuccessfully,
            "The first task should start immediately when the queue is empty.");
        Assert(!secondOrdinaryTurn.IsCompleted && !thirdOrdinaryTurn.IsCompleted,
            "Later tasks must wait while an earlier task is active.");
        Assert(scheduler.WaitingCount == 2 && scheduler.HasActiveJob,
            "Two tasks should be queued while one is active.");

        IDisposable firstOrdinaryReadLease =
            await scheduler.AcquireExecutionLeaseAsync(firstOrdinary);
        IDisposable firstOrdinaryWriteLease =
            await scheduler.AcquireExecutionLeaseAsync(firstOrdinary);

        // 后到的优先任务必须先等待普通任务在安全检查点确认停稳。
        CopyJobScheduler.CopyJobScheduleRegistration firstPriority = scheduler.Register(true);
        Task firstPriorityTurn = scheduler.WaitForTurnAsync(firstPriority);
        Assert(!firstPriorityTurn.IsCompleted &&
               scheduler.CanExecute(firstOrdinary) &&
               !scheduler.IsPreempted(firstOrdinary) &&
               scheduler.GetWaitingPosition(firstPriority) == 0,
            "A priority task must not start before the ordinary task acknowledges a safe checkpoint.");
        Assert(scheduler.WaitForExecutionAsync(firstOrdinary).IsCompletedSuccessfully,
            "An in-flight safe region must be allowed to release all of its I/O leases.");
        firstOrdinaryReadLease.Dispose();
        Assert(!firstPriorityTurn.IsCompleted,
            "One remaining I/O lease must keep the priority task blocked.");
        firstOrdinaryWriteLease.Dispose();
        await firstPriorityTurn.WaitAsync(TimeSpan.FromSeconds(1));
        Task firstOrdinaryBlocked = scheduler.WaitForExecutionAsync(firstOrdinary);
        Assert(!firstOrdinaryBlocked.IsCompleted &&
               scheduler.IsPreempted(firstOrdinary) &&
               scheduler.CanExecute(firstPriority),
            "The priority task should start only after the ordinary task confirms it has stopped.");
        Assert(scheduler.WaitingCount == 2 &&
               !secondOrdinaryTurn.IsCompleted &&
               !thirdOrdinaryTurn.IsCompleted,
            "Preemption must pause the active ordinary task without starting queued ordinary tasks.");

        // 暂停当前优先任务时，也必须在安全检查点确认后才恢复普通任务。
        scheduler.SetPaused(firstPriority, true);
        Assert(scheduler.CanExecute(firstPriority) && scheduler.IsPreempted(firstOrdinary),
            "Pausing a priority task must not transfer ownership before its safe checkpoint.");
        Task firstPriorityBlocked = scheduler.WaitForExecutionAsync(firstPriority);
        await firstOrdinaryBlocked.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(scheduler.CanExecute(firstOrdinary) &&
               !scheduler.CanExecute(firstPriority) &&
               !scheduler.IsPreempted(firstOrdinary) &&
               !firstPriorityBlocked.IsCompleted,
            "The ordinary task should resume after the paused priority task confirms it has stopped.");

        scheduler.SetPaused(firstPriority, false);
        Assert(scheduler.CanExecute(firstOrdinary) && !firstPriorityBlocked.IsCompleted,
            "Resuming a priority task must still wait for the ordinary safe checkpoint.");
        Task firstOrdinaryBlockedAgain = scheduler.WaitForExecutionAsync(firstOrdinary);
        await firstPriorityBlocked.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(!firstOrdinaryBlockedAgain.IsCompleted &&
               scheduler.CanExecute(firstPriority) &&
               scheduler.IsPreempted(firstOrdinary),
            "Resuming the priority task must safely preempt the ordinary task again.");

        // 多个优先任务之间仍保持先来先执行，不能并行占用磁盘。
        CopyJobScheduler.CopyJobScheduleRegistration secondPriority = scheduler.Register(true);
        Task secondPriorityTurn = scheduler.WaitForTurnAsync(secondPriority);
        Assert(!secondPriorityTurn.IsCompleted &&
               scheduler.GetWaitingPosition(secondPriority) == 0 &&
               scheduler.GetWaitingPosition(secondOrdinary) == 1,
            "Additional priority tasks should wait ahead of ordinary tasks in arrival order.");

        // 用户可强制继续被抢占的普通任务；所有优先任务都应转为手动暂停。
        scheduler.ForceResumeOrdinary(firstOrdinary);
        Assert(scheduler.CanExecute(firstPriority) &&
               scheduler.IsPaused(firstPriority) &&
               scheduler.IsPaused(secondPriority),
            "Forcing an ordinary task to resume must request every priority task to pause safely.");
        Task firstPriorityBlockedForForce = scheduler.WaitForExecutionAsync(firstPriority);
        await firstOrdinaryBlockedAgain.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(scheduler.CanExecute(firstOrdinary) &&
               !scheduler.CanExecute(firstPriority) &&
               !scheduler.CanExecute(secondPriority) &&
               !firstPriorityBlockedForForce.IsCompleted,
            "The ordinary task must resume only after the priority task reaches a safe checkpoint.");

        scheduler.SetPaused(firstPriority, false);
        Task firstOrdinaryBlockedThird = scheduler.WaitForExecutionAsync(firstOrdinary);
        await firstPriorityBlockedForForce.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(scheduler.CanExecute(firstPriority) &&
               scheduler.IsPreempted(firstOrdinary) &&
               !firstOrdinaryBlockedThird.IsCompleted,
            "Explicitly resuming a priority task must restore its execution right after safe handoff.");

        // 取消一个排队中的任务应当把它从队列移除。
        using CopyJobScheduler.CopyJobScheduleRegistration cancelledOrdinary = scheduler.Register(false);
        Task cancelledOrdinaryTurn = scheduler.WaitForTurnAsync(cancelledOrdinary);
        Assert(scheduler.GetWaitingPosition(cancelledOrdinary) == 3,
            "The task should join the queue behind the priority and ordinary tasks.");
        cancelledOrdinary.Dispose();
        try
        {
            await cancelledOrdinaryTurn.WaitAsync(TimeSpan.FromSeconds(1));
            throw new InvalidOperationException(
                "A task removed from the queue should not start.");
        }
        catch (OperationCanceledException)
        {
            // Expected: removing a queued task cancels its waiting turn.
        }
        Assert(scheduler.GetWaitingPosition(secondOrdinary) == 1,
            "Removing a queued task should close the gap behind it.");

        scheduler.SetPaused(secondPriority, false);
        firstPriority.Dispose();
        await secondPriorityTurn.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(secondPriorityTurn.IsCompletedSuccessfully &&
               scheduler.IsPreempted(firstOrdinary) &&
               !secondOrdinaryTurn.IsCompleted && !thirdOrdinaryTurn.IsCompleted,
            "The next priority task should run while the ordinary task remains preempted.");
        secondPriority.Dispose();

        Assert(!scheduler.IsPreempted(firstOrdinary) &&
               !secondOrdinaryTurn.IsCompleted &&
               !thirdOrdinaryTurn.IsCompleted,
            "The original ordinary task should resume only after all priority tasks finish.");

        firstOrdinary.Dispose();
        await secondOrdinaryTurn.WaitAsync(TimeSpan.FromSeconds(1));
        secondOrdinary.Dispose();
        await thirdOrdinaryTurn.WaitAsync(TimeSpan.FromSeconds(1));
        thirdOrdinary.Dispose();

        Assert(scheduler.WaitingCount == 0 && !scheduler.HasActiveJob,
            "The scheduler should return to an idle state after all tasks finish.");

        // 被优先任务暂挂时取消普通任务，优先任务结束后不得把已取消任务恢复。
        var cancellationScheduler = new CopyJobScheduler();
        CopyJobScheduler.CopyJobScheduleRegistration cancelledWhilePreempted =
            cancellationScheduler.Register(false);
        CopyJobScheduler.CopyJobScheduleRegistration preemptingPriority =
            cancellationScheduler.Register(true);
        CopyJobScheduler.CopyJobScheduleRegistration nextOrdinary =
            cancellationScheduler.Register(false);
        await cancellationScheduler.WaitForTurnAsync(cancelledWhilePreempted);
        Task preemptingPriorityTurn = cancellationScheduler.WaitForTurnAsync(preemptingPriority);
        Task cancelledOrdinaryBlocked =
            cancellationScheduler.WaitForExecutionAsync(cancelledWhilePreempted);
        await preemptingPriorityTurn.WaitAsync(TimeSpan.FromSeconds(1));
        Task nextOrdinaryTurn = cancellationScheduler.WaitForTurnAsync(nextOrdinary);
        Assert(cancellationScheduler.IsPreempted(cancelledWhilePreempted),
            "The ordinary task should be preempted before its cancellation is tested.");
        cancelledWhilePreempted.Dispose();
        try
        {
            await cancelledOrdinaryBlocked.WaitAsync(TimeSpan.FromSeconds(1));
            throw new InvalidOperationException(
                "A cancelled preempted task should remain stopped.");
        }
        catch (OperationCanceledException)
        {
            // Expected: disposing the preempted task cancels its blocked checkpoint.
        }
        preemptingPriority.Dispose();
        await nextOrdinaryTurn.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(!cancellationScheduler.IsPreempted(cancelledWhilePreempted),
            "A cancelled preempted task must not resume after the priority task finishes.");
        nextOrdinary.Dispose();
    }

    private static Task TestPathSafetyAsync()
    {
        string parent = Path.Combine(Path.GetTempPath(), "ClipPort-PathSafety");
        Assert(PathSafety.TryResolveSubfolder(parent, "Card_01", out string resolved) &&
               resolved == Path.GetFullPath(Path.Combine(parent, "Card_01")),
            "A simple subfolder name should stay under its selected parent.");
        Assert(!PathSafety.TryResolveSubfolder(parent, @"..\escape", out _),
            "Parent traversal must not escape the selected destination.");
        Assert(!PathSafety.TryResolveSubfolder(parent, Path.GetPathRoot(parent), out _),
            "An absolute subfolder value must be rejected.");
        Assert(PathSafety.PathsOverlap(
                Path.Combine(parent, "A"),
                Path.Combine(parent, "A", "child")),
            "Nested destination roots should be treated as overlapping.");
        Assert(!PathSafety.PathsOverlap(
                Path.Combine(parent, "A"),
                Path.Combine(parent, "AB")),
            "Sibling paths with a common text prefix must not be treated as overlapping.");
        Assert(!PathSafety.TryValidateSourceAndDestination(
                "\0invalid",
                parent,
                out PathValidationError invalidPathError) &&
               invalidPathError == PathValidationError.InvalidPath,
            "TryValidateSourceAndDestination should reject malformed paths without throwing.");

        string root = Path.GetPathRoot(parent)!;
        string suggested = PathSafety.GetSuggestedSubfolderName(
            root,
            new DateTime(2026, 7, 28, 12, 34, 56));
        Assert(!Path.IsPathRooted(suggested) &&
               suggested.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               suggested.EndsWith("20260728123456", StringComparison.Ordinal),
            "A drive root should produce a valid relative subfolder suggestion.");
        return Task.CompletedTask;
    }

    private static Task TestDisplayFormattingAsync()
    {
        Assert(DisplayFormatting.FormatBytes(1024) == "1.00 KB",
            "Every view should use the same byte-unit formatter.");
        Assert(DisplayFormatting.FormatDuration(TimeSpan.FromHours(54.5)) == "2:06:30:00",
            "Durations over 24 hours should show an unambiguous day and hour count.");
        Assert(DisplayFormatting.GetWaveformDivisionStep(0) == 0 &&
               DisplayFormatting.GetWaveformDivisionStep(1.14) == 0.5 &&
               DisplayFormatting.GetWaveformDivisionStep(4.1) == 1.5 &&
               DisplayFormatting.GetWaveformDivisionStep(8) == 3 &&
               DisplayFormatting.GetWaveformDivisionStep(10) == 5 &&
               DisplayFormatting.GetWaveformDivisionStep(35) == 15 &&
               DisplayFormatting.GetWaveformDivisionStep(60) == 20 &&
               DisplayFormatting.GetWaveformDivisionStep(99) == 50 &&
               DisplayFormatting.GetWaveformDivisionStep(100) == 50 &&
               DisplayFormatting.GetWaveformDivisionStep(350) == 150 &&
               DisplayFormatting.GetWaveformDivisionStep(401) == 150 &&
               DisplayFormatting.GetWaveformDivisionStep(600) == 200 &&
               DisplayFormatting.GetWaveformDivisionStep(601) == 300,
            "Waveform divisions should use readable half-step values that cover the visible peak.");

        var idleVerification = new CopyProgressInfo(
            CopyPhase.Verifying,
            100,
            40,
            10,
            4,
            string.Empty,
            0,
            TimeSpan.FromSeconds(2))
        {
            IsPhaseActive = false
        };
        Assert(DisplayFormatting.GetDisplayedOperationPhase(
                   true,
                   idleVerification,
                   idleVerification) == CopyPhase.Copying,
            "Idle background verification must not replace an active foreground copy status.");
        return Task.CompletedTask;
    }

    private static Task TestCopyThroughputSamplingAsync()
    {
        const double megabyte = 1024 * 1024;
        var sampler = new CopyThroughputSampler(capacity: 3, minimumIntervalSeconds: 0.2);
        var byteRates = new List<double>();
        var itemRates = new List<double>();
        var progressPositions = new List<double>();

        Assert(!sampler.TrySample(
                new CopyProgressInfo(
                    CopyPhase.Verifying, 100, 10, 10, 1, "verify-a.mov",
                    25 * megabyte, TimeSpan.FromSeconds(0.4)),
                byteRates,
                itemRates,
                progressPositions),
            "A copy sampler should ignore verification progress.");

        bool sampledTooEarly = sampler.TrySample(
                new CopyProgressInfo(
                    CopyPhase.Copying, 100, 10, 10, 1, "a.mov",
                    25 * megabyte, TimeSpan.FromSeconds(0.1)),
            byteRates,
            itemRates,
            progressPositions);
        Assert(!sampledTooEarly && byteRates.Count == 0,
            "Waveform sampling should throttle progress bursts.");

        Assert(sampler.TrySample(
                new CopyProgressInfo(
                    CopyPhase.Copying, 100, 20, 10, 2, "b.mov",
                    25 * megabyte, TimeSpan.FromSeconds(0.4)),
                byteRates,
                itemRates,
                progressPositions),
            "The first complete interval should produce a waveform sample.");
        Assert(Math.Abs(byteRates[0] - 25 * megabyte) < 1 &&
               Math.Abs(itemRates[0] - 5) < 0.001 &&
               Math.Abs(progressPositions[0] - 0.2) < 0.001,
            "Cumulative progress should be converted to instantaneous byte and item rates.");

        sampler.TrySample(
                new CopyProgressInfo(
                    CopyPhase.Copying, 100, 50, 10, 5, "e.mov",
                    30 * megabyte, TimeSpan.FromSeconds(0.8)),
            byteRates,
            itemRates,
            progressPositions);
        Assert(Math.Abs(byteRates[1] - 35 * megabyte) < 1 &&
               Math.Abs(itemRates[1] - 7.5) < 0.001 &&
               Math.Abs(progressPositions[0] - 0.2) < 0.001 &&
               Math.Abs(progressPositions[1] - 0.5) < 0.001,
            "Later samples should preserve existing positions and advance along the estimated timeline.");

        Assert(sampler.TryAppendIdleSample(byteRates, itemRates, progressPositions) &&
               byteRates[^1] == 0 && itemRates[^1] == 0 &&
               Math.Abs(progressPositions[^1] - 0.5) < 0.001 &&
               !sampler.TryAppendIdleSample(byteRates, itemRates, progressPositions),
            "A completed copy interval should end at zero exactly once.");

        for (int index = 1; index <= 4; index++)
        {
            double elapsed = 0.8 + index * 0.4;
            sampler.TrySample(
                new CopyProgressInfo(
                    CopyPhase.Copying, 100, 50 + index, 20, 5 + index,
                    $"extra-{index}.mov", 30 * megabyte, TimeSpan.FromSeconds(elapsed)),
                byteRates,
                itemRates,
                progressPositions);
        }
        Assert(byteRates.Count == 3 && itemRates.Count == 3 &&
               progressPositions.Count == 3 &&
               progressPositions.SequenceEqual(progressPositions.OrderBy(value => value)),
            "Waveform histories and their stable timeline positions should remain aligned and bounded.");

        var verifySampler = new CopyThroughputSampler(
            capacity: 3,
            minimumIntervalSeconds: 0.2,
            sampledPhase: CopyPhase.Verifying);
        var verifyByteRates = new List<double>();
        var verifyItemRates = new List<double>();
        var verifyProgressPositions = new List<double>();
        Assert(verifySampler.TrySample(
                new CopyProgressInfo(
                    CopyPhase.Verifying, 100, 40, 10, 4, "verify-d.mov",
                    100 * megabyte, TimeSpan.FromSeconds(0.4)),
                verifyByteRates,
                verifyItemRates,
                verifyProgressPositions) &&
               Math.Abs(verifyByteRates[0] - 100 * megabyte) < 1 &&
               Math.Abs(verifyItemRates[0] - 10) < 0.001 &&
               Math.Abs(verifyProgressPositions[0] - 0.4) < 0.001,
            "A verification sampler should produce independent byte and item rates.");
        return Task.CompletedTask;
    }

    private static Task TestInvalidSettingsEnumsAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "ClipPort-SettingsTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "settings.json"),
                """
                {
                  "theme": 999,
                  "accent": -7,
                  "language": 42,
                  "legacyExplorerContextMenuEnabled": true,
                  "logAndReportDirectory": "relative-output"
                }
                """);
            AppSettings loaded = new AppSettingsService(root).Load();
            Assert(loaded.Theme == AppThemeMode.System &&
                   loaded.Accent == AppAccentMode.System &&
                   loaded.Language == AppLanguage.SimplifiedChinese,
                "Undefined numeric enum values should fall back instead of crashing startup.");
            Assert(Path.IsPathFullyQualified(loaded.LogAndReportDirectory),
                "An invalid or relative output directory should return to the absolute default.");
            Assert(loaded.LegacyExplorerContextMenuEnabled,
                "The independent traditional-menu setting should survive settings loading.");
        }
        finally
        {
            Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }

    private static Task TestQuickStartRequestsAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "ClipPort-QuickStartTests",
            Guid.NewGuid().ToString("N"));
        string sourceA = Path.Combine(root, "source-a");
        string sourceB = Path.Combine(root, "source-b");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        Directory.CreateDirectory(destination);
        try
        {
            QuickStartRequest? parsedSource = QuickStartRequestParser.Parse(
                [QuickStartRequestParser.SourceOption, sourceA]);
            Assert(parsedSource is
                {
                    Role: QuickStartDirectoryRole.Source
                } && parsedSource.DirectoryPath == Path.GetFullPath(sourceA),
                "The source command-line option should produce a normalized source request.");

            var draft = new QuickStartDraft(null, destination).Apply(parsedSource!);
            Assert(draft.SourceDirectory == Path.GetFullPath(sourceA) &&
                   draft.DestinationDirectory == destination,
                "Applying a source request must preserve the destination directory.");

            QuickStartRequest? replacementSource = QuickStartRequestParser.Parse(
                [QuickStartRequestParser.SourceOption, sourceB]);
            draft = draft.Apply(replacementSource!);
            Assert(draft.SourceDirectory == Path.GetFullPath(sourceB) &&
                   draft.DestinationDirectory == destination,
                "Applying another source request must overwrite only the source directory.");

            QuickStartRequest? parsedDestination = QuickStartRequestParser.Parse(
                [QuickStartRequestParser.DestinationOption, destination]);
            var destinationDraft = new QuickStartDraft(sourceA, null).Apply(parsedDestination!);
            Assert(destinationDraft.SourceDirectory == sourceA &&
                   destinationDraft.DestinationDirectory == Path.GetFullPath(destination),
                "Applying a destination request must preserve the source directory.");

            Assert(QuickStartRequestParser.Parse(
                       [QuickStartRequestParser.SourceOption, Path.Combine(root, "missing")]) is null,
                "A missing directory must not produce a quick-start request.");
        }
        finally
        {
            Directory.Delete(root, true);
        }

        return Task.CompletedTask;
    }

    private static Task TestWideLayoutsKeepStretchingAsync()
    {
        string mainWindowXaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Localization",
            "MainWindow.xaml"));
        string settingsViewXaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Localization",
            "SettingsView.xaml"));

        Assert(!mainWindowXaml.Contains("MaxWidth=\"1180\"", StringComparison.Ordinal),
            "The task page must not stop stretching at 1180 DIP.");
        Assert(!settingsViewXaml.Contains("MaxWidth=\"920\"", StringComparison.Ordinal),
            "The settings page must not stop stretching at 920 DIP.");
        Assert(ResponsiveLayout.GetTaskContentWidth(1920) > 1180 &&
               ResponsiveLayout.GetTaskContentWidth(3200) >
               ResponsiveLayout.GetTaskContentWidth(1920),
            "The task page must continue growing on 1920 DIP and wider viewports.");
        return Task.CompletedTask;
    }

    private static async Task TestSourceScanningYieldsCallerAsync()
    {
        string sourceRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "ClipPort-ScannerTests",
            Guid.NewGuid().ToString("N")));
        const long hugeFileLength = 1_600_000_000_000;
        var provider = new DelayedSourceEntryProvider(
            TimeSpan.FromMilliseconds(350),
            [new SourceEntry(
                Path.Combine(sourceRoot, "huge.bin"),
                FileAttributes.Normal,
                hugeFileLength)]);
        var progressEvents = new List<CopyProgressInfo>();
        var scanner = new SourceScanner(provider);
        var stopwatch = Stopwatch.StartNew();

        Task<SourceScanResult> scanTask = scanner.ScanAsync(
            sourceRoot,
            new InlineProgress<CopyProgressInfo>(progressEvents.Add),
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200) && !scanTask.IsCompleted,
            "Starting a slow source scan must return without blocking the caller thread.");
        SourceScanResult result = await scanTask;
        Assert(result.TotalBytes == hugeFileLength && result.Files.Count == 1,
            "Scanning must preserve 1.6 TB file sizes without allocating file contents.");
        Assert(progressEvents.Last().Phase == CopyPhase.Scanning &&
               !progressEvents.Last().IsTotalKnown &&
               progressEvents.Last().ProcessedBytes == hugeFileLength,
            "Scanning should expose discovered counts while the final total is unknown.");
    }

    private static Task TestWaveformTimelineAsync()
    {
        Assert(WaveformTimeline.GetNormalizedX(1, 0) == 1,
            "A single current sample should be anchored at the right edge.");
        Assert(WaveformTimeline.GetNormalizedX(2, 0) == 0 &&
               WaveformTimeline.GetNormalizedX(2, 1) == 1,
            "Two samples should span the entire chart.");
        Assert(WaveformTimeline.GetNormalizedX(3, 0) == 0 &&
               WaveformTimeline.GetNormalizedX(3, 1) == 0.5 &&
               WaveformTimeline.GetNormalizedX(3, 2) == 1,
            "New samples should remain at the right edge while earlier samples compress left.");
        Assert(WaveformTimeline.GetNormalizedX(90, 0) == 0 &&
               WaveformTimeline.GetNormalizedX(90, 89) == 1,
            "Ninety retained samples should continue to fill the chart.");

        var sampler = new CopyThroughputSampler(
            CopyThroughputSampler.DefaultCapacity,
            minimumIntervalSeconds: 0.01);
        var byteRates = new List<double>();
        var itemRates = new List<double>();
        var progressPositions = new List<double>();
        for (int index = 1; index <= 91; index++)
        {
            double elapsedSeconds = index * 0.1;
            double cumulativeTransferredBytes = 1000 + index - 1;
            sampler.TrySample(
                new CopyProgressInfo(
                    CopyPhase.Copying,
                    91,
                    index,
                    91,
                    index,
                    $"sample-{index}",
                    cumulativeTransferredBytes / elapsedSeconds,
                    TimeSpan.FromSeconds(elapsedSeconds)),
                byteRates,
                itemRates,
                progressPositions);
        }
        Assert(byteRates.Count == 91 && itemRates.Count == 91 &&
               progressPositions.Count == 91 &&
               Math.Abs(progressPositions[0] - 1d / 91) < 0.0001 &&
               byteRates[0] > byteRates.Skip(1).Max() &&
               progressPositions[^1] == 1,
            "New samples must compress the full timeline without discarding the first reading or its peak.");

        double[] longTimeline = Enumerable.Repeat(10d, 2048).ToArray();
        longTimeline[0] = 900;
        longTimeline[777] = 700;
        longTimeline[^1] = 20;
        IReadOnlyList<WaveformDisplaySample> displaySamples =
            WaveformTimeline.CreateDisplaySamples(longTimeline, 128);
        Assert(displaySamples.Count <= 128 &&
               displaySamples[0].SourceIndex == 0 &&
               displaySamples[^1].SourceIndex == longTimeline.Length - 1 &&
               displaySamples.Any(sample => sample.SourceIndex == 777),
            "Display downsampling must preserve the full time range and old extrema.");

        WaveformCoordinate[] currentPoints =
        [
            new(0, 80),
            new(100, 20)
        ];
        WaveformCoordinate[] targetPoints =
        [
            new(0, 70),
            new(50, 40),
            new(100, 10)
        ];
        IReadOnlyList<WaveformCoordinate> animationStart =
            WaveformTimeline.AlignHorizontalTransition(currentPoints, targetPoints);
        Assert(animationStart.Select(point => point.Y)
                   .SequenceEqual(targetPoints.Select(point => point.Y)) &&
               animationStart.Select(point => point.X).SequenceEqual([0d, 100d, 100d]),
            "Waveform updates should animate horizontal compression without vertically wobbling old points.");

        var compactedByteRates = Enumerable.Range(0, 5000)
            .Select(index => index == 0 ? 9000d : index % 97)
            .ToList();
        var compactedItemRates = Enumerable.Range(0, 5000)
            .Select(index => index == 1777 ? 7000d : index % 41)
            .ToList();
        var compactedPositions = Enumerable.Range(0, 5000)
            .Select(index => index / 4999d)
            .ToList();
        CopyThroughputSampler.CompactToCapacity(
            compactedByteRates,
            compactedItemRates,
            compactedPositions);
        Assert(compactedByteRates.Count <= CopyThroughputSampler.DefaultCapacity &&
               compactedByteRates.Count == compactedItemRates.Count &&
               compactedByteRates.Count == compactedPositions.Count &&
               compactedByteRates[0] == 9000 &&
               compactedItemRates.Contains(7000) &&
               compactedPositions[^1] == 1,
            "Long waveform histories must stay bounded while retaining their beginning, end, and extrema.");
        return Task.CompletedTask;
    }

    private static Task TestLegacyExplorerContextMenuRegistrationAsync()
    {
        string executablePath = Path.Combine(
            Path.GetTempPath(),
            "Clip Port",
            "ClipPort.exe");
        string normalizedExecutablePath = Path.GetFullPath(executablePath);
        IReadOnlyList<LegacyExplorerContextMenuRegistration> registrations =
            LegacyExplorerContextMenuRegistrationFactory.Create(
                executablePath,
                AppLanguage.SimplifiedChinese);

        Assert(registrations.Count == 2,
            "Traditional quick start should register both folder and folder-background menus.");
        LegacyExplorerContextMenuRegistration directory = registrations.Single(
            registration => registration.MenuKeyPath ==
                LegacyExplorerContextMenuRegistrationFactory.DirectoryMenuKeyPath);
        LegacyExplorerContextMenuRegistration background = registrations.Single(
            registration => registration.MenuKeyPath ==
                LegacyExplorerContextMenuRegistrationFactory.BackgroundMenuKeyPath);
        Assert(directory.SourceCommand ==
               $"\"{normalizedExecutablePath}\" {QuickStartRequestParser.SourceOption} \"%1\"" &&
               directory.DestinationCommand ==
               $"\"{normalizedExecutablePath}\" {QuickStartRequestParser.DestinationOption} \"%1\"",
            "A selected folder should be quoted and passed through the %1 placeholder.");
        Assert(background.SourceCommand ==
               $"\"{normalizedExecutablePath}\" {QuickStartRequestParser.SourceOption} \"%V\"" &&
               background.DestinationCommand ==
               $"\"{normalizedExecutablePath}\" {QuickStartRequestParser.DestinationOption} \"%V\"",
            "A folder background should be quoted and passed through the %V placeholder.");
        Assert(registrations.All(registration =>
                !registration.SourceCommand.Contains(".msix", StringComparison.OrdinalIgnoreCase) &&
                !registration.SourceCommand.Contains(".cer", StringComparison.OrdinalIgnoreCase)),
            "Traditional registration must launch ClipPort directly without a package or certificate dependency.");

        LegacyExplorerContextMenuRegistration english =
            LegacyExplorerContextMenuRegistrationFactory.Create(
                executablePath,
                AppLanguage.English)[0];
        LegacyExplorerContextMenuRegistration classicalChinese =
            LegacyExplorerContextMenuRegistrationFactory.Create(
                executablePath,
                AppLanguage.ClassicalChinese)[0];
        Assert(english.SourceText == "Use as source directory" &&
               classicalChinese.SourceText == "以为源目录",
            "Traditional menu labels should follow every supported application language.");

        return Task.CompletedTask;
    }

    private static Task TestLegacyExplorerContextMenuRegistryLifecycleAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        string testRoot = $@"Software\Classes\ClipPort.CoreTests.{Guid.NewGuid():N}";
        string directoryMenuKeyPath = $@"{testRoot}\Directory";
        string backgroundMenuKeyPath = $@"{testRoot}\Background";
        var service = new LegacyExplorerContextMenuService(
            Path.Combine(Path.GetTempPath(), "Clip Port", "ClipPort.exe"),
            directoryMenuKeyPath,
            backgroundMenuKeyPath);
        try
        {
            LegacyExplorerContextMenuStatus enabledStatus = service.SetEnabled(
                true,
                AppLanguage.SimplifiedChinese);
            Assert(enabledStatus is { IsEnabled: true, ErrorMessage: null },
                "The traditional-menu service should write both per-user registry trees.");

            LegacyExplorerContextMenuStatus staleLanguageStatus = service.GetStatus(
                AppLanguage.English);
            Assert(!staleLanguageStatus.IsEnabled,
                "A language change should require the registered labels to be synchronized.");
            LegacyExplorerContextMenuStatus localizedStatus = service.SetEnabled(
                true,
                AppLanguage.English);
            Assert(localizedStatus is { IsEnabled: true, ErrorMessage: null },
                "Re-enabling should update the traditional menu to the selected language.");

            LegacyExplorerContextMenuStatus disabledStatus = service.SetEnabled(
                false,
                AppLanguage.English);
            using RegistryKey? remainingDirectoryKey =
                Registry.CurrentUser.OpenSubKey(directoryMenuKeyPath);
            using RegistryKey? remainingBackgroundKey =
                Registry.CurrentUser.OpenSubKey(backgroundMenuKeyPath);
            Assert(disabledStatus is { IsEnabled: false, ErrorMessage: null } &&
                   remainingDirectoryKey is null &&
                   remainingBackgroundKey is null,
                "Disabling should remove both ClipPort-owned registry trees.");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
        }

        return Task.CompletedTask;
    }

    private static Task TestLocalizedTaskReportAsync()
    {
        var job = new JobHistoryItem
        {
            DisplayName = "Card A",
            SourcePath = @"F:\",
            DestinationPath = @"D:\Archive",
            StartedAt = new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 7, 28, 10, 1, 0, TimeSpan.Zero),
            Status = JobStatus.Completed,
            CopyEnabled = true,
            VerificationEnabled = true,
            VerificationAlgorithm = VerificationAlgorithmKind.Md5,
            VerificationExecutionMode = VerificationExecutionMode.OpportunisticDuringCopy,
            FileCount = 1,
            CopiedFiles = 1,
            CopiedBytes = 4,
            VerifiedFiles = 1
        };
        var result = new CopyResult(
            true,
            1,
            4,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            true,
            [],
            [new FileVerificationResult("a.txt", 4, "AA", "AA", true, null)],
            [],
            [])
        {
            CopiedFiles = 1,
            CopiedBytes = 4,
            VerifiedFileCount = 1,
            VerifiedBytes = 4,
            VerificationAlgorithm = VerificationAlgorithmKind.Md5
        };

        ResourceService.SetLanguage(AppLanguage.English);
        string english = TaskReportBuilder.Build(result, job);
        Assert(english.Contains("Task name: Card A", StringComparison.Ordinal) &&
               english.Contains("Copied successfully: 1/1 files", StringComparison.Ordinal) &&
               english.Contains("Verification algorithm: MD5", StringComparison.Ordinal) &&
               english.Contains("Verification mode: Background verification while copying", StringComparison.Ordinal) &&
               english.Contains("MD5: AA", StringComparison.Ordinal) &&
               !english.Contains("任务名称", StringComparison.Ordinal),
            "English reports should not contain hard-coded Chinese labels.");

        ResourceService.SetLanguage(AppLanguage.SimplifiedChinese);
        string chinese = TaskReportBuilder.Build(result, job);
        Assert(chinese.Contains("任务名称：Card A", StringComparison.Ordinal),
            "Chinese reports should use the Chinese report resources.");

        ResourceService.SetLanguage(AppLanguage.ClassicalChinese);
        string classicalChinese = TaskReportBuilder.Build(result, job);
        Assert(classicalChinese.Contains("役名：Card A", StringComparison.Ordinal) &&
               classicalChinese.Contains("传写既成：1/1 卷", StringComparison.Ordinal) &&
               !classicalChinese.Contains("任务名称", StringComparison.Ordinal),
            "Classical Chinese reports should use the complete Classical Chinese resources.");

        ResourceService.SetLanguage(AppLanguage.SimplifiedChinese);
        return Task.CompletedTask;
    }

    private static async Task TestHistoryMalformedRecordIsolationAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "ClipPort-HistoryIsolationTests",
            Guid.NewGuid().ToString("N"));
        string reportsA = Path.Combine(root, "reports-a");
        string reportsB = Path.Combine(root, "reports-b");
        Directory.CreateDirectory(root);
        try
        {
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            };
            var valid = new JobHistoryItem
            {
                Id = "valid",
                DisplayName = "Valid task",
                Status = JobStatus.Completed,
                StartedAt = DateTimeOffset.Now
            };
            string validJson = JsonSerializer.Serialize(valid, jsonOptions);
            await File.WriteAllTextAsync(
                Path.Combine(root, "history.json"),
                $"[{validJson},{{\"id\":\"bad\",\"status\":\"NotARealStatus\"}}]");

            var service = new JobHistoryService(root, reportsA);
            List<JobHistoryItem> loaded = await service.LoadAsync();
            Assert(loaded.Count == 1 && loaded[0].Id == "valid",
                "One malformed history entry must not discard valid entries.");

            string reportPath = await service.SaveReportAsync("valid", "old-directory-report");
            service.SetReportsDirectory(reportsB);
            Assert(await service.ReadReportAsync(reportPath) == "old-directory-report",
                "An absolute saved report path should remain readable after changing directories.");
            await service.DeleteReportAsync(reportPath);
            Assert(!File.Exists(reportPath),
                "Deleting a report should remove its recorded absolute path.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task TestLegacyFailureReasonNormalizationAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "ClipPort-HistoryFailureReasonTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "history.json"),
                """
                [
                  {
                    "id": "legacy",
                    "status": "CompletedWithErrors",
                    "failedFiles": [
                      {
                        "relativePath": "mismatch.bin",
                        "sourcePath": "source\\mismatch.bin",
                        "destinationPath": "destination\\mismatch.bin",
                        "length": 4,
                        "stage": "Verifying",
                        "error": "Verification mismatch: mismatch.bin"
                      },
                      {
                        "relativePath": "unreadable.bin",
                        "sourcePath": "source\\unreadable.bin",
                        "destinationPath": "destination\\unreadable.bin",
                        "length": 8,
                        "stage": "Verifying",
                        "error": "Could not verify unreadable.bin: access denied"
                      },
                      {
                        "relativePath": "copy.bin",
                        "sourcePath": "source\\copy.bin",
                        "destinationPath": "destination\\copy.bin",
                        "length": 16,
                        "stage": "Copying",
                        "error": "Could not copy copy.bin: access denied"
                      }
                    ]
                  }
                ]
                """);

            List<JobHistoryItem> loaded = await new JobHistoryService(root).LoadAsync();
            Assert(loaded.Count == 1 && loaded[0].FailedFiles.Count == 3,
                "The legacy history record should load with every failure.");
            Assert(
                loaded[0].FailedFiles[0].Reason == FileOperationFailureReason.VerificationMismatch &&
                loaded[0].FailedFiles[0].IsVerificationMismatch,
                "A legacy mismatch message should migrate to the structured mismatch reason.");
            Assert(
                loaded[0].FailedFiles[1].Reason == FileOperationFailureReason.VerificationIo &&
                !loaded[0].FailedFiles[1].IsVerificationMismatch,
                "A legacy verification IO failure must not be mistaken for a mismatch.");
            Assert(
                loaded[0].FailedFiles[2].Reason == FileOperationFailureReason.CopyIo,
                "A legacy copy failure should migrate to the structured copy IO reason.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static Task TestHistoryRetentionProtectsActiveJobsAsync()
    {
        JobHistoryItem[] history =
        [
            new() { Id = "newest-terminal" },
            new() { Id = "oldest-terminal" },
            new() { Id = "older-active" },
            new() { Id = "oldest-active" }
        ];
        var activeIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "older-active",
            "oldest-active"
        };

        int removable = HistoryRetentionPolicy.FindOldestRemovableIndex(
            history,
            activeIds.Contains);
        Assert(removable == 1,
            "History trimming should skip older active jobs and choose the oldest terminal job.");

        activeIds.Add("newest-terminal");
        activeIds.Add("oldest-terminal");
        Assert(
            HistoryRetentionPolicy.FindOldestRemovableIndex(history, activeIds.Contains) == -1,
            "History trimming should keep every record when all jobs are active.");
        return Task.CompletedTask;
    }

    private static Task TestRetryResultWarningsAsync()
    {
        var result = new FileRetryResult([], TimeSpan.Zero, TimeSpan.Zero)
        {
            Warnings = ["timestamp warning"]
        };
        Assert(result.Warnings.SequenceEqual(["timestamp warning"]),
            "Retry results should carry non-fatal warnings into the final task result.");
        return Task.CompletedTask;
    }

    private static Task TestUpdateVersionComparisonAsync()
    {
        Assert(
            UpdateService.IsNewerVersion("1.0.0-beta", "1.0.1-beta"),
            "A newer patch release should be detected.");
        Assert(
            !UpdateService.IsNewerVersion("1.0.1-beta", "1.0.0-beta"),
            "An older patch release should not be detected.");
        Assert(
            !UpdateService.IsNewerVersion("1.0.0-beta", "1.0.0-beta"),
            "The same release should be considered up to date.");
        Assert(
            UpdateService.IsNewerVersion("1.0.0-beta", "1.0.0"),
            "A stable release should be newer than its beta.");
        Assert(
            !UpdateService.IsNewerVersion("1.0.0", "1.0.0-beta"),
            "A beta should not downgrade a stable release.");
        Assert(
            UpdateService.IsNewerVersion("1.0.0-beta", "1.0.0-beta.1"),
            "A longer pre-release identifier should be newer.");
        Assert(
            UpdateService.IsNewerVersion("1.0.0-beta.2", "1.0.0-beta.10"),
            "Numeric pre-release identifiers should compare numerically.");
        Assert(
            UpdateService.IsNewerVersion("1.0.0", "v1.0.1"),
            "A leading v prefix should be ignored.");
        return Task.CompletedTask;
    }

    private static Task TestUpdateReleaseAssetSelectionAsync()
    {
        Assert(
            UpdateService.IsZipAsset(new GitHubReleaseAsset
            {
                Name = "ClipPort-1.0.1-beta-win-x64.zip"
            }),
            "The portable win-x64 zip asset should match.");
        Assert(
            !UpdateService.IsZipAsset(new GitHubReleaseAsset
            {
                Name = "ClipPort-1.0.1-beta-win-x64.exe"
            }),
            "Non-zip assets should not match.");
        Assert(
            !UpdateService.IsZipAsset(new GitHubReleaseAsset
            {
                Name = "setup-x64.zip"
            }),
            "Unrelated zip assets should not match.");
        return Task.CompletedTask;
    }

    private static async Task WithTempFoldersAsync(Func<string, string, Task> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClipPort-CoreTests", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            await test(source, destination);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class DelayedSourceEntryProvider(
        TimeSpan delay,
        IReadOnlyList<SourceEntry> entries) : ISourceEntryProvider
    {
        public IEnumerable<SourceEntry> EnumerateDirectory(string directory)
        {
            Thread.Sleep(delay);
            return entries;
        }
    }
}
