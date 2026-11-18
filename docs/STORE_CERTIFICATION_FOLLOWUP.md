# Store certification follow-up

Historical rejection analysis prepared on 2026-11-18. The later 1.2.1 release,
saved Partner Center changes and current submission status are recorded in
[RELEASE_1.2.1_PUBLICATION.md](RELEASE_1.2.1_PUBLICATION.md). The review-time
observations and pending actions below are retained as history.

## Verified report and submission

- Product: CloudInlet, `9N63JNRR5VB4`.
- Submission: `1152921505702060020`.
- [Report](https://partner.microsoft.com/en-us/dashboard/products/9N63JNRR5VB4/certification/reports/e82561f0-fe9c-4ef0-b72f-1cb2804da5db), review completed **2026-10-15**.
- One failed policy is listed: **10.6.3 Capabilities**, specifically `unvirtualizedResources`.
- Microsoft denied the request based on the supplied justification. Reconsideration needs new or expanded information, specifically a valid support contact or developer website. The report explicitly says GitHub is not allowed for that purpose.
- The Packages page showed `CloudInlet-1.2.0-win-x64-release-store.msixupload`, package version `1.2.0.0`, x64, minimum Windows build 19041.
- The live capability fields matched the original wording in `STORE_CERTIFICATION.md`. That document points to GitHub Issues as Support, with a separate contact email at the end. The current Store listing's support field could not be inspected before Browser access became unavailable.

This report does not identify a UI defect or request another installer test batch.
It also does not certify all other application behavior. Changing the support
information does not guarantee approval of the restricted capability.

## Correct the support information

Use **tahsinfaiyaz30@gmail.com** as the explicit primary publisher/support contact,
based on the existing project's certification contact. Put it at the beginning of
the capability response and Additional Testing Information, rather than relying on
a GitHub Issues link. Do not claim a support response time or a verified mailbox
delivery test.

If a Store field accepts only an HTTPS support/developer URL, provide an actual
publisher-controlled non-GitHub support page naming CloudInlet and displaying the
direct contact. No such URL has been verified in this review; do not invent one or
put an email address into a URL-only field. A code-repository link can remain a
separate source-code reference, but is not the requested support destination.

## Restricted-capability field (494 / 500 characters)

```text
Support: tahsinfaiyaz30@gmail.com (publisher Tahsin Faiyaz). Reconsideration of 10.6.3: user-approved Windows folder backup must update real Explorer mappings; package-private mappings cannot provide it. Shared per-user state enables recovery after reinstall or switching installers. Windows 11 uses scoped state-directory and two Shell-key exclusions; Windows 10 uses broad fallback switches. Exact paths, alternatives, safeguards and uninstall retention are in Additional Testing Information.
```

Keep the existing `runFullTrust` explanation unless the application changes; the
report specifically names `unvirtualizedResources` as denied.

## Additional Testing Information: proposed opening

Prepend this to the package-matched technical explanation and uninstall details.
It is a draft for owner review, not a message already sent to Microsoft.

```text
RECONSIDERATION: POLICY 10.6.3 - UNVIRTUALIZEDRESOURCES
Product: CloudInlet (9N63JNRR5VB4)
Publisher and direct support contact: Tahsin Faiyaz, tahsinfaiyaz30@gmail.com

This response addresses the certification report completed on 15 October 2026. Please use the direct email contact above for publisher questions and user support; a GitHub account or issue is not required. The previous submission identified GitHub Issues as its Support destination. This reconsideration identifies the publisher's direct contact prominently instead.

CloudInlet's requested capability supports two existing user-visible behaviors: Windows personal-folder backup that Explorer and other applications can see, and recovery of the same user's accounts and unfinished work after reinstalling or explicitly changing installer distribution. It is not requested merely to run a desktop app or preserve data during an ordinary Store update.

The user reviews a selected Windows-folder backup before the app calls SHSetKnownFolderPath. Package-private mappings cannot redirect the folder used by Explorer and other applications. Default virtualization and scoped exclusions were compared using isolated mapping-key fixtures; the original evidence and its limits are described below. Shared recovery state is scoped to the directories declared in the submitted package manifest. Neither purpose requires elevated execution or a system service.

Uninstall removes application/package-owned components while retaining shared account and recovery state. It does not silently download online-only content, erase credentials or cloud files, or restore Windows folder mappings. Before uninstalling, the user can explicitly disconnect, restore folder mappings, and choose whether to download files. Store removal can remove online-only placeholder entries; reconnecting with retained state and cloud access permits recovery. The exact retained locations, disconnect choices and recovery limits follow below.

We request reconsideration for this documented scenario and understand that approval remains Microsoft's decision. If the scenario is ineligible, please identify the unsupported requirement so we can evaluate a supported distribution or implementation without silently removing backup or recovery features.
```

## Match the explanation to the package

The original published `v1.2.0` packaging script and the saved
`artifacts/store-certification/store-1.2.0-manifest.json` declare one Windows 11
state-directory exclusion. Current source declares three, introduced by the
later migration work. The uploaded archive was not downloaded and rehashed in
this review; matching its filename/version does not prove byte identity.

| Windows 11 exclusion | Original v1.2.0 packaging | Current source |
| --- | --- | --- |
| `$(KnownFolder:LocalAppData)\CloudBay\Client` | Yes | Yes |
| `$(KnownFolder:LocalAppData)\CloudInlet\Client` | No | Yes |
| `$(KnownFolder:LocalAppData)\CloudInlet\Migration\Release` | No | Yes |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders` | Yes | Yes |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders` | Yes | Yes |

Both packaging scripts retain broad Windows 10 virtualization-disable switches.
Disclose that fallback; do not describe the package as narrowly scoped on all
supported versions. Current-source notes must also describe the CloudInlet client
directory and retained migration routing/recovery records in `UNINSTALL.md`.

- For a metadata-only reconsideration of the existing upload, keep its original
  technical scope and evidence; do not attribute later UI/migration changes or
  later fixture results to that uploaded binary.
- To submit the latest source, first prepare a new Store package and verify its
  generated manifest/inventory. Follow `RELEASING.md` and `RELEASE_STYLE.md` for
  versioning; do not overwrite the published v1.2.0 packages or move its tag.
- Keep the capability while evaluating reconsideration. Simply deleting it would
  change the virtualization behavior used by existing folder backup/recovery.
  Any alternative implementation must preserve the distribution parity contract.

Microsoft's [restricted-capability guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations#restricted-capabilities)
requires a specific justification and approval. Its `unvirtualizedResources`
entry explains both cross-application visibility and uninstall concerns. A
successful local build or test cannot grant Store approval.

## Remaining Partner Center actions

Inspect the actual Store listing support/developer fields; replace the rejected
contact destination with an appropriate supported contact/page. Save the revised
capability response and package-matched Additional Testing Information, then
request reconsideration. These external changes are still pending: Browser
returned `Local browser access is unavailable for this chat` on both the initial
connection check and one retry. No submission, package, publishing setting or
account credential was changed during this review.
