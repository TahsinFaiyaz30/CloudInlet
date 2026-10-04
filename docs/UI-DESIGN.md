# Windows 11 interface

CloudBay uses native WinUI controls and Windows Community Toolkit SettingsCard and SettingsExpander controls. Their theme, focus, keyboard, hover, and adaptive layout behavior provide the Windows 11 foundation. The interface follows the page and navigation patterns shown in the owner's references. The app's identity comes from the sync status, activity history, and recognizable folder icons.

## Structure

- Mica Alt is the window base. The native commanding fill softens the wallpaper tint in the title bar and sidebar. NavigationView's native content fill creates one continuous foreground surface from below the title bar through the body, with an 8 epx upper-left corner separating it from navigation.
- The page title occupies a separate, fixed header above the scrollable task content, within that continuous foreground surface. Detail-page back navigation and the title share this same header; scrolling never loses the page identity. The title does not sit inside an individual card or an additional outlined box.
- Rows and groups inside the body use native `CardBackgroundFillColorDefaultBrush`, restrained `CardStrokeColorDefaultBrush` contours, and the native 4 epx `ControlCornerRadius`. The Account form uses the body directly, with no second card wrapping its inputs. The tray and dialogs retain the Windows 8 epx treatment.
- Section headings describe one useful group, and controls are placed inside that group's surface. Native Windows text and control sizes are retained; comfortable page gutters, group padding, and section spacing establish the hierarchy.
- Settings opens a home page with two balanced category groups: Backup and sync, and This app. The current cloud account remains a separate identity and action. Focused detail pages expose the account form, file availability, transfers and power, appearance, startup, and support when selected. Planned providers and modes have one discoverable entry rather than repeated advertisements. Native back navigation connects the two levels; breadcrumbs are reserved for deeper hierarchies.
- Backup uses recognizable Windows folder tiles, accessible switches, and paths in tooltips. Less common folders remain discoverable on demand.
- Pages share consistent outer gutters and adapt to the available window width. Activity offers All, In progress, Queued, and History views. Uploads and downloads show their filenames, relative folder, measured speed, and progress before completed history. The header summarizes current upload and download rates independently.
- The Activity presentation is bounded while its queue count covers the complete workload. Changing a filter resets to its first visible file; ordinary progress updates preserve existing row containers and scroll position. Acceptance checks visible filenames intersecting the viewport and reaching the last displayed queued row, rather than accepting an item count behind a blank virtualized list.
- The tray places up to three live transfers first, with per-file and total directional speeds. Its queue action opens Activity, and recent history follows beneath. Quiet, queued, paused, and verifying files do not display an invented transfer rate. The footer remains reachable as content scrolls.
- The tray uses a compact layout: current state, relevant progress, recent activity when present, and quick actions. Its height follows the visible content.
- The tray body scrolls when the work area limits its height. Footer actions stay reachable and stack when scaled text no longer fits beside each other. Connect opens Account directly; attention opens Overview's review action.
- The tray synchronizes its native frame with the actual XAML theme. Windows 11 DWM suppresses the flyout's outer stroke while retaining native rounded corners and shadow; contrast themes keep the system boundary. The frame updates after theme, activation, and contrast-setting changes.

## Interaction

Account setup remains an explicit operation. Preference changes apply immediately through a field-specific controller update that preserves account and folder ownership. Numeric values and exclusions commit only when valid. Failed saves remain visible and recoverable.

Exclusions use one shared editor reached from Folder backup or Files and storage. The rule list shows readable names, scope, and an enable switch; editing and removal sit in each row's options menu. Add exclusion offers Windows file and folder pickers, file types, plain-text name matching, and an advanced token builder. Scope and an optional example check make advanced behavior reviewable before saving. A failed save retains the complete draft. Existing legacy patterns remain unchanged unless the user explicitly edits them.

Folder backup separates whole-cloud account shortcuts from personal-folder setup. Pictures offers matching Pictures locations per account, with a native Browse action for another source. Setup uses visible mutually exclusive choices for bringing files or changing the Windows location without a transfer. Copy retains originals; Move explains source-cloud deletions before the final review. No-transfer hides irrelevant source and transfer controls and never promises to empty an existing cloud folder. Stopping backup reviews its chosen Windows destination and offers native freeing of downloaded copies while retaining B2 files.

Each folder owns its pending ring, status and result warning. Other folder switches remain available while a transfer runs; waiting choices and mutations show queued status. The checked switch continues to reflect the effective Windows mapping until the operation commits. Dialogs share one queue. Retained-original warnings stay on the affected tile even if a later folder succeeds.

The fixed page header and task content use a single spacing interval. Closed notices take no layout space, quiet status cards size to their contents, and Account fields retain a comfortable maximum width. These changes adjust composition while retaining native text and control sizes.

Settings routes retain their instantiated editors, so returning Home and opening another category preserves drafts. Back restores focus to the originating action after layout. Account actions open the editor directly and focus the bucket field for setup or the application-key field for an existing connection. Backup tiles use cached Windows mappings and show attention when another app changes a location; they cannot advertise that external location as a protected CloudBay folder.

Fluent icons communicate actions consistently. Personal folders use Windows' own colored icons, retrieved from their Known Folder definitions; custom folders use the Windows stock folder icon. The icon reader opens only local Windows resource files, frees native handles after rendering, and caches a bounded set of images on each UI thread. Missing resources retain a Fluent icon fallback.

Native animated icons and expander chevrons respond to interaction. Transfer progress communicates ongoing work; animation respects Windows accessibility preferences and does not continue unnecessarily in hidden windows.

## Review

Review the five pages together at the same width and theme, then narrow layouts, both themes, tray states, expanded controls, long activity history, unsaved editors, and provider search. Fresh capture processes isolate each theme. Rendered XAML images do not capture the desktop Mica compositor, and transparent title-bar areas need their real window backdrop for correct visual contrast. Popup/dialog capture limitations must be identified separately from product defects.

## Reference composition

The owner's Windows Settings Apps page, Store Library, Files Home, and PowerToys Home references define the visual review criteria. Their layouts use surfaces to group useful content; applying native controls alone does not establish this hierarchy.

| Element | Placement and treatment |
| --- | --- |
| Page title | Fixed header within the continuous foreground body, above individual cards; native Title style, 28 epx semibold. |
| Settings entry | Regular 14 epx label, 12 epx secondary description, neutral 20 epx action icon, and a comfortable row rather than an oversized tile. |
| Entry geometry | Native 4 epx control corners; theme fill distinguishes entries, with subordinate contours. |
| Shared content group | Native 4 epx in-page corners and one group heading; internal rows do not repeat outlined containers. |
| Detail section | BodyStrong heading above related native SettingsCard/SettingsExpander rows, with close spacing within the group and larger spacing between groups. |
| File or folder identity | Windows' colored object icon; a larger icon is reserved for recognizable content, rather than applied to every setting. |
| Account setup | A clear account identity and next action; connection fields appear only on the Account detail page. |

These proportions follow the [Windows type ramp](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography), [content spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics), and [control geometry](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry). The [WinUI Gallery Settings source](https://github.com/microsoft/WinUI-Gallery/blob/main/WinUIGallery/Pages/SettingsPage.xaml) places the page title in an Auto grid row and the scrolling settings in a separate star row, with close spacing inside sections. [PowerToys' General source](https://github.com/microsoft/PowerToys/blob/main/src/settings-ui/Settings.UI/SettingsXAML/Views/GeneralPage.xaml) demonstrates focused SettingsGroup sections and progressively disclosed SettingsExpander controls; [its dashboard source](https://github.com/microsoft/PowerToys/blob/main/src/settings-ui/Settings.UI/SettingsXAML/Views/DashboardPage.xaml) provides a reference for purposeful shared home surfaces. CloudBay follows Microsoft's [Mica Alt layering guidance](https://learn.microsoft.com/en-us/windows/apps/design/style/mica#app-layering-with-mica-alt): the native commanding fill sits over Mica Alt, and NavigationView's contiguous content fill sits above that layer. The page header and task cards share this body, as in the owner's Store reference.

## Microsoft references

- [App settings](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings)
- [NavigationView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)
- [BreadcrumbBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/breadcrumbbar)
- [SettingsCard](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/windows/settingscontrols/settingscard)
- [Mica and Mica Alt layers](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)
- [Layering and elevation](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/layering)
- [Geometry](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry)
- [Typography](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography)
- [Color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color)
- [Motion](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion)
- [AnimatedIcon and animation accessibility](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.animatedicon?view=windows-app-sdk-1.8)
