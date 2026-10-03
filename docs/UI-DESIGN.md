# Windows 11 interface

CloudBay uses native WinUI controls and Windows Community Toolkit SettingsCard and SettingsExpander controls. Their theme, focus, keyboard, hover, and adaptive layout behavior provide the Windows 11 foundation. The app's identity comes from the sync status, activity history, and folder organization.

## Structure

- Mica Alt is the window base. Navigation uses the commanding layer; NavigationView supplies the content layer once.
- Page titles and section headings sit on the page above their content. A status or activity section does not need a large enclosing card.
- Settings and backup controls use compact native rows: icon, label and description, then an action or switch. Related rows share a section heading. Advanced options expand on demand.
- Pages share a bounded content width and consistent outer gutters. Activity remains a bounded, scrollable list.
- The tray uses a compact layout: current state, relevant progress, recent activity when present, and quick actions. Its height follows the visible content.

## Interaction

Account setup remains an explicit operation. Preference changes apply immediately through a field-specific controller update that preserves account and folder ownership. Numeric values and exclusions commit only when valid. Failed saves remain visible and recoverable.

Fluent icons communicate actions consistently. Personal folders use Windows' own colored icons, retrieved from their Known Folder definitions; custom folders use the Windows stock folder icon. The icon reader opens only local Windows resource files, frees native handles after rendering, and caches a bounded set of images on each UI thread. Missing resources retain a Fluent icon fallback.

Native animated icons and expander chevrons respond to interaction. Transfer progress communicates ongoing work; animation respects Windows accessibility preferences and does not continue unnecessarily in hidden windows.

## Review

Review the five pages together at the same width and theme, then narrow layouts, both themes, tray states, expanded controls, long activity history, unsaved editors, and provider search. Fresh capture processes isolate each theme. Rendered XAML images do not capture the desktop Mica compositor, and transparent title-bar areas need their real window backdrop for correct visual contrast. Popup/dialog capture limitations must be identified separately from product defects.

## Microsoft references

- [App settings](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings)
- [SettingsCard](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/windows/settingscontrols/settingscard)
- [Mica and Mica Alt layers](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)
- [Layering and elevation](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/layering)
- [Geometry](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry)
- [Typography](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography)
- [Color](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/color)
- [Motion](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion)
- [AnimatedIcon and animation accessibility](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.animatedicon?view=windows-app-sdk-1.8)
