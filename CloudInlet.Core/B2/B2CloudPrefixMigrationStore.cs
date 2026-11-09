using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CloudInlet.Core.Migration;

namespace CloudInlet.Core.B2;

public sealed partial class B2CloudStore
{
    /// <summary>
    /// Creates a version-preserving, server-side migration transport for one reviewed pair of folders.
    /// The caller owns the authenticated journal and its exclusive lease for the entire migration.
    /// No payload is downloaded or staged. Source history is never deleted by this transport.
    /// </summary>
    public ICloudPrefixMigrationStore CreatePrefixMigrationStore(string bucketId, string sourcePrefix,
        string destinationPrefix, IB2CloudPrefixCopyJournal journal, IReadOnlyList<CloudPrefixRule>? rules = null) =>
        new B2CloudPrefixMigrationStore(this, bucketId, sourcePrefix, destinationPrefix, journal, rules);

    private sealed class B2CloudPrefixMigrationStore : ICloudPrefixMigrationStore, ICloudPrefixMigrationHideRecovery, ICloudPrefixMigrationCopyRecovery
    {
        private const string TimestampMetadata = "cloudinlet_migration_source_upload_millis";
        private readonly B2CloudStore _owner;
        private readonly string _source, _destination;
        private readonly CloudPrefixKeyMap _keyMap;
        private readonly IB2CloudPrefixCopyJournal _journal;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private B2CloudPrefixCopyCheckpoint? _checkpoint;
        private Exception? _journalFailure;
        private bool _validated;
        public string AccountId { get; }
        public string BucketId { get; }

        public B2CloudPrefixMigrationStore(B2CloudStore owner, string bucket, string source, string destination,
            IB2CloudPrefixCopyJournal journal, IReadOnlyList<CloudPrefixRule>? rules)
        {
            ArgumentNullException.ThrowIfNull(journal);
            MigrationFolderPrefix(source); MigrationFolderPrefix(destination);
            if (MigrationPrefixesOverlap(source, destination)) throw new ArgumentException("Migration folders must not overlap.");
            _owner = owner; BucketId = bucket; AccountId = owner.Current.Account.AccountId;
            _source = source; _destination = destination; _journal = journal;
            _keyMap = new(source, destination, rules);
            _checkpoint = journal.Read();
            if (_checkpoint is not null) ValidateCheckpoint(_checkpoint);
        }

        public async Task ValidateAccessAsync(string sourcePrefix, string destinationPrefix, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Healthy();
                if (sourcePrefix != _source || destinationPrefix != _destination) throw Conflict("The reviewed cloud folders changed.");
                Capabilities();
                var inventory = await _owner.InspectPrefixMigrationAsync(BucketId, _source, _destination, token).ConfigureAwait(false);
                _keyMap.ValidateDistinctKeys(inventory.SourceVersions.Select(value => value.Key));
                var permitted = new[] { "destination-not-empty", "unfinished-uploads", "bucket-encryption-preservation-required", "version-encryption-preservation-required" };
                var blocked = inventory.Blockers.Where(b => !permitted.Contains(b.Code, StringComparer.Ordinal)).ToArray();
                if (blocked.Length != 0) throw Conflict(string.Join(" ", blocked.Select(b => b.Description)));
                if (!SupportedEncryption(inventory.DefaultEncryptionMode, inventory.DefaultEncryptionAlgorithm))
                    throw Conflict("The bucket encryption policy cannot be preserved by this migration.");
                foreach (var version in inventory.SourceVersions.Concat(inventory.DestinationVersions).Concat(inventory.UnfinishedUploads))
                    ValidateProtection(version);
                // B2 applies the bucket's effective encryption to an omitted/null destination setting.
                // An unencrypted historical payload cannot be represented unchanged in an encrypted bucket.
                if (inventory.DefaultEncryptionMode is not null && inventory.SourceVersions.Any(v => v.Action == "upload" && v.EncryptionMode is null))
                    throw Conflict("The bucket now encrypts new files, but some source history is unencrypted. Exact encryption preservation needs a separately reviewed policy.");
                if (_checkpoint is null)
                {
                    if (inventory.DestinationVersions.Count != 0 || inventory.UnfinishedUploads.Count != 0)
                        throw Conflict("A selected folder contains destination history or unfinished uploads. No existing data was merged or canceled.");
                    Persist(new(1, AccountId, BucketId, _source, _destination, inventory.BucketRevision, [], [], _keyMap.Rules));
                }
                else if (_checkpoint.BucketRevision != inventory.BucketRevision)
                    throw Conflict("The bucket policy changed since migration began. The saved recovery record and all original versions were retained.");
                foreach (var unfinished in inventory.UnfinishedUploads)
                {
                    var owned = _checkpoint!.Intents.SingleOrDefault(i => Matches(i, unfinished));
                    if (owned is null || owned.Phase is B2CloudPrefixCopyPhase.Ready or B2CloudPrefixCopyPhase.Completed or B2CloudPrefixCopyPhase.Canceled)
                        throw Conflict("An unfinished upload is not owned by this migration. No upload was canceled or replaced.");
                    ValidateReceipt(owned, unfinished, start: true);
                }
                _validated = true;
            }
            finally { _gate.Release(); }
        }

        public async Task<IReadOnlyList<CloudPrefixVersion>> ListAsync(string prefix, CancellationToken token)
        {
            Healthy(); Capabilities();
            if (prefix != _source && prefix != _destination) throw Conflict("Only the reviewed cloud folders may be listed.");
            var versions = await _owner.MigrationVersionsAsync(AccountId, BucketId, prefix, new MigrationInventoryBudget(), token).ConfigureAwait(false);
            // B2 returns unfinished starts in the all-version listing as well. They are transport
            // recovery state, not visible history. Suppress only exact authenticated owned starts;
            // foreign/source starts remain visible so the engine rejects them.
            if (prefix != _destination) return versions;
            return versions.Where(value => !IsOwnedUnfinished(value)).ToArray();
        }

        public async Task<CloudPrefixVersion> CopyAsync(CloudPrefixVersion source, string destinationKey, string operationId, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Ready(); ValidateSource(source, destinationKey, operationId);
                await VerifyPolicyAsync(token).ConfigureAwait(false);
                await VerifySourceAsync(source, token).ConfigureAwait(false);
                var intent = _checkpoint!.Intents.SingleOrDefault(i => i.OperationId == operationId);
                if (intent is not null && (!Same(source, intent.Source) || intent.DestinationKey != destinationKey))
                    throw Conflict("A cloud operation receipt belongs to a different source or destination.");
                if (intent is null)
                {
                    intent = new(operationId, Freeze(source), destinationKey);
                    Put(intent);
                }
                if (intent.Phase is B2CloudPrefixCopyPhase.Canceling or B2CloudPrefixCopyPhase.Canceled)
                    throw Conflict("This copy is being reverted. Finish recovery before reviewing another migration.");
                var existing = await FindReceiptAsync(intent, unfinished: false, token).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (intent.Completed is not null && !Same(intent.Completed, existing)) throw Conflict("A completed cloud receipt changed.");
                    Put(intent with { Phase = B2CloudPrefixCopyPhase.Completed, Completed = existing });
                    return existing;
                }
                if (intent.Phase == B2CloudPrefixCopyPhase.Completed) throw Conflict("A verified migration version is missing. It will not be silently recreated.");
                if (source.Size < MaxPartSize)
                {
                    if (intent.Phase != B2CloudPrefixCopyPhase.Ready) throw Unknown("copy");
                    token.ThrowIfCancellationRequested();
                    intent = intent with { Phase = B2CloudPrefixCopyPhase.Creating }; Put(intent);
                    JsonDocument response;
                    try
                    {
                        response = await _owner.ApiAsync("b2_copy_file", new
                        {
                            sourceFileId = source.FileId, fileName = destinationKey, destinationBucketId = BucketId,
                            metadataDirective = "REPLACE", contentType = source.ContentType, fileInfo = Metadata(intent),
                            destinationServerSideEncryption = Encryption(source)
                        }, token, retryNetwork: false, retryTransient: false).ConfigureAwait(false);
                    }
                    catch (B2RequestException error) when (DefinitiveRejection(error))
                    { Put(intent with { Phase = B2CloudPrefixCopyPhase.Ready }); throw new CloudMigrationRequestRejectedException("Backblaze rejected the creating copy request.", error); }
                    using (response)
                    {
                        var copy = MigrationVersion(response.RootElement, AccountId, BucketId, _destination, copyAcknowledgment: true);
                        ValidateReceipt(intent, copy, start: false);
                        await VerifyExactAsync(copy, token).ConfigureAwait(false);
                        Put(intent with { Phase = B2CloudPrefixCopyPhase.Completed, Completed = copy });
                        return copy;
                    }
                }
                return await CopyLargeAsync(intent, token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private async Task<CloudPrefixVersion> CopyLargeAsync(B2CloudPrefixCopyIntent intent, CancellationToken token)
        {
            if (intent.Phase == B2CloudPrefixCopyPhase.Creating)
            {
                var recovered = await FindReceiptAsync(intent, unfinished: true, token).ConfigureAwait(false);
                if (recovered is null) throw Unknown("large-file start");
                intent = intent with { LargeFileId = recovered.FileId, Phase = B2CloudPrefixCopyPhase.CopyingParts }; Put(intent);
            }
            if (intent.Phase == B2CloudPrefixCopyPhase.Ready)
            {
                var size = Math.Max(100 * 1024 * 1024L, (intent.Source.Size + 9_999) / 10_000);
                intent = intent with { PartSize = size, PartHashes = new string?[checked((int)((intent.Source.Size + size - 1) / size))], Phase = B2CloudPrefixCopyPhase.Creating };
                token.ThrowIfCancellationRequested(); Put(intent);
                JsonDocument response;
                try
                {
                    response = await _owner.ApiAsync("b2_start_large_file", new
                    {
                        bucketId = BucketId, fileName = intent.DestinationKey, contentType = intent.Source.ContentType,
                        fileInfo = Metadata(intent), serverSideEncryption = Encryption(intent.Source)
                    }, token, retryNetwork: false, retryTransient: false).ConfigureAwait(false);
                }
                catch (B2RequestException error) when (DefinitiveRejection(error))
                { Put(intent with { Phase = B2CloudPrefixCopyPhase.Ready }); throw new CloudMigrationRequestRejectedException("Backblaze rejected the creating large-file request.", error); }
                using (response)
                {
                    var started = _owner.ParseMigrationVersion(response.RootElement, AccountId, BucketId, _destination);
                    ValidateReceipt(intent, started, start: true);
                    intent = intent with { LargeFileId = started.FileId, Phase = B2CloudPrefixCopyPhase.CopyingParts }; Put(intent);
                }
            }
            var unfinished = await FindReceiptAsync(intent, unfinished: true, token).ConfigureAwait(false);
            if (unfinished is null || unfinished.FileId != intent.LargeFileId) throw Unknown("large-file recovery");
            await VerifyPartsAsync(intent, token).ConfigureAwait(false);
            for (var index = 0; index < intent.PartHashes!.Count; index++)
            {
                if (intent.PartHashes[index] is not null) continue;
                await VerifyPolicyAsync(token).ConfigureAwait(false);
                var offset = index * intent.PartSize; var length = Math.Min(intent.PartSize, intent.Source.Size - offset);
                // Same immutable source, destination file ID, part number and range: replay replaces
                // only our unfinished part and cannot create another visible file version.
                using var response = await _owner.ApiAsync("b2_copy_part", new
                {
                    sourceFileId = intent.Source.FileId, largeFileId = intent.LargeFileId, partNumber = index + 1,
                    range = FormattableString.Invariant($"bytes={offset}-{offset + length - 1}")
                }, token).ConfigureAwait(false);
                var hash = ValidatePart(intent, response.RootElement, index + 1, length);
                var hashes = MigrationSnapshotList<string?>.Set(intent.PartHashes, index, hash);
                intent = intent with { PartHashes = hashes }; Put(intent);
            }
            await VerifyPartsAsync(intent, token).ConfigureAwait(false);
            await VerifyPolicyAsync(token).ConfigureAwait(false);
            await VerifySourceAsync(intent.Source, token).ConfigureAwait(false);
            intent = intent with { Phase = B2CloudPrefixCopyPhase.Finishing }; Put(intent);
            // The known immutable file ID makes finish recoverable. No second start is issued.
            using var finished = await _owner.ApiAsync("b2_finish_large_file", new
                { fileId = intent.LargeFileId, partSha1Array = intent.PartHashes }, token, retryNetwork: false, retryTransient: false).ConfigureAwait(false);
            var result = _owner.ParseMigrationVersion(finished.RootElement, AccountId, BucketId, _destination);
            ValidateReceipt(intent, result, start: false);
            await VerifyExactAsync(result, token).ConfigureAwait(false);
            Put(intent with { Phase = B2CloudPrefixCopyPhase.Completed, Completed = result });
            return result;
        }

        public async Task<CloudPrefixVersion> HideOnceAsync(string destinationKey, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Ready(); DestinationKey(destinationKey); await VerifyPolicyAsync(token).ConfigureAwait(false);
                JsonDocument response;
                try
                {
                    response = await _owner.ApiAsync("b2_hide_file", new { bucketId = BucketId, fileName = destinationKey },
                        token, retryNetwork: false, retryTransient: false).ConfigureAwait(false);
                }
                catch (B2RequestException error) when (DefinitiveRejection(error))
                { throw new CloudMigrationRequestRejectedException("Backblaze rejected the hide-marker request.", error); }
                using (response)
                {
                    var marker = _owner.ParseMigrationVersion(response.RootElement, AccountId, BucketId, _destination);
                    if (marker.Key != destinationKey || marker.Action != "hide") throw Conflict("Backblaze returned a different hide marker.");
                    // The engine has already flushed its pending intent. If this write fails, neither
                    // provider nor engine guesses ownership from a later untagged marker listing.
                    Persist(_checkpoint! with { HideReceipts = MigrationSnapshotList<CloudPrefixVersion>.Append(_checkpoint.HideReceipts ?? [], marker) });
                    return marker;
                }
            }
            finally { _gate.Release(); }
        }

        public async Task DeleteExactAsync(CloudPrefixVersion version, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Ready(); DestinationKey(version.Key); await VerifyPolicyAsync(token).ConfigureAwait(false);
                var receipt = _checkpoint!.Intents.Select(i => i.Completed).Where(v => v is not null).Cast<CloudPrefixVersion>()
                    .Concat(_checkpoint.HideReceipts ?? []).SingleOrDefault(v => v.FileId == version.FileId);
                if (receipt is null || !Same(receipt, version)) throw Conflict("Only an exact version acknowledged by this migration can be reverted.");
                var listed = await _owner.MigrationVersionsAsync(AccountId, BucketId, version.Key, new MigrationInventoryBudget(), token).ConfigureAwait(false);
                var actual = listed.SingleOrDefault(v => v.FileId == version.FileId);
                if (actual is null) return; // Previously acknowledged exact deletion is idempotent.
                if (!Same(actual, version)) throw Conflict("A migration version changed before recovery. It was retained.");
                using var deleted = await _owner.ApiAsync("b2_delete_file_version", new { fileName = version.Key, fileId = version.FileId },
                    token, retryNetwork: false, retryTransient: false).ConfigureAwait(false);
                if (MigrationString(deleted.RootElement, "fileId") != version.FileId || MigrationString(deleted.RootElement, "fileName") != version.Key)
                    throw Conflict("Backblaze returned a different deletion acknowledgment.");
            }
            finally { _gate.Release(); }
        }

        public async Task<CloudPrefixVersion?> ReconcileCopyForRevertAsync(CloudPrefixVersion source, string destinationKey,
            string operationId, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Ready(); ValidateSource(source, destinationKey, operationId); await VerifyPolicyAsync(token).ConfigureAwait(false);
                var intent = _checkpoint!.Intents.SingleOrDefault(value => value.OperationId == operationId);
                if (intent is null) return null; // Outer intent was durable before transport was entered.
                if (!Same(source, intent.Source) || intent.DestinationKey != destinationKey)
                    throw Conflict("The recovery request does not match the authenticated copy intent.");
                var completed = await FindReceiptAsync(intent, unfinished: false, token).ConfigureAwait(false);
                if (completed is not null)
                {
                    if (intent.Phase is B2CloudPrefixCopyPhase.Canceled || intent.Completed is not null && !Same(intent.Completed, completed))
                        throw Conflict("An already recovered cloud copy unexpectedly reappeared or changed.");
                    Put(intent with { Phase = B2CloudPrefixCopyPhase.Completed, Completed = completed });
                    return completed;
                }
                if (intent.Phase == B2CloudPrefixCopyPhase.Completed)
                    throw Conflict("A completed copy is missing. Its disappearance was not acknowledged by this recovery.");
                if (intent.Phase is B2CloudPrefixCopyPhase.Ready or B2CloudPrefixCopyPhase.Canceled) return null;
                if (source.Size < MaxPartSize) throw Unknown("copy recovery");
                var unfinished = await FindReceiptAsync(intent, unfinished: true, token).ConfigureAwait(false);
                if (unfinished is null)
                {
                    // Only a previously flushed cancellation of an exact known ID can acknowledge
                    // absence. An ambiguous start is never treated as permission to guess or repeat.
                    if (intent.Phase != B2CloudPrefixCopyPhase.Canceling) throw Unknown("unfinished copy recovery");
                    Put(intent with { Phase = B2CloudPrefixCopyPhase.Canceled }); return null;
                }
                if (intent.LargeFileId is not null && intent.LargeFileId != unfinished.FileId)
                    throw Conflict("The unfinished copy no longer matches its authenticated immutable ID.");
                intent = intent with { LargeFileId = unfinished.FileId, Phase = B2CloudPrefixCopyPhase.Canceling }; Put(intent);
                using var canceled = await _owner.ApiAsync("b2_cancel_large_file", new { fileId = intent.LargeFileId }, token,
                    retryNetwork: false, retryTransient: false).ConfigureAwait(false);
                if (MigrationString(canceled.RootElement, "fileId") != intent.LargeFileId ||
                    MigrationString(canceled.RootElement, "fileName") != destinationKey ||
                    MigrationString(canceled.RootElement, "bucketId") != BucketId || MigrationString(canceled.RootElement, "accountId") != AccountId)
                    throw Conflict("Backblaze returned a different unfinished-copy cancellation acknowledgment.");
                if (await FindReceiptAsync(intent, unfinished: false, token).ConfigureAwait(false) is not null ||
                    await FindReceiptAsync(intent, unfinished: true, token).ConfigureAwait(false) is not null)
                    throw Conflict("The canceled copy still appears in cloud history. Recovery retained its receipt for another check.");
                Put(intent with { Phase = B2CloudPrefixCopyPhase.Canceled }); return null;
            }
            finally { _gate.Release(); }
        }

        public async Task<CloudPrefixVersion?> RecoverAcknowledgedHideAsync(string destinationKey,
            IReadOnlyCollection<string> acknowledgedVersionIds, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Ready(); DestinationKey(destinationKey); await VerifyPolicyAsync(token).ConfigureAwait(false);
                var candidates = (_checkpoint!.HideReceipts ?? []).Where(value => value.Key == destinationKey &&
                    !acknowledgedVersionIds.Contains(value.FileId, StringComparer.Ordinal)).ToArray();
                if (candidates.Length > 1) throw Conflict("Multiple acknowledged hide markers need review. No marker was guessed or replayed.");
                if (candidates.Length == 0) return null;
                var receipt = candidates[0];
                var versions = await _owner.MigrationVersionsAsync(AccountId, BucketId, destinationKey, new MigrationInventoryBudget(), token).ConfigureAwait(false);
                if (!versions.Any(value => Same(value, receipt))) throw Conflict("An acknowledged hide marker changed or disappeared.");
                return receipt;
            }
            finally { _gate.Release(); }
        }

        private async Task<CloudPrefixVersion?> FindReceiptAsync(B2CloudPrefixCopyIntent intent, bool unfinished, CancellationToken token)
        {
            var versions = unfinished
                ? await _owner.MigrationUnfinishedAsync(AccountId, BucketId, intent.DestinationKey, new MigrationInventoryBudget(), token).ConfigureAwait(false)
                : await _owner.MigrationVersionsAsync(AccountId, BucketId, intent.DestinationKey, new MigrationInventoryBudget(), token).ConfigureAwait(false);
            // A start also appears in b2_list_file_versions; it must not masquerade as a
            // completed receipt before the multipart recovery path has a chance to resume it.
            var matches = versions.Where(v => v.Action == (unfinished ? "start" : "upload") && Matches(intent, v)).ToArray();
            if (matches.Length > 1) throw Conflict("Multiple versions claim one migration receipt. No duplicate was adopted or deleted.");
            if (matches.Length == 0) return null;
            var match = matches[0]; ValidateReceipt(intent, match, unfinished);
            if (!unfinished) await VerifyExactAsync(match, token).ConfigureAwait(false);
            return match;
        }

        private async Task VerifyPartsAsync(B2CloudPrefixCopyIntent intent, CancellationToken token)
        {
            var seen = new HashSet<int>(); int? next = 1;
            do
            {
                using var response = await _owner.ApiAsync("b2_list_parts", new { fileId = intent.LargeFileId, startPartNumber = next, maxPartCount = 1000 }, token).ConfigureAwait(false);
                var parts = MigrationRequired(response.RootElement, "parts"); var cursor = MigrationRequired(response.RootElement, "nextPartNumber");
                if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() > 1000) throw Conflict("The copied-part inventory is incomplete.");
                foreach (var part in parts.EnumerateArray())
                {
                    var number = checked((int)MigrationLong(part, "partNumber"));
                    if (number < 1 || number > intent.PartHashes!.Count || !seen.Add(number)) throw Conflict("The copied-part inventory contains an invalid part.");
                    var hash = ValidatePart(intent, part, number, Math.Min(intent.PartSize, intent.Source.Size - (number - 1) * intent.PartSize));
                    if (intent.PartHashes[number - 1] is { } known && !hash.Equals(known, StringComparison.OrdinalIgnoreCase))
                        throw Conflict("An acknowledged copied part changed. The unfinished migration was retained.");
                }
                int? following = cursor.ValueKind == JsonValueKind.Null ? null : checked((int)MigrationLong(response.RootElement, "nextPartNumber"));
                if (following is not null && (following <= next || following > intent.PartHashes!.Count || parts.GetArrayLength() == 0))
                    throw Conflict("The copied-part inventory has an invalid continuation.");
                next = following;
            } while (next is not null);
            if (intent.PartHashes!.Select((hash, index) => (hash, index)).Any(v => v.hash is not null && !seen.Contains(v.index + 1)))
                throw Conflict("An acknowledged copied part is missing. No completed history was claimed.");
        }
        private static string ValidatePart(B2CloudPrefixCopyIntent intent, JsonElement part, int number, long length)
        {
            if (MigrationString(part, "fileId") != intent.LargeFileId || MigrationLong(part, "partNumber") != number ||
                MigrationLong(part, "contentLength") != length || !IsSha1(MigrationString(part, "contentSha1")))
                throw Conflict("A copied part does not match its immutable source range.");
            var encryption = MigrationRequired(part, "serverSideEncryption");
            if (MigrationNullableString(encryption, "mode") != intent.Source.EncryptionMode ||
                MigrationNullableString(encryption, "algorithm") != intent.Source.EncryptionAlgorithm)
                throw Conflict("A copied part did not retain the selected encryption.");
            return MigrationString(part, "contentSha1");
        }
        private async Task VerifySourceAsync(CloudPrefixVersion source, CancellationToken token)
        {
            using var response = await _owner.ApiAsync("b2_get_file_info", new { fileId = source.FileId }, token).ConfigureAwait(false);
            if (!Same(source, _owner.ParseMigrationVersion(response.RootElement, AccountId, BucketId, _source)))
                throw Conflict("The immutable source metadata or protection changed since review.");
        }
        private async Task VerifyExactAsync(CloudPrefixVersion expected, CancellationToken token)
        {
            using var response = await _owner.ApiAsync("b2_get_file_info", new { fileId = expected.FileId }, token).ConfigureAwait(false);
            if (!Same(expected, _owner.ParseMigrationVersion(response.RootElement, AccountId, BucketId, _destination)))
                throw Conflict("The copied version could not be independently verified.");
        }
        private async Task VerifyPolicyAsync(CancellationToken token)
        {
            Ready(); Capabilities();
            using var bucket = await _owner.MigrationBucketAsync(AccountId, BucketId, token).ConfigureAwait(false);
            if (MigrationLong(bucket.RootElement.GetProperty("buckets")[0], "revision") != _checkpoint!.BucketRevision)
                throw Conflict("The bucket policy changed. No further cloud mutations were sent.");
        }
        private void Capabilities()
        {
            _owner.MigrationAccount(AccountId);
            foreach (var capability in new[] { "listBuckets", "listFiles", "readFiles", "writeFiles", "deleteFiles", "readBucketEncryption",
                "readBucketRetentions", "readFileRetentions", "readFileLegalHolds", "readBucketReplications" }) _owner.ValidateCapability(capability);
            _owner.ValidateAccess(BucketId, _source, "readFiles"); _owner.ValidateAccess(BucketId, _destination, "writeFiles");
        }
        private void ValidateSource(CloudPrefixVersion source, string key, string operationId)
        {
            DestinationKey(key);
            if (!source.Key.StartsWith(_source, StringComparison.Ordinal) || source.Key.Length <= _source.Length ||
                key != _keyMap.Map(source.Key) || source.Action != "upload" || string.IsNullOrWhiteSpace(source.FileId) ||
                source.Size is < 0 or > MaxLargeFileSize || !IsSha1(source.Sha1) || string.IsNullOrWhiteSpace(source.ContentType) ||
                operationId is not { Length: 64 } || !operationId.All(Uri.IsHexDigit)) throw Conflict("The cloud copy does not match a reviewed immutable source.");
            ValidateProtection(source);
            if (source.Metadata.Keys.Any(k => k is CloudPrefixMigration.ReceiptMetadataKey or CloudPrefixMigration.SourceMetadataKey or TimestampMetadata))
                throw Conflict("Source metadata already contains migration receipts. Existing metadata will not be replaced.");
            if (source.Metadata.TryGetValue("large_file_sha1", out var sha) && !sha.Equals(source.Sha1, StringComparison.OrdinalIgnoreCase))
                throw Conflict("The source's whole-file checksum metadata is inconsistent.");
        }
        private void DestinationKey(string key)
        {
            ValidateKey(key);
            if (!key.StartsWith(_destination, StringComparison.Ordinal) || key.Length <= _destination.Length) throw Conflict("The target is outside the reviewed destination folder.");
        }
        private static void ValidateProtection(CloudPrefixVersion version)
        {
            if (!version.ProtectionReadable || version.RetentionMode is not null || version.RetainUntilMillis is not null ||
                version.LegalHold is not (null or "off") || version.ReplicationStatus is not null ||
                !SupportedEncryption(version.EncryptionMode, version.EncryptionAlgorithm))
                throw Conflict("Retention, legal hold, replication, SSE-C or unreadable protection requires a separately supported migration. No protection was weakened.");
        }
        private static bool SupportedEncryption(string? mode, string? algorithm) =>
            mode is null && algorithm is null || mode == "SSE-B2" && algorithm == "AES256";
        private static object Encryption(CloudPrefixVersion source) => new { mode = source.EncryptionMode, algorithm = source.EncryptionAlgorithm };
        private static Dictionary<string, string> Metadata(B2CloudPrefixCopyIntent intent)
        {
            var result = new Dictionary<string, string>(intent.Source.Metadata, StringComparer.Ordinal)
            {
                [CloudPrefixMigration.ReceiptMetadataKey] = intent.OperationId,
                [CloudPrefixMigration.SourceMetadataKey] = intent.Source.FileId,
                [TimestampMetadata] = intent.Source.UploadedAtMillis.ToString(CultureInfo.InvariantCulture)
            };
            if (intent.Source.Size >= MaxPartSize) result.TryAdd("large_file_sha1", intent.Source.Sha1!);
            return result;
        }
        private static bool Matches(B2CloudPrefixCopyIntent intent, CloudPrefixVersion version) =>
            version.Key == intent.DestinationKey && version.Metadata.TryGetValue(CloudPrefixMigration.ReceiptMetadataKey, out var receipt) && receipt == intent.OperationId &&
            version.Metadata.TryGetValue(CloudPrefixMigration.SourceMetadataKey, out var source) && source == intent.Source.FileId;
        private bool IsOwnedUnfinished(CloudPrefixVersion version)
        {
            if (version.Action != "start") return false;
            var intent = _checkpoint?.Intents.SingleOrDefault(value => Matches(value, version));
            if (intent is null || intent.Phase is B2CloudPrefixCopyPhase.Ready or B2CloudPrefixCopyPhase.Completed or B2CloudPrefixCopyPhase.Canceled)
                return false;
            ValidateReceipt(intent, version, start: true); return true;
        }
        private static void ValidateReceipt(B2CloudPrefixCopyIntent intent, CloudPrefixVersion value, bool start)
        {
            ValidateProtection(value);
            var source = intent.Source; var metadata = Metadata(intent);
            if (!Matches(intent, value) || value.FileId == source.FileId || intent.LargeFileId is not null && value.FileId != intent.LargeFileId ||
                value.Action != (start ? "start" : "upload") || value.Size != (start ? 0 : source.Size) ||
                !start && !string.Equals(value.Sha1, source.Sha1, StringComparison.OrdinalIgnoreCase) ||
                value.ContentType != source.ContentType || value.EncryptionMode != source.EncryptionMode || value.EncryptionAlgorithm != source.EncryptionAlgorithm ||
                value.LegalHold != source.LegalHold || value.Metadata.Count != metadata.Count ||
                metadata.Any(pair => !value.Metadata.TryGetValue(pair.Key, out var v) || v != pair.Value))
                throw Conflict("The copied version does not preserve exact source content, metadata, encryption and operation ownership.");
        }
        private static bool Same(CloudPrefixVersion left, CloudPrefixVersion right) =>
            left.FileId == right.FileId && left.Key == right.Key && left.Action == right.Action && left.Size == right.Size &&
            left.UploadedAtMillis == right.UploadedAtMillis && string.Equals(left.Sha1, right.Sha1, StringComparison.OrdinalIgnoreCase) &&
            left.ContentType == right.ContentType && left.ProtectionReadable == right.ProtectionReadable && left.RetentionMode == right.RetentionMode &&
            left.RetainUntilMillis == right.RetainUntilMillis && left.LegalHold == right.LegalHold && left.EncryptionMode == right.EncryptionMode &&
            left.EncryptionAlgorithm == right.EncryptionAlgorithm && left.ReplicationStatus == right.ReplicationStatus &&
            left.Metadata.Count == right.Metadata.Count && left.Metadata.All(p => right.Metadata.TryGetValue(p.Key, out var v) && p.Value == v);
        private static CloudPrefixVersion Freeze(CloudPrefixVersion value) => value with
            { Metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value.Metadata, StringComparer.Ordinal)) };
        private void ValidateCheckpoint(B2CloudPrefixCopyCheckpoint state)
        {
            if (state.Schema != 1 || state.AccountId != AccountId || state.BucketId != BucketId || state.SourcePrefix != _source ||
                state.DestinationPrefix != _destination || state.BucketRevision < 0 || state.Intents is null || state.Intents.Count > 100_000 ||
                !(state.Rules ?? []).SequenceEqual(_keyMap.Rules) ||
                state.Intents.Select(i => i.OperationId).Distinct(StringComparer.Ordinal).Count() != state.Intents.Count)
                throw Conflict("The provider recovery record does not match this account and migration.");
            foreach (var intent in state.Intents)
            {
                ValidateSource(intent.Source, intent.DestinationKey, intent.OperationId);
                var large = intent.Source.Size >= MaxPartSize;
                if (!Enum.IsDefined(intent.Phase) || !large && (intent.LargeFileId is not null || intent.PartSize != 0 || intent.PartHashes is not null) ||
                    large && intent.Phase != B2CloudPrefixCopyPhase.Ready && (intent.PartSize is < 5_000_000 or > MaxPartSize || intent.PartHashes is null ||
                    intent.PartHashes.Count != (intent.Source.Size + intent.PartSize - 1) / intent.PartSize || intent.PartHashes.Count > 10_000 ||
                    intent.PartHashes.Any(hash => hash is not null && !IsSha1(hash))) ||
                    intent.Phase is B2CloudPrefixCopyPhase.CopyingParts or B2CloudPrefixCopyPhase.Finishing or B2CloudPrefixCopyPhase.Canceling or B2CloudPrefixCopyPhase.Canceled && string.IsNullOrWhiteSpace(intent.LargeFileId) ||
                    !large && intent.Phase is B2CloudPrefixCopyPhase.Canceling or B2CloudPrefixCopyPhase.Canceled ||
                    (intent.Phase == B2CloudPrefixCopyPhase.Completed) != (intent.Completed is not null))
                    throw Conflict("The provider recovery record has inconsistent copy stages.");
                if (intent.Completed is not null) ValidateReceipt(intent, intent.Completed, start: false);
            }
            foreach (var marker in state.HideReceipts ?? [])
            { DestinationKey(marker.Key); if (marker.Action != "hide" || marker.Size != 0 || marker.Metadata.Count != 0) throw Conflict("A saved hide receipt is invalid."); }
        }
        private void Put(B2CloudPrefixCopyIntent intent)
        {
            var entries = _checkpoint!.Intents;
            var index = -1;
            for (var current = 0; current < entries.Count; current++)
                if (entries[current].OperationId == intent.OperationId) { index = current; break; }
            Persist(_checkpoint with { Intents = index < 0
                ? MigrationSnapshotList<B2CloudPrefixCopyIntent>.Append(entries, intent)
                : MigrationSnapshotList<B2CloudPrefixCopyIntent>.Set(entries, index, intent) });
        }
        private void Persist(B2CloudPrefixCopyCheckpoint checkpoint)
        {
            Healthy();
            try { _journal.Write(checkpoint); _checkpoint = checkpoint; }
            catch (Exception error) { _journalFailure = error; throw new MigrationJournalException("Cloud copying stopped because its durable request intent could not be saved.", error); }
        }
        private void Healthy()
        { if (_journalFailure is not null) throw new MigrationJournalException("Reopen the saved provider recovery record before continuing.", _journalFailure); }
        private void Ready() { Healthy(); if (!_validated || _checkpoint is null) throw Conflict("Review and validate the protected cloud inventory before execution."); }
        private static bool DefinitiveRejection(B2RequestException error) =>
            error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.RequestedRangeNotSatisfiable;
        private static InvalidDataException Conflict(string message) => new(message);
        private static IOException Unknown(string operation) => new($"The B2 {operation} outcome is unconfirmed. Its durable intent was retained; no duplicate request was sent. Retry after the existing receipt becomes available.");
    }

    // Keep the strict inventory parser shared by listing and creation acknowledgments.
    private CloudPrefixVersion ParseMigrationVersion(JsonElement value, string account, string bucket, string prefix) =>
        MigrationVersion(value, account, bucket, prefix);
}
