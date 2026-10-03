# Windows 11 interface

CloudBay uses native WinUI controls and Windows Community Toolkit SettingsCard and SettingsExpander controls. Their theme, focus, keyboard, hover, and adaptive layout behavior provide the Windows 11 foundation. The interface follows the page and navigation patterns shown in the owner's references. The app's identity comes from the sync status, activity history, and recognizable folder icons.

## Structure

- Mica Alt is the window base. Navigation uses the commanding layer; NavigationView supplies the content layer once.
- Foreground rows and groups have a distinct soft fill through native `CardBackgroundFillColorDefaultBrush`, with restrained `CardStrokeColorDefaultBrush` contours. This separates their content from the wallpaper-tinted window foundation. NavigationView retains its native geometry; the page is not wrapped in another painted frame.
- Page titles and section headings provide hierarchy above purposeful content surfaces. Generous spacing and comfortable controls take priority over compactness.
- Settings opens a home page with functional categories. Focused detail pages expose the account form, sync preferences, network options, appearance, and support when selected. Native back navigation connects the two levels; breadcrumbs are reserved for deeper hierarchies.
- Backup uses recognizable Windows folder tiles, accessible switches, and paths in tooltips. Less common folders remain discoverable on demand.
- Pages share consistent outer gutters and adapt to the available window width. Activity remains a bounded, scrollable list.
- The tray uses a compact layout: current state, relevant progress, recent activity when present, and quick actions. Its height follows the visible content.
- The tray body scrolls when the work area limits its height. Footer actions stay reachable and stack when scaled text no longer fits beside each other. Connect opens Account directly; attention opens Overview's review action.

## Interaction

Account setup remains an explicit operation. Preference changes apply immediately through a field-specific controller update that preserves account and folder ownership. Numeric values and exclusions commit only when valid. Failed saves remain visible and recoverable.

Settings routes retain their instantiated editors, so returning Home and opening another category preserves drafts. Back restores focus to the originating action after layout. Account actions open the editor directly and focus the bucket field for setup or the application-key field for an existing connection. Backup tiles use cached Windows mappings and show attention when another app changes a location; they cannot advertise that external location as a protected CloudBay folder.

Fluent icons communicate actions consistently. Personal folders use Windows' own colored icons, retrieved from their Known Folder definitions; custom folders use the Windows stock folder icon. The icon reader opens only local Windows resource files, frees native handles after rendering, and caches a bounded set of images on each UI thread. Missing resources retain a Fluent icon fallback.

Native animated icons and expander chevrons respond to interaction. Transfer progress communicates ongoing work; animation respects Windows accessibility preferences and does not continue unnecessarily in hidden windows.

## Review

Review the five pages together at the same width and theme, then narrow layouts, both themes, tray states, expanded controls, long activity history, unsaved editors, and provider search. Fresh capture processes isolate each theme. Rendered XAML images do not capture the desktop Mica compositor, and transparent title-bar areas need their real window backdrop for correct visual contrast. Popup/dialog capture limitations must be identified separately from product defects.

## Reference composition

The owner's Windows Settings Apps page, Store Library, Files Home, and PowerToys Home references define the visual review criteria. Their layouts use surfaces to group useful content; applying native controls alone does not establish this hierarchy.

| Element | Placement and treatment |
| --- | --- |
| Page title | Outside the content surfaces; native Title style, 28 epx semibold. |
| Settings entry | Regular 14 epx label, 12 epx secondary description, neutral 20 epx action icon, and a comfortable row rather than an oversized tile. |
| Entry geometry | Native 4 epx control corners; theme fill distinguishes entries, with subordinate contours. |
| Shared content group | 8 epx outer corners and one group heading; internal rows do not repeat outlined containers. |
| Detail section | BodyStrong heading above related native SettingsCard/SettingsExpander rows, with close spacing within the group and larger spacing between groups. |
| File or folder identity | Windows' colored object icon; a larger icon is reserved for recognizable content, rather than applied to every setting. |
| Account setup | A clear account identity and next action; connection fields appear only on the Account detail page. |

These proportions follow the [Windows type ramp](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography), [content spacing](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics), and [control geometry](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry). The [WinUI Gallery Settings source](https://github.com/microsoft/WinUI-Gallery/blob/main/WinUIGallery/Pages/SettingsPage.xaml) provides the grouped detail-row pattern; [PowerToys' dashboard source](https://github.com/microsoft/PowerToys/blob/main/src/settings-ui/Settings.UI/SettingsXAML/Views/DashboardPage.xaml) provides a reference for purposeful shared home surfaces.

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
