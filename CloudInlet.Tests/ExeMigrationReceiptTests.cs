using System.Runtime.Serialization.Json;
using CloudInlet.Packaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CloudInlet.Tests;

[TestClass]
public sealed class ExeMigrationReceiptTests
{
    [DataTestMethod]
    [DataRow("InstallingDestination")]
    [DataRow("RestoringOriginal")]
    [DataRow("Completed")]
    public void SavedRegistrationOnlyReceiptRetainsVerifiedSurvivorInventory(string phase)
    {
        var original = Receipt(phase);
        using var bytes = new MemoryStream();
        var serializer = new DataContractJsonSerializer(typeof(ExeMigrationReceipt));
        serializer.WriteObject(bytes, original);
        bytes.Position = 0;
        var restored = (ExeMigrationReceipt)serializer.ReadObject(bytes)!;

        restored.Validate(ReceiptPath(restored));

        Assert.IsTrue(restored.State.RegistrationOnly);
        Assert.AreEqual(phase, restored.State.Phase);
        Assert.AreEqual(original.State.Source, restored.State.Source);
        Assert.AreEqual(original.OriginalDirectoryIdentity, restored.DestinationDirectoryIdentity);
        Assert.AreEqual(1, restored.RetainedOriginalFiles!.Count);
        Assert.AreEqual("personal\\notes.txt", restored.RetainedOriginalFiles[0].Path);
        Assert.AreEqual(7L, restored.RetainedOriginalFiles[0].Size);
        Assert.AreEqual(new string('4', 64), restored.RetainedOriginalFiles[0].Sha256);
    }

    [DataTestMethod]
    [DataRow("Prepared")]
    [DataRow("UninstallingOriginal")]
    [DataRow("RestoringOriginal")]
    [DataRow("Reverted")]
    public void PreInstallRecoveryMayLackSurvivorLedger(string phase)
    {
        var receipt = Receipt(phase);
        receipt.RetainedOriginalFiles = null;
        // Restoration after a crash can discover retirement and durably capture
        // survivors before invoking setup. Initial cancellation needs no ledger.
        receipt.Validate(ReceiptPath(receipt));
    }

    [DataTestMethod]
    [DataRow("InstallingDestination")]
    [DataRow("Completed")]
    public void InstallingOrCompletedRegistrationRequiresDurableSurvivorLedger(string phase)
    {
        var receipt = Receipt(phase);
        receipt.RetainedOriginalFiles = null;
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void EmptySurvivorLedgerIsValidWhenNativeUninstallLeftNoPersonalFiles()
    {
        var receipt = Receipt("Completed");
        receipt.RetainedOriginalFiles = new();
        receipt.Validate(ReceiptPath(receipt));
    }

    [DataTestMethod]
    [DataRow("personal\\notes.txt")]
    [DataRow("PERSONAL\\NOTES.TXT")]
    public void DuplicateSurvivorPathsAreRejectedCaseInsensitively(string duplicatePath)
    {
        var receipt = Receipt();
        receipt.RetainedOriginalFiles!.Add(File(duplicatePath, 7, '4'));
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void SurvivorHashMustMatchVerifiedOriginal()
    {
        var receipt = Receipt();
        receipt.RetainedOriginalFiles![0].Sha256 = new string('a', 64);
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void SurvivorSizeMustMatchVerifiedOriginal()
    {
        var receipt = Receipt();
        receipt.RetainedOriginalFiles![0].Size++;
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void SurvivorNotPresentInOriginalInventoryIsRejected()
    {
        var receipt = Receipt();
        receipt.RetainedOriginalFiles![0].Path = "unexpected.txt";
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [DataTestMethod]
    [DataRow("..\\outside.txt")]
    [DataRow("C:\\outside.txt")]
    [DataRow("personal\\notes.txt:stream")]
    [DataRow("personal\\notes.txt.")]
    public void SurvivorCannotUseUnsafeOrAliasedPath(string path)
    {
        var receipt = Receipt();
        receipt.RetainedOriginalFiles![0].Path = path;
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void SameDirectoryReceiptCannotOmitRegistrationOnlyMode()
    {
        var receipt = Receipt();
        receipt.State.RegistrationOnly = false;
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void RegistrationOnlyReceiptCannotDescribeDifferentDirectories()
    {
        var receipt = Receipt();
        receipt.State.Destination = @"C:\Applications\Another client";
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void RelocationReceiptCannotClaimRegistrationOnlySurvivorLedger()
    {
        var receipt = Receipt();
        receipt.State.RegistrationOnly = false;
        receipt.State.Source = @"C:\Applications\CloudBay Debug";
        receipt.State.Destination = @"C:\Applications\CloudInlet Debug";
        receipt.DestinationDirectoryIdentity = "00000001:0000000000000003";
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void RegistrationOnlyReceiptMustRetainExactOriginalDirectoryIdentity()
    {
        var receipt = Receipt();
        receipt.DestinationDirectoryIdentity = "00000001:0000000000000003";
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void RegistrationOnlyReceiptRequiresOriginalLegacyRegistration()
    {
        var receipt = Receipt();
        receipt.OriginalBrand = "CloudInlet";
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [DataTestMethod]
    [DataRow("Update")]
    [DataRow("Uninstall")]
    [DataRow("Install")]
    public void UnknownNativeOutcomeRequiresAnotherBootBeforeRetry(string kind)
    {
        var attempt = Attempt(kind);
        attempt.Validate();
        Assert.IsTrue(attempt.RequiresRestart(Boot));
        Assert.ThrowsException<ExeInstallationMigration.PauseException>(() => attempt.EnsureRestartSatisfied(Boot));
        Assert.IsFalse(attempt.RequiresRestart(NextBoot));
        attempt.EnsureRestartSatisfied(NextBoot);
    }

    [DataTestMethod]
    [DataRow("Update")]
    [DataRow("Uninstall")]
    [DataRow("Install")]
    public void NativeRestartExitCannotBeSatisfiedWithinSameBoot(string kind)
    {
        var attempt = Attempt(kind, 3010);
        attempt.Validate();
        Assert.IsTrue(attempt.RequiresRestart(Boot));
        Assert.ThrowsException<ExeInstallationMigration.PauseException>(() => attempt.EnsureRestartSatisfied(Boot));
        Assert.IsFalse(attempt.RequiresRestart(NextBoot));
        attempt.EnsureRestartSatisfied(NextBoot);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(1603)]
    public void KnownNonRestartExitDoesNotInventBootBarrier(int exitCode)
    {
        var attempt = Attempt("Install", exitCode);
        attempt.Validate();
        Assert.IsFalse(attempt.RequiresRestart(Boot));
        attempt.EnsureRestartSatisfied(Boot);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not-a-boot")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    public void NativeAttemptRejectsMissingMalformedOrEmptyBootIdentity(string invalidBoot)
    {
        var attempt = Attempt("Install");
        attempt.BootIdentity = invalidBoot;
        Assert.ThrowsException<IOException>(attempt.Validate);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("Repair")]
    [DataRow("install")]
    public void NativeAttemptRejectsUnknownOperationKind(string invalidKind)
    {
        var attempt = Attempt(invalidKind);
        Assert.ThrowsException<IOException>(attempt.Validate);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("not-a-boot")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    public void UnknownCurrentBootCannotReleaseRestartBarrier(string invalidCurrentBoot)
    {
        var attempt = Attempt("Install", 3010);
        Assert.ThrowsException<IOException>(() => attempt.RequiresRestart(invalidCurrentBoot));
        Assert.ThrowsException<IOException>(() => attempt.EnsureRestartSatisfied(invalidCurrentBoot));
    }

    [TestMethod]
    public void EquivalentBootGuidFormattingCannotPretendWindowsRestarted()
    {
        var attempt = Attempt("Install", 3010);
        Assert.IsTrue(attempt.RequiresRestart(Boot.ToUpperInvariant()));
        Assert.ThrowsException<ExeInstallationMigration.PauseException>(() => attempt.EnsureRestartSatisfied(Boot.ToUpperInvariant()));
    }

    [TestMethod]
    public void ReceiptValidationIncludesNativeAttemptEvidence()
    {
        var receipt = Receipt();
        receipt.NativeAttempt = Attempt("Install", 3010);
        receipt.Validate(ReceiptPath(receipt));
        receipt.NativeAttempt.BootIdentity = "invalid";
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void SavedNativeAttemptPreservesUnknownOutcomeAndOriginalUpdateVersion()
    {
        var receipt = Receipt("UpdatingOriginal");
        receipt.RetainedOriginalFiles = null;
        receipt.OriginalVersion = "1.2.0";
        receipt.NativeAttempt = Attempt("Update");
        receipt.Failure = "The native installer outcome could not be saved.";
        receipt.FailureRequiresRestart = true;
        using var bytes = new MemoryStream();
        var serializer = new DataContractJsonSerializer(typeof(ExeMigrationReceipt));
        serializer.WriteObject(bytes, receipt);
        bytes.Position = 0;
        var restored = (ExeMigrationReceipt)serializer.ReadObject(bytes)!;
        restored.Validate(ReceiptPath(restored));
        Assert.AreEqual("1.2.0", restored.OriginalVersion);
        Assert.AreEqual("Update", restored.NativeAttempt!.Kind);
        Assert.AreEqual(Boot, restored.NativeAttempt.BootIdentity);
        Assert.IsNull(restored.NativeAttempt.ExitCode);
        Assert.IsTrue(restored.FailureRequiresRestart);
        Assert.AreEqual(receipt.Failure, restored.Failure);
        Assert.ThrowsException<ExeInstallationMigration.PauseException>(() => restored.NativeAttempt.EnsureRestartSatisfied(Boot));
    }

    [TestMethod]
    public void LegacyReceiptWithoutNativeAttemptRemainsReadable()
    {
        var receipt = Receipt();
        Assert.IsNull(receipt.OriginalVersion);
        Assert.IsNull(receipt.NativeAttempt);
        receipt.Validate(ReceiptPath(receipt));
    }

    [TestMethod]
    public void OriginalVersionMustBeValidIfPresent()
    {
        var receipt = Receipt("UpdatingOriginal");
        receipt.RetainedOriginalFiles = null;
        receipt.OriginalVersion = "not-a-version";
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [TestMethod]
    public void OriginalVersionCannotBeNewerThanTarget()
    {
        var receipt = Receipt("UpdatingOriginal");
        receipt.RetainedOriginalFiles = null;
        receipt.OriginalVersion = "1.3.0";
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
    }

    [DataTestMethod]
    [DataRow("Prepared")]
    [DataRow("InstallingDestination")]
    [DataRow("Completed")]
    [DataRow("Reverted")]
    public void PostUpdateStatesMustReferenceCompletedTargetVersion(string phase)
    {
        var receipt = Receipt(phase);
        receipt.OriginalVersion = "1.2.0";
        Assert.ThrowsException<IOException>(() => receipt.Validate(ReceiptPath(receipt)));
        receipt.OriginalVersion = receipt.Version;
        receipt.Validate(ReceiptPath(receipt));
    }

    private const string Boot = "0c43d3d0-a137-4b9f-9e5b-184006cd4531";
    private const string NextBoot = "fcd5c4a3-821a-4aef-9256-aaf0b6d3fd53";
    private static ExeNativeAttempt Attempt(string kind, int? exitCode = null) => new()
    { Kind = kind, BootIdentity = Boot, ExitCode = exitCode };

    private static ExeMigrationReceipt Receipt(string phase = "InstallingDestination") => new()
    {
        Id = "e4fa2b018a644c4fb287d69cf0156fc7",
        RequestHash = new string('a', 64),
        Flavor = "Debug",
        Version = "1.2.1",
        OriginalBrand = "CloudBay",
        Uninstaller = "unins000.exe",
        Package = File("installer.exe", 1024, '1'),
        Helper = File("recovery.exe", 128, '2'),
        OriginalDirectoryIdentity = "00000001:0000000000000002",
        DestinationDirectoryIdentity = "00000001:0000000000000002",
        PayloadFiles = new() { File("CloudInlet.exe", 4096, '3') },
        OriginalFiles = new()
        {
            File("CloudInlet.exe", 4096, '3'), File("personal\\notes.txt", 7, '4'),
            File("unins000.exe", 13, '5'), File("unins000.dat", 14, '6'),
            File("CloudBay.exe", 17, '7')
        },
        RetainedOriginalFiles = new() { File("personal\\notes.txt", 7, '4') },
        State = new()
        {
            Source = @"C:\Applications\My backup client",
            Destination = @"C:\Applications\My backup client",
            RegistrationOnly = true,
            Phase = phase,
            Sequence = 2
        }
    };

    private static ExeMigrationFile File(string path, long size, char hash) => new()
    { Path = path, Size = size, Sha256 = new string(hash, 64) };

    // Validation only: these tests never create an application directory, write
    // a live recovery receipt, or inspect/change installed registry state.
    private static string ReceiptPath(ExeMigrationReceipt receipt) =>
        Path.Combine(ExeMigrationReceipt.Root(receipt.Flavor), receipt.Id, "migration.dpapi");
}
