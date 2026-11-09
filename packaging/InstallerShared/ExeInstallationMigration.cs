using System;
using System.IO;
using System.Runtime.Serialization;

namespace CloudInlet.Packaging;

/// <summary>Durably stages the update before relocating its verified installation.
/// The same verified newer installer is the recovery package, so reverting a
/// failed migration never rolls back a successfully installed update.</summary>
internal static class ExeInstallationMigration
{
    internal sealed class PauseException : IOException
    {
        internal PauseException(string message) : base(message) { }
        internal PauseException(string message, Exception inner) : base(message, inner) { }
    }
    internal interface IJournal
    {
        State Read();
        void Write(State state);
    }

    internal interface IRuntime
    {
        // Every call must validate the current exact ownership and package.
        void VerifyRecovery();
        void UpdateOriginal();
        bool IsInstalledAt(string directory);
        bool HasAnyRegistration();
        void VerifyOriginal();
        void VerifyDestinationAvailable();
        void UninstallOriginal();
        void VerifyOriginalRetired();
        void Install(string directory);
        void VerifyInstalled(string directory);
        void VerifyRestoreAvailable();
        void DisarmRecovery();
    }

    [DataContract]
    internal sealed class State
    {
        [DataMember(IsRequired = true)] public int SchemaVersion { get; set; } = 1;
        [DataMember(IsRequired = true)] public string Source { get; set; } = "";
        [DataMember(IsRequired = true)] public string Destination { get; set; } = "";
        [DataMember] public bool RegistrationOnly { get; set; }
        [DataMember(IsRequired = true)] public string Phase { get; set; } = "Prepared";
        [DataMember(IsRequired = true)] public string Detail { get; set; } = "The updated installation is ready for migration.";
        [DataMember(IsRequired = true)] public long Sequence { get; set; }
    }

    internal static State Resume(IJournal journal, IRuntime runtime, Action<string>? progress = null, bool revert = false)
    {
        var state = journal.Read();
        Validate(state);
        if (state.Phase == "Completed" || state.Phase == "Reverted")
        {
            // Completed items are immutable. A missing/changed installation is
            // reported; it is never repaired or rolled back through this receipt.
            if (state.RegistrationOnly && state.Phase == "Reverted") runtime.VerifyOriginal();
            else runtime.VerifyInstalled(state.Phase == "Completed" ? state.Destination : state.Source);
            runtime.DisarmRecovery();
            return state;
        }
        runtime.VerifyRecovery();

        void Save(string phase, string detail)
        {
            // No external mutation follows a failed durable state write.
            var next = new State { Source = state.Source, Destination = state.Destination,
                RegistrationOnly = state.RegistrationOnly, Phase = phase, Detail = detail, Sequence = checked(state.Sequence + 1) };
            journal.Write(next);
            state = next;
            // Telemetry is deliberately after the authority was persisted.
            try { progress?.Invoke(detail); } catch { }
        }

        var completedDetail = state.RegistrationOnly
            ? "CloudInlet installer registration is verified. The installation folder was retained."
            : "CloudInlet is installed and verified at the selected location.";

        if (state.Phase == "UpdatingOriginal")
        {
            // The verified update and the selected migration share one durable
            // recovery record, including across an update-required reboot.
            runtime.UpdateOriginal();
            Save("Prepared", "The updated original installation is verified. The selected migration is ready.");
        }

        // An install may have committed immediately before power loss. Verify
        // it before considering recovery, never uninstall a completed target.
        if (state.Phase == "InstallingDestination" && runtime.IsInstalledAt(state.Destination))
        {
            runtime.VerifyInstalled(state.Destination);
            runtime.VerifyOriginalRetired();
            Save("Completed", completedDetail);
            runtime.DisarmRecovery();
            return state;
        }
        if (revert && (state.Phase == "Prepared" || state.RegistrationOnly &&
            state.Phase == "UninstallingOriginal" && runtime.IsInstalledAt(state.Source)))
        {
            runtime.VerifyOriginal();
            Save("Reverted", "The original installation and registration were retained. The completed application update was preserved.");
            runtime.DisarmRecovery();
            return state;
        }
        if (revert && state.Phase != "RestoringOriginal")
        {
            // An incomplete new registration cannot safely be displaced by a
            // blanket uninstall. Leave it for repair with its evidence intact.
            if (state.Phase == "InstallingDestination" && runtime.HasAnyRegistration())
                throw new IOException("An incomplete installation is registered. Repair it before reverting; no completed installation was removed.");
            Save("RestoringOriginal", "Restoring the updated application at its original location.");
        }

        if (state.Phase == "Prepared")
        {
            runtime.VerifyOriginal();
            runtime.VerifyDestinationAvailable();
            Save("UninstallingOriginal", "Removing only installer-owned application files from the original location. Personal files and settings remain intact.");
        }
        if (state.Phase == "UninstallingOriginal")
        {
            // A crash before invocation retains the exact original registration;
            // a crash after native uninstall is recognized by absence + retirement.
            if (runtime.IsInstalledAt(state.Source))
            {
                runtime.VerifyOriginal();
                runtime.UninstallOriginal();
            }
            else if (runtime.HasAnyRegistration())
                throw new IOException("The EXE installation ownership changed. Recovery was paused without changing the conflicting installation.");
            runtime.VerifyOriginalRetired();
            runtime.VerifyDestinationAvailable();
            Save("InstallingDestination", state.RegistrationOnly
                ? "Updating installer registration in the existing folder and preserving startup and shortcut choices."
                : "Installing CloudInlet at the selected location and preserving startup and shortcut choices.");
        }
        if (state.Phase == "InstallingDestination")
        {
            if (runtime.HasAnyRegistration())
                throw new IOException("A conflicting installation is registered. Migration is paused for repair; its recovery files were retained.");
            try
            {
                runtime.VerifyDestinationAvailable();
                runtime.Install(state.Destination);
                runtime.VerifyInstalled(state.Destination);
                runtime.VerifyOriginalRetired();
            }
            catch (PauseException) { throw; }
            catch (Exception error)
            {
                // If installation committed, do not uninstall it because a
                // reporting/verification step failed. Forward repair is required.
                if (runtime.HasAnyRegistration()) throw;
                Save("RestoringOriginal", (state.RegistrationOnly ? "Installer registration could not be updated: " : "The new location could not be installed: ") +
                    error.Message + " Restoring the completed update at its original location.");
            }
            if (state.Phase == "InstallingDestination")
            {
                Save("Completed", completedDetail);
                runtime.DisarmRecovery();
                return state;
            }
        }
        if (state.Phase == "RestoringOriginal")
        {
            if (!runtime.IsInstalledAt(state.Source))
            {
                if (runtime.HasAnyRegistration()) throw new IOException("A conflicting installation prevents restoration. No registered installation was removed.");
                runtime.VerifyRestoreAvailable();
                runtime.Install(state.Source);
            }
            runtime.VerifyInstalled(state.Source);
            if (state.RegistrationOnly)
            {
                runtime.VerifyOriginalRetired();
                Save("Completed", "The updated application was restored in its existing folder and its CloudInlet installer registration was verified.");
            }
            else Save("Reverted", "CloudInlet was restored at its original location. The completed application update was preserved; location migration can be reviewed again.");
            runtime.DisarmRecovery();
        }
        return state;
    }

    internal static void Validate(State state)
    {
        if (state == null || state.SchemaVersion != 1 || state.Sequence < 0 || state.Detail == null || state.Detail.Length > 16384 ||
            (state.Phase != "UpdatingOriginal" && state.Phase != "Prepared" && state.Phase != "UninstallingOriginal" && state.Phase != "InstallingDestination" &&
             state.Phase != "RestoringOriginal" && state.Phase != "Completed" && state.Phase != "Reverted"))
            throw new IOException("The EXE migration recovery state is invalid.");
        var source = InstallationMigrationPolicy.Normalize(state.Source);
        var destination = InstallationMigrationPolicy.Normalize(state.Destination);
        if (state.RegistrationOnly != string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The EXE migration mode does not match its reviewed installation locations.");
    }
}
