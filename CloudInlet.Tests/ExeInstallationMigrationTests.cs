using CloudInlet.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ExeInstallationMigrationTests
{
    private const string Source = @"C:\Apps\CloudBay";
    private const string Destination = @"C:\Apps\CloudInlet";

    [TestMethod]
    public void SuccessUninstallsOnlyOriginalThenVerifiesAndPreservesCompletedUpdate()
    {
        var fixture = new Fixture();
        var result = ExeInstallationMigration.Resume(fixture, fixture);
        Assert.AreEqual("Completed", result.Phase);
        Assert.AreEqual(Destination, fixture.Installed);
        CollectionAssert.AreEqual(new[] { "uninstall", "install:" + Destination, "disarm" }, fixture.Mutations.ToArray());
        CollectionAssert.AreEqual(new[] { "UninstallingOriginal", "InstallingDestination", "Completed" }, fixture.Saved.ToArray());
    }

    [TestMethod]
    public void FailedDestinationRestoresSameUpdatedVersionAtOriginal()
    {
        var fixture = new Fixture { FailInstallAt = Destination };
        var result = ExeInstallationMigration.Resume(fixture, fixture);
        Assert.AreEqual("Reverted", result.Phase);
        Assert.AreEqual(Source, fixture.Installed);
        CollectionAssert.AreEqual(new[] { "uninstall", "install:" + Destination, "install:" + Source, "disarm" }, fixture.Mutations.ToArray());
        Assert.IsTrue(fixture.Saved.Contains("RestoringOriginal"));
    }

    [DataTestMethod]
    [DataRow("Prepared", true)]
    [DataRow("UninstallingOriginal", true)]
    [DataRow("UninstallingOriginal", false)]
    [DataRow("InstallingDestination", false)]
    public void CrashBoundariesResumeWithoutRepeatingCommittedUninstall(string phase, bool originalPresent)
    {
        var fixture = new Fixture { State = new() { Source = Source, Destination = Destination, Phase = phase, Sequence = 3 }, Installed = originalPresent ? Source : null };
        var result = ExeInstallationMigration.Resume(fixture, fixture);
        Assert.AreEqual("Completed", result.Phase);
        Assert.AreEqual(originalPresent ? 1 : 0, fixture.Mutations.Count(x => x == "uninstall"));
    }

    [TestMethod]
    public void CrashAfterTargetCommitFinalizesWithoutReinstallOrRevert()
    {
        var fixture = new Fixture { State = new() { Source = Source, Destination = Destination, Phase = "InstallingDestination" }, Installed = Destination };
        var result = ExeInstallationMigration.Resume(fixture, fixture, revert: true);
        Assert.AreEqual("Completed", result.Phase);
        CollectionAssert.AreEqual(new[] { "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void CrashAfterRestoreCommitDoesNotReinstall()
    {
        var fixture = new Fixture { State = new() { Source = Source, Destination = Destination, Phase = "RestoringOriginal" }, Installed = Source };
        Assert.AreEqual("Reverted", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        CollectionAssert.AreEqual(new[] { "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void CompletedMigrationCannotRevert()
    {
        var fixture = new Fixture { State = new() { Source = Source, Destination = Destination, Phase = "Completed" }, Installed = Destination };
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture, revert: true).Phase);
        Assert.AreEqual(0, fixture.Saved.Count);
        CollectionAssert.AreEqual(new[] { "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void JournalWriteFailureBeforeUninstallPreservesInstallation()
    {
        var fixture = new Fixture { FailSavePhase = "UninstallingOriginal" };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual(Source, fixture.Installed);
        Assert.AreEqual(0, fixture.Mutations.Count);
        Assert.AreEqual("Prepared", fixture.State.Phase);
    }

    [TestMethod]
    public void JournalWriteFailureAfterUninstallRetainsRecoverableIntent()
    {
        var fixture = new Fixture { FailSavePhase = "InstallingDestination" };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.IsNull(fixture.Installed);
        Assert.AreEqual("UninstallingOriginal", fixture.State.Phase);
        fixture.FailSavePhase = null;
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        Assert.AreEqual(1, fixture.Mutations.Count(x => x == "uninstall"));
    }

    [TestMethod]
    public void JournalWriteFailureAfterCommitRecoversByVerificationOnly()
    {
        var fixture = new Fixture { FailSavePhase = "Completed" };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual(Destination, fixture.Installed);
        Assert.AreEqual("InstallingDestination", fixture.State.Phase);
        fixture.FailSavePhase = null;
        ExeInstallationMigration.Resume(fixture, fixture);
        Assert.AreEqual(1, fixture.Mutations.Count(x => x.StartsWith("install:")));
    }

    [TestMethod]
    public void TelemetryFailureCannotUndoOrInterruptNativeCommit()
    {
        var fixture = new Fixture();
        var result = ExeInstallationMigration.Resume(fixture, fixture, _ => throw new IOException("observer failed"));
        Assert.AreEqual("Completed", result.Phase);
        Assert.AreEqual(Destination, fixture.Installed);
    }

    [TestMethod]
    public void ChangedRecoveryPackageBlocksAllMutations()
    {
        var fixture = new Fixture { RecoveryValid = false };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual(0, fixture.Mutations.Count);
    }

    [TestMethod]
    public void DestinationCollisionNeverUninstallsOriginal()
    {
        var fixture = new Fixture { DestinationAvailable = false };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual(Source, fixture.Installed);
        Assert.AreEqual(0, fixture.Mutations.Count);
    }

    [TestMethod]
    public void UninstallerFailureRetainsRecoveryAndNeverInstallsDestination()
    {
        var fixture = new Fixture { FailUninstall = true };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("UninstallingOriginal", fixture.State.Phase);
        Assert.AreEqual(Source, fixture.Installed);
        CollectionAssert.AreEqual(new[] { "uninstall" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void PartialRegisteredDestinationRequiresForwardRepairNotUninstallOrRestore()
    {
        var fixture = new Fixture { FailInstallAt = Destination, RegisterBeforeFailure = true };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("InstallingDestination", fixture.State.Phase);
        Assert.AreEqual(Destination, fixture.Installed);
        Assert.AreEqual(1, fixture.Mutations.Count(x => x == "uninstall"));
        Assert.IsFalse(fixture.Mutations.Contains("install:" + Source));
    }

    [TestMethod]
    public void FailedRestorationRetainsDurableRetryWithoutSilentCompletion()
    {
        var fixture = new Fixture { State = new() { Source = Source, Destination = Destination, Phase = "RestoringOriginal" }, Installed = null, FailInstallAt = Source };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("RestoringOriginal", fixture.State.Phase);
        Assert.IsFalse(fixture.Mutations.Contains("disarm"));
        fixture.FailInstallAt = null;
        Assert.AreEqual("Reverted", ExeInstallationMigration.Resume(fixture, fixture).Phase);
    }

    [TestMethod]
    public void RevertBeforeFirstMutationKeepsOriginalAndCompletedUpdate()
    {
        var fixture = new Fixture();
        Assert.AreEqual("Reverted", ExeInstallationMigration.Resume(fixture, fixture, revert: true).Phase);
        CollectionAssert.AreEqual(new[] { "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void ConflictingRegistrationAfterCrashIsNotRemoved()
    {
        var fixture = new Fixture { State = new() { Source = Source, Destination = Destination, Phase = "UninstallingOriginal" }, Installed = @"C:\Other\App" };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual(0, fixture.Mutations.Count);
    }

    [TestMethod]
    public void RegistrationOnlyMigrationKeepsCustomPathAndCompletesCanonicalRegistration()
    {
        var fixture = RegistrationFixture();
        var result = ExeInstallationMigration.Resume(fixture, fixture);
        Assert.AreEqual("Completed", result.Phase);
        Assert.IsTrue(result.RegistrationOnly);
        Assert.AreEqual(CustomDirectory, result.Source);
        Assert.AreEqual(CustomDirectory, result.Destination);
        Assert.AreEqual(CustomDirectory, fixture.Installed);
        Assert.AreEqual("CloudInlet", fixture.InstalledBrand);
        CollectionAssert.AreEqual(new[] { "uninstall", "install:" + CustomDirectory, "disarm" }, fixture.Mutations.ToArray());
        Assert.IsTrue(fixture.SavedRegistrationModes.All(value => value));
    }

    [TestMethod]
    public void RegistrationOnlyCancelBeforeMutationRetainsLegacyRegistration()
    {
        var fixture = RegistrationFixture();
        Assert.AreEqual("Reverted", ExeInstallationMigration.Resume(fixture, fixture, revert: true).Phase);
        Assert.AreEqual(CustomDirectory, fixture.Installed);
        Assert.AreEqual("CloudBay", fixture.InstalledBrand);
        CollectionAssert.AreEqual(new[] { "disarm" }, fixture.Mutations.ToArray());
        // A later recovery startup must verify the retained original rather than
        // requiring a canonical registration that was deliberately declined.
        Assert.AreEqual("Reverted", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        Assert.AreEqual(1, fixture.Saved.Count);
    }

    [DataTestMethod]
    [DataRow("UninstallingOriginal", true)]
    [DataRow("UninstallingOriginal", false)]
    [DataRow("InstallingDestination", false)]
    public void RegistrationOnlyCrashResumesWithoutMovingOrRepeatingRetiredUninstall(string phase, bool originalPresent)
    {
        var fixture = RegistrationFixture(phase, originalPresent ? "CloudBay" : null);
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        Assert.AreEqual(CustomDirectory, fixture.Installed);
        Assert.AreEqual("CloudInlet", fixture.InstalledBrand);
        Assert.AreEqual(originalPresent ? 1 : 0, fixture.Mutations.Count(x => x == "uninstall"));
    }

    [DataTestMethod]
    [DataRow("InstallingDestination")]
    [DataRow("Completed")]
    public void RegistrationOnlyCanonicalCommitCannotBeUndoneByRevert(string phase)
    {
        var fixture = RegistrationFixture(phase, "CloudInlet");
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture, revert: true).Phase);
        Assert.AreEqual("CloudInlet", fixture.InstalledBrand);
        CollectionAssert.AreEqual(new[] { "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void RegistrationOnlyLegacyAtTargetPathIsNotMistakenForCanonicalCommit()
    {
        var fixture = RegistrationFixture("InstallingDestination", "CloudBay");
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("InstallingDestination", fixture.State.Phase);
        Assert.AreEqual("CloudBay", fixture.InstalledBrand);
        Assert.AreEqual(0, fixture.Mutations.Count);
    }

    [TestMethod]
    public void RegistrationOnlyPartialCanonicalCommitRequiresRepairAndCannotRevert()
    {
        var fixture = RegistrationFixture("InstallingDestination", "CloudInlet");
        fixture.InstalledValid = false;
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture, revert: true));
        Assert.AreEqual("InstallingDestination", fixture.State.Phase);
        Assert.AreEqual("CloudInlet", fixture.InstalledBrand);
        Assert.AreEqual(0, fixture.Mutations.Count);
        fixture.InstalledValid = true;
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        CollectionAssert.AreEqual(new[] { "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void RegistrationOnlyPreparedOwnershipChangeBlocksNativeMutation()
    {
        var fixture = RegistrationFixture("Prepared", "CloudInlet");
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("Prepared", fixture.State.Phase);
        Assert.AreEqual(0, fixture.Mutations.Count);
    }

    [TestMethod]
    public void RegistrationOnlySuccessfulRecoveryReportsCompletedRatherThanFalseRevert()
    {
        var fixture = RegistrationFixture();
        fixture.FailInstallAttempts = 1;
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        Assert.AreEqual("CloudInlet", fixture.InstalledBrand);
        Assert.AreEqual(CustomDirectory, fixture.Installed);
        Assert.IsTrue(fixture.Saved.Contains("RestoringOriginal"));
        Assert.IsFalse(fixture.Saved.Contains("Reverted"));
        CollectionAssert.AreEqual(new[] { "uninstall", "install:" + CustomDirectory, "install:" + CustomDirectory, "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void RegistrationOnlyRevertAfterRetirementRestoresUsableCanonicalAppInPlace()
    {
        var fixture = RegistrationFixture("UninstallingOriginal", null);
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture, revert: true).Phase);
        Assert.AreEqual("CloudInlet", fixture.InstalledBrand);
        CollectionAssert.AreEqual(new[] { "install:" + CustomDirectory, "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void RegistrationOnlyRevertAfterIntentBeforeNativeMutationRetainsOriginal()
    {
        var fixture = RegistrationFixture("UninstallingOriginal", "CloudBay");
        Assert.AreEqual("Reverted", ExeInstallationMigration.Resume(fixture, fixture, revert: true).Phase);
        Assert.AreEqual("CloudBay", fixture.InstalledBrand);
        CollectionAssert.AreEqual(new[] { "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void RegistrationOnlyCrashAfterRestorationCommitRequiresNoSecondInstall()
    {
        var fixture = RegistrationFixture("RestoringOriginal", "CloudInlet");
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        CollectionAssert.AreEqual(new[] { "disarm" }, fixture.Mutations.ToArray());
    }

    [TestMethod]
    public void RegistrationOnlyFailedRestorationRetainsRetryAndDoesNotReportCompletion()
    {
        var fixture = RegistrationFixture("RestoringOriginal", null);
        fixture.FailInstallAttempts = 1;
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("RestoringOriginal", fixture.State.Phase);
        Assert.IsNull(fixture.Installed);
        Assert.IsFalse(fixture.Mutations.Contains("disarm"));
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
    }

    [TestMethod]
    public void RegistrationOnlyJournalFailureAfterCommitFinalizesWithoutReinstall()
    {
        var fixture = RegistrationFixture();
        fixture.FailSavePhase = "Completed";
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("CloudInlet", fixture.InstalledBrand);
        Assert.AreEqual("InstallingDestination", fixture.State.Phase);
        fixture.FailSavePhase = null;
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        Assert.AreEqual(1, fixture.Mutations.Count(x => x.StartsWith("install:")));
    }

    [TestMethod]
    public void RegistrationOnlyModeMustExactlyMatchSameDirectoryPlan()
    {
        var samePathWithoutConsent = new ExeInstallationMigration.State { Source = CustomDirectory, Destination = CustomDirectory };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Validate(samePathWithoutConsent));
        var differentPathsWithMode = new ExeInstallationMigration.State { Source = Source, Destination = Destination, RegistrationOnly = true };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Validate(differentPathsWithMode));
        var slashAliasWithoutMode = new ExeInstallationMigration.State { Source = CustomDirectory, Destination = CustomDirectory.Replace('\\', '/') };
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Validate(slashAliasWithoutMode));
    }

    [TestMethod]
    public void UpdatingOriginalFinishesVerifiedUpdateBeforeRetiringInstallation()
    {
        var fixture = UpdatingFixture();
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        CollectionAssert.AreEqual(new[] { "update", "uninstall", "install:" + CustomDirectory, "disarm" }, fixture.Mutations.ToArray());
        CollectionAssert.AreEqual(new[] { "Prepared", "UninstallingOriginal", "InstallingDestination", "Completed" }, fixture.Saved.ToArray());
        Assert.IsTrue(fixture.UpdateCompleted);
        Assert.IsTrue(fixture.SavedRegistrationModes.All(mode => mode));
    }

    [TestMethod]
    public void UpdatingOriginalRevertPreservesCompletedUpdateAndDeclinesRegistrationMigration()
    {
        var fixture = UpdatingFixture();
        Assert.AreEqual("Reverted", ExeInstallationMigration.Resume(fixture, fixture, revert: true).Phase);
        Assert.IsTrue(fixture.UpdateCompleted);
        Assert.AreEqual("CloudBay", fixture.InstalledBrand);
        CollectionAssert.AreEqual(new[] { "update", "disarm" }, fixture.Mutations.ToArray());
        CollectionAssert.AreEqual(new[] { "Prepared", "Reverted" }, fixture.Saved.ToArray());
    }

    [TestMethod]
    public void FailedOriginalUpdateNeverUninstallsOrReportsMigrationComplete()
    {
        var fixture = UpdatingFixture();
        fixture.FailUpdate = true;
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("UpdatingOriginal", fixture.State.Phase);
        Assert.AreEqual(CustomDirectory, fixture.Installed);
        Assert.IsFalse(fixture.UpdateCompleted);
        CollectionAssert.AreEqual(new[] { "update" }, fixture.Mutations.ToArray());
        Assert.AreEqual(0, fixture.Saved.Count);
    }

    [TestMethod]
    public void CrashAfterOriginalUpdateCommitVerifiesWithoutRepeatingUpdate()
    {
        var fixture = UpdatingFixture();
        fixture.UpdateCompleted = true;
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        Assert.IsFalse(fixture.Mutations.Contains("update"));
    }

    [TestMethod]
    public void FailedPreparedStateWriteAfterUpdateRetainsDurableRecoveryIntent()
    {
        var fixture = UpdatingFixture();
        fixture.FailSavePhase = "Prepared";
        Assert.ThrowsException<IOException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("UpdatingOriginal", fixture.State.Phase);
        Assert.IsTrue(fixture.UpdateCompleted);
        CollectionAssert.AreEqual(new[] { "update" }, fixture.Mutations.ToArray());
        fixture.FailSavePhase = null;
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        Assert.AreEqual(1, fixture.Mutations.Count(value => value == "update"));
    }

    [TestMethod]
    public void OriginalUpdateRestartPauseRetainsIntentWithoutUninstallOrRevert()
    {
        var fixture = UpdatingFixture();
        fixture.PauseUpdate = true;
        Assert.ThrowsException<ExeInstallationMigration.PauseException>(() => ExeInstallationMigration.Resume(fixture, fixture, revert: true));
        Assert.AreEqual("UpdatingOriginal", fixture.State.Phase);
        CollectionAssert.AreEqual(new[] { "update" }, fixture.Mutations.ToArray());
        Assert.AreEqual(0, fixture.Saved.Count);
        fixture.PauseUpdate = false;
        Assert.AreEqual("Reverted", ExeInstallationMigration.Resume(fixture, fixture, revert: true).Phase);
        Assert.AreEqual(1, fixture.Mutations.Count(value => value == "update"));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InstallRestartPauseNeverTriggersAutomaticRestoration(bool registeredBeforePause)
    {
        var fixture = RegistrationFixture("InstallingDestination", null);
        fixture.PauseInstall = true;
        fixture.RegisterBeforeFailure = registeredBeforePause;
        Assert.ThrowsException<ExeInstallationMigration.PauseException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.AreEqual("InstallingDestination", fixture.State.Phase);
        CollectionAssert.AreEqual(new[] { "install:" + CustomDirectory }, fixture.Mutations.ToArray());
        Assert.IsFalse(fixture.Saved.Contains("RestoringOriginal"));
        Assert.IsFalse(fixture.Saved.Contains("Completed"));
        fixture.PauseInstall = false;
        Assert.AreEqual("Completed", ExeInstallationMigration.Resume(fixture, fixture).Phase);
        Assert.AreEqual(registeredBeforePause ? 1 : 2, fixture.Mutations.Count(value => value.StartsWith("install:")));
    }

    [DataTestMethod]
    [DataRow("InstallingDestination", false)]
    [DataRow("InstallingDestination", true)]
    [DataRow("RestoringOriginal", false)]
    [DataRow("UpdatingOriginal", true)]
    public void SameBootRecoveryBarrierPreventsRetryOrRevertBeforeNativeVerification(string phase, bool registered)
    {
        var fixture = RegistrationFixture(phase, registered ? "CloudInlet" : null);
        fixture.RecoveryPaused = true;
        Assert.ThrowsException<ExeInstallationMigration.PauseException>(() => ExeInstallationMigration.Resume(fixture, fixture));
        Assert.ThrowsException<ExeInstallationMigration.PauseException>(() => ExeInstallationMigration.Resume(fixture, fixture, revert: true));
        Assert.AreEqual(phase, fixture.State.Phase);
        Assert.AreEqual(0, fixture.Mutations.Count);
        Assert.AreEqual(0, fixture.Saved.Count);
    }

    private static Fixture UpdatingFixture()
    {
        var fixture = RegistrationFixture("UpdatingOriginal");
        fixture.UpdateCompleted = false;
        return fixture;
    }

    private const string CustomDirectory = @"C:\Applications\My backup app";
    private static Fixture RegistrationFixture(string phase = "Prepared", string? brand = "CloudBay") => new()
    {
        State = new() { Source = CustomDirectory, Destination = CustomDirectory, RegistrationOnly = true, Phase = phase },
        Installed = brand == null ? null : CustomDirectory,
        InstalledBrand = brand
    };

    private sealed class Fixture : ExeInstallationMigration.IJournal, ExeInstallationMigration.IRuntime
    {
        internal ExeInstallationMigration.State State = new() { Source = Source, Destination = Destination };
        internal string? Installed = Source, InstalledBrand = "CloudBay", FailInstallAt, FailSavePhase;
        internal int FailInstallAttempts;
        internal bool RecoveryValid = true, DestinationAvailable = true, InstalledValid = true, UpdateCompleted = true,
            FailUninstall, RegisterBeforeFailure, FailUpdate, PauseUpdate, PauseInstall, RecoveryPaused;
        internal readonly List<string> Mutations = new(), Saved = new();
        internal readonly List<bool> SavedRegistrationModes = new();
        public ExeInstallationMigration.State Read() => State;
        public void Write(ExeInstallationMigration.State state) { if (state.Phase == FailSavePhase) throw new IOException("disk full"); State = state; Saved.Add(state.Phase); SavedRegistrationModes.Add(state.RegistrationOnly); }
        public void VerifyRecovery() { if (!RecoveryValid) throw new IOException("package changed"); if (RecoveryPaused) throw new ExeInstallationMigration.PauseException("restart required"); }
        public void UpdateOriginal()
        {
            if (UpdateCompleted) return;
            Mutations.Add("update");
            if (FailUpdate) throw new IOException("update failed");
            UpdateCompleted = true;
            if (PauseUpdate) throw new ExeInstallationMigration.PauseException("restart required");
        }
        public bool IsInstalledAt(string directory) => Installed == directory;
        public bool HasAnyRegistration() => Installed != null;
        public void VerifyOriginal() { if (Installed != State.Source || State.RegistrationOnly && InstalledBrand != "CloudBay") throw new IOException("original ownership changed"); }
        public void VerifyDestinationAvailable() { if (!DestinationAvailable) throw new IOException("destination collision"); }
        public void UninstallOriginal() { Mutations.Add("uninstall"); if (FailUninstall) throw new IOException("uninstaller failed"); Installed = null; InstalledBrand = null; }
        public void VerifyOriginalRetired() { if (Installed == State.Source && (!State.RegistrationOnly || InstalledBrand != "CloudInlet")) throw new IOException("original files retained"); }
        public void Install(string directory)
        {
            Mutations.Add("install:" + directory);
            if (PauseInstall)
            {
                if (RegisterBeforeFailure) { Installed = directory; InstalledBrand = "CloudInlet"; }
                throw new ExeInstallationMigration.PauseException("restart required");
            }
            var fail = FailInstallAt == directory || FailInstallAttempts > 0;
            if (FailInstallAttempts > 0) FailInstallAttempts--;
            if (RegisterBeforeFailure || !fail) { Installed = directory; InstalledBrand = "CloudInlet"; }
            if (fail) throw new IOException("installer failed");
        }
        public void VerifyInstalled(string directory)
        {
            if (Installed != directory || !InstalledValid) throw new IOException("not verified");
            if (State.RegistrationOnly && InstalledBrand != (State.Phase == "Reverted" ? "CloudBay" : "CloudInlet"))
                throw new IOException("registration brand is not verified");
        }
        public void VerifyRestoreAvailable() { }
        public void DisarmRecovery() => Mutations.Add("disarm");
    }
}
