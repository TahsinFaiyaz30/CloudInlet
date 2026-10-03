# License sources

Release packaging copies license and notice files unchanged from the exact restored NuGet packages, including .NET and Windows App SDK runtime packs. `App/Licenses/packages.json` records resolved package versions, upstream license metadata, retained files, and their SHA256 hashes. This inventory includes build dependencies declared by the publish manifest; it does not claim that every dependency contributes an executable file.

Some packages declare a license expression without including its text. The packaging script retains their copyright metadata and supplies the corresponding license:

- `Apache-2.0.txt`: [SQLitePCL.raw v2.1.6 upstream license](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.6/LICENSE.TXT), SHA256 `CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30`.
- MIT packages without an included license file use the unchanged .NET runtime MIT license, alongside each package's own copyright metadata.
- `CsWinRT-2.2.0-LICENSE.txt`: [CsWinRT license at commit 8649ee3e](https://github.com/microsoft/CsWinRT/blob/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/LICENSE), SHA256 `9906940F61B1F0B533FA7D99BAF55178B2808FBE113EA51DFBFAD8572CCD5F2B`. The shipped WinRT runtime reports this source commit. This notice accompanies the Windows SDK .NET projection assemblies; the SDK package's original license URL remains in the manifest.

These files document dependency terms; they do not alter them. Future dependency upgrades require reviewing packages with changed or unfamiliar license declarations.
